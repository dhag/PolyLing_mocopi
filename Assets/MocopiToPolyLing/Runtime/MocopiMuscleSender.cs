// MocopiMuscleSender.cs
// 背景のアバター（Humanoid）の姿勢からマッスルを取り出し、PolyLing のライブ受信へ WebSocket で送る。
// JSON の形は PolyLing 側の Poly_Ling.Motion.MotionLiveJson（同じクラスで組み立てる）。
// 通信は com.haglib.net_duplexchannel の WebSocketDuplexClient（PolyLing の待ち受けと同じ部品・同じ封筒）。
//
// ■ 送らないもの
//   mocopi が動かす Humanoid 骨（MocopiAvatar.HUMAN_BONE_NAME_TO_MOCOPI_BONE_ID の 22 本）のマッスルだけを
//   入れる。指・目・顎は入れない（PolyLing 側で当てない）。
//   骨の番号は HumanTrait.BoneName の空白を抜いた名前で引く（PolyLing の UnityClipApplier と同じ引き方）。
//
// ■ 送信待ちを溜めない
//   部品は 1 接続の送信を 1 件ずつ行う。前の送信が終わっていないときは、そのフレームを送らずに捨てる。
//
// ■ 呼び出し
//   Connect / Disconnect / Send はメインスレッドから呼ぶ（HumanPoseHandler はメインスレッドで使う）。
//   接続の完了・失敗は State と LastError で見る（別スレッドで書き換わる）。

using System;
using System.Threading.Tasks;
using HagLib.NET.Duplex;
using Mocopi.Receiver;
using Poly_Ling.Motion;
using UnityEngine;

namespace MocopiToPolyLing
{
    public enum SenderState { Disconnected, Connecting, Connected }

    public sealed class MocopiMuscleSender : IDisposable
    {
        public SenderState State => _client == null ? SenderState.Disconnected
                                  : _client.IsConnected ? SenderState.Connected
                                  : _connecting ? SenderState.Connecting : SenderState.Disconnected;
        public bool   IsActive => _client != null;          // 接続中または接続しようとしている
        public int    SentCount => _sent;
        public int    SkippedCount => _skipped;
        public string LastError => _lastError;
        public string Destination { get; private set; } = "";

        private WebSocketDuplexClient _client;
        private volatile bool         _connecting;
        private volatile bool         _sending;
        private volatile string       _lastError = "";
        private volatile int          _sent;
        private volatile int          _skipped;

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
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            {
                _lastError = "Humanoid のアバターがありません。";
                return false;
            }
            ip = (ip ?? "").Trim();
            if (ip.Length == 0)
            {
                _lastError = "PolyLing の IP アドレスが空です。";
                return false;
            }
            if (port < 1 || port > 65535)
            {
                _lastError = "PolyLing のポート番号が正しくありません。";
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

        public void Disconnect()
        {
            var c = _client;
            _client = null;
            _connecting = false;
            if (c != null) c.CloseAsync().ContinueWith(_ => c.Dispose());
            _poseHandler?.Dispose();
            _poseHandler = null;
        }

        public void Dispose() => Disconnect();

        /// <summary>今のアバターの姿勢を 1 フレーム送る。前の送信中・未接続なら送らない。</summary>
        public void Send(double time)
        {
            var client = _client;
            if (client == null || !client.IsConnected || _poseHandler == null) return;
            if (_sending) { _skipped++; return; }

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

            string json = MotionLiveJson.Build(_frame, _muscleNames);
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
                else _sent++;
                _sending = false;
            }, TaskContinuationOptions.ExecuteSynchronously);
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
