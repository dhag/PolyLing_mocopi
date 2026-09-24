// MocopiMuscleSender.cs
// 背景のアバター（Humanoid）の姿勢からマッスルを取り出し、WebSocket で送る。
// JSON の形は PolyLing 側の Poly_Ling.Motion.MotionLiveJson（同じクラスで組み立てる）。
// 通信は com.haglib.net_duplexchannel（PolyLing の待ち受けと同じ部品・同じ封筒）。
//
// ■ 送り方は 2 通り
//   接続する（Connect）: WebSocketDuplexClient で PolyLing のライブ受信へつなぎ、その 1 本へ送る。
//   待ち受ける（Listen）: WebSocketDuplexServer で待ち受け、つないできた全接続へ送る。
//                         lan=false は同じ PC の中だけ、lan=true は LAN 上の別の PC からも受ける
//                         （PolyLing の MotionLiveHandler.Accept と同じ指定）。
//   受け取ったものは使わない（送るだけ）。
//
// ■ 送らないもの
//   mocopi が動かす Humanoid 骨（MocopiAvatar.HUMAN_BONE_NAME_TO_MOCOPI_BONE_ID の 22 本）のマッスルだけを
//   入れる。指・目・顎は入れない（PolyLing 側で当てない）。
//   骨の番号は HumanTrait.BoneName の空白を抜いた名前で引く（PolyLing の UnityClipApplier と同じ引き方）。
//
// ■ 送信待ちを溜めない
//   部品は 1 接続の送信を 1 件ずつ行う。前の送信が終わっていない接続には、そのフレームを送らずに捨てる。
//   待ち受け時は接続ごとに判定する（PolyLing の MotionLiveHandler.Relay と同じ扱い）。
//
// ■ 呼び出し
//   Connect / Listen / Disconnect / Send はメインスレッドから呼ぶ（HumanPoseHandler はメインスレッドで使う）。
//   接続の完了・失敗は State と LastError で見る（別スレッドで書き換わる）。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HagLib.NET.Duplex;
using Mocopi.Receiver;
using Poly_Ling.Motion;
using UnityEngine;

namespace MocopiToPolyLing
{
    public enum SenderState { Disconnected, Connecting, Connected, Listening }

    public sealed class MocopiMuscleSender : IDisposable
    {
        public SenderState State => _server != null ? SenderState.Listening
                                  : _client == null ? SenderState.Disconnected
                                  : _client.IsConnected ? SenderState.Connected
                                  : _connecting ? SenderState.Connecting : SenderState.Disconnected;
        public bool   IsActive => _client != null || _server != null;   // 接続中・接続しようとしている・待ち受け中
        public bool   IsListening => _server != null;
        public int    ClientCount => _server?.Clients.Length ?? 0;
        /// <summary>今送れる相手がいるか（接続済み、または待ち受け中で接続が 1 本以上）。</summary>
        public bool   CanSend => _server != null ? ClientCount > 0 : (_client != null && _client.IsConnected);
        public int    SentCount => _sent;
        public int    SkippedCount => _skipped;
        public string LastError => _lastError;
        public string Destination { get; private set; } = "";

        private WebSocketDuplexClient _client;
        private WebSocketDuplexServer _server;
        private volatile bool         _connecting;
        private volatile bool         _sending;
        private readonly HashSet<string> _sendingIds = new HashSet<string>();
        private readonly object       _sendingLock = new object();
        private volatile string       _lastError = "";
        private int                   _sent;
        private int                   _skipped;

        private HumanPoseHandler _poseHandler;
        private HumanPose        _pose;
        private string[]         _muscleNames;
        private readonly MotionLiveFrame _frame = new MotionLiveFrame();
        private uint             _seq;

        /// <summary>接続を始める。入力の誤りは false（LastError に理由）。接続の成否は State で見る。</summary>
        public bool Connect(Animator animator, string ip, int port)
        {
            Disconnect();
            _lastError = "";
            if (!CheckAnimator(animator) || !CheckPort(port)) return false;
            ip = (ip ?? "").Trim();
            if (ip.Length == 0)
            {
                _lastError = "PolyLing の IP アドレスが空です。";
                return false;
            }
            Uri uri;
            try
            {
                uri = new UriBuilder("ws", ip, port, "/").Uri;
            }
            catch (UriFormatException e)
            {
                _lastError = "PolyLing の IP アドレスが正しくありません: " + e.Message;
                return false;
            }

            PreparePose(animator);
            Destination = uri.ToString();

            var client = new WebSocketDuplexClient { DefaultFrame = WebSocketFrameKind.Text };
            client.OnDisconnected += _ => { if (_client == client) _lastError = "PolyLing との接続が切れました。"; };
            _client = client;
            _connecting = true;
            client.ConnectAsync(uri).ContinueWith(t =>
            {
                _connecting = false;
                if (t.IsFaulted && _client == client)
                    _lastError = "PolyLing に接続できません: " + (t.Exception?.GetBaseException().Message ?? "");
            });
            return true;
        }

        /// <summary>port で待ち受けを始める。lan=true なら LAN 上の別の PC からも受ける。失敗は false（LastError に理由）。</summary>
        public bool Listen(Animator animator, int port, bool lan)
        {
            Disconnect();
            _lastError = "";
            if (!CheckAnimator(animator) || !CheckPort(port)) return false;

            var server = new WebSocketDuplexServer
            {
                DefaultFrame    = WebSocketFrameKind.Text,
                ListenAddresses = lan ? WebSocketDuplexServer.AnyAddresses : WebSocketDuplexServer.LoopbackAddresses,
            };
            server.OnClientDisconnected += ch => { if (ch != null) lock (_sendingLock) _sendingIds.Remove(ch.Id); };
            try
            {
                _ = server.StartAsync(port);    // 待ち受けは同期で開始し、失敗は例外で返る
            }
            catch (Exception e)
            {
                try { _ = server.StopAsync(); } catch { }
                _lastError = $"ポート {port} を開けません: {e.Message}";
                return false;
            }

            PreparePose(animator);
            Destination = $"ポート {port}（{(lan ? "LAN から受ける" : "この PC のみ")}）";
            _server = server;
            return true;
        }

        public void Disconnect()
        {
            var c = _client;
            _client = null;
            _connecting = false;
            if (c != null) c.CloseAsync().ContinueWith(_ => c.Dispose());

            var s = _server;
            _server = null;
            if (s != null) s.StopAsync().ContinueWith(_ => s.Dispose());
            lock (_sendingLock) _sendingIds.Clear();

            _poseHandler?.Dispose();
            _poseHandler = null;
        }

        public void Dispose() => Disconnect();

        /// <summary>今のアバターの姿勢を 1 フレーム送る。送れる相手がいなければ送らない。</summary>
        public void Send(double time)
        {
            if (_poseHandler == null) return;
            var server = _server;
            var client = _client;
            if (server == null && (client == null || !client.IsConnected)) return;
            if (server == null && _sending) { _skipped++; return; }

            string json = BuildJson(time);

            if (server != null)
            {
                foreach (var ch in server.Clients)
                {
                    if (ch == null) continue;
                    string id = ch.Id;
                    lock (_sendingLock)
                    {
                        if (!_sendingIds.Add(id)) { _skipped++; continue; }   // 前の送信中：このフレームは捨てる
                    }
                    try
                    {
                        // 送信で封筒の Id・種別が書き換わるので、接続ごとに作る
                        ch.SendAsync(TypedPayload.FromJson(json).ToMessage()).ContinueWith(t =>
                        {
                            if (t.IsFaulted) _lastError = t.Exception?.GetBaseException().Message ?? "";
                            else System.Threading.Interlocked.Increment(ref _sent);
                            lock (_sendingLock) _sendingIds.Remove(id);
                        }, TaskContinuationOptions.ExecuteSynchronously);
                    }
                    catch (Exception e)
                    {
                        _lastError = e.Message;
                        lock (_sendingLock) _sendingIds.Remove(id);
                    }
                }
                return;
            }

            _sending = true;
            Task task;
            try
            {
                task = client.SendAsync(TypedPayload.FromJson(json).ToMessage());
            }
            catch (Exception e)
            {
                _sending = false;
                _lastError = e.Message;
                return;
            }
            task.ContinueWith(t =>
            {
                if (t.IsFaulted) _lastError = t.Exception?.GetBaseException().Message ?? "";
                else System.Threading.Interlocked.Increment(ref _sent);
                _sending = false;
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        // ---------------- 内部 ----------------

        private bool CheckAnimator(Animator animator)
        {
            if (animator != null && animator.avatar != null && animator.avatar.isHuman) return true;
            _lastError = "Humanoid のアバターがありません。";
            return false;
        }

        private bool CheckPort(int port)
        {
            if (port >= 1 && port <= 65535) return true;
            _lastError = "ポート番号が正しくありません。";
            return false;
        }

        private void PreparePose(Animator animator)
        {
            _poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            _pose = new HumanPose();
            _muscleNames = HumanTrait.MuscleName;

            int n = HumanTrait.MuscleCount;
            _frame.EnsureCount(n);
            Array.Clear(_frame.Valid, 0, n);
            foreach (string key in MocopiAvatar.HUMAN_BONE_NAME_TO_MOCOPI_BONE_ID.Keys)
            {
                int bi = BoneIndexOf(key);
                if (bi < 0) continue;
                for (int dof = 0; dof < 3; dof++)
                {
                    int mi = HumanTrait.MuscleFromBone(bi, dof);
                    if (mi >= 0 && mi < n) _frame.Valid[mi] = true;
                }
            }

            _seq = 0;
            _sent = 0;
            _skipped = 0;
            _sending = false;
        }

        private string BuildJson(double time)
        {
            _poseHandler.GetHumanPose(ref _pose);
            var m = _pose.muscles;
            int n = _frame.Muscles.Length;
            for (int i = 0; i < n; i++)
                _frame.Muscles[i] = (m != null && i < m.Length) ? m[i] : 0f;

            _frame.Seq     = _seq++;
            _frame.Time    = time;
            _frame.HasRoot = true;
            _frame.RootT   = _pose.bodyPosition;
            _frame.RootQ   = _pose.bodyRotation;
            return MotionLiveJson.Build(_frame, _muscleNames);
        }

        private static int BoneIndexOf(string key)
        {
            var names = HumanTrait.BoneName;
            for (int i = 0; i < names.Length; i++)
                if (names[i].Replace(" ", string.Empty) == key) return i;
            return -1;
        }
    }
}
