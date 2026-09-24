// MocopiRecorderApp.cs
// メイン画面：mocopi の IP・ポート・保存先ファイル名の入力と、受信開始／録画／一時停止／停止ボタン。
// avatar を設定しておくと、受信データで背景のアバターも動かす（公式 MocopiSimpleReceiver と同じく
// 受信スレッドから InitializeSkeleton / UpdateSkeleton を呼ぶ：MocopiSimpleReceiver.cs:147-148）。
// 「接続」中は、背景のアバターの姿勢からマッスルを取り出して PolyLing のライブ受信へ WebSocket で送る
// （MocopiAvatar は Update で姿勢を当てるので、LateUpdate で読む）。録画とは独立。
// 「サーバモード」をオンにすると、自分が WebSocket で待ち受け、つないできた全接続へ同じものを送る。

using System.IO;
using System.Net;
using Mocopi.Receiver;
using UnityEngine;

namespace MocopiToPolyLing
{
    public sealed class MocopiRecorderApp : MonoBehaviour
    {
        [Tooltip("背景で動かすアバター（なくてもよい）")]
        public MocopiAvatar avatar;

        [Tooltip("mocopi（送信側）の IP。空欄はすべて受ける")]
        public string mocopiIp = "";

        [Tooltip("受信ポート（mocopi アプリの送信先ポート）")]
        public int port = 12351;

        [Tooltip("保存先ファイル名。相対指定は画面に出る保存フォルダが基準")]
        public string fileName = "mocopi_take";

        [Tooltip("PolyLing（ライブ受信）の IP")]
        public string polyLingIp = "127.0.0.1";

        [Tooltip("PolyLing（ライブ受信）のポート")]
        public int polyLingPort = 12361;

        [Tooltip("サーバモード（オン: 自分が待ち受ける／オフ: PolyLing へ接続する）")]
        public bool serverMode = false;

        [Tooltip("待ち受けのポート")]
        public int listenPort = 12361;

        [Tooltip("待ち受け時に LAN 上の別の PC からも受ける")]
        public bool listenLan = false;

        /// <summary>mocopi の受信がこの秒数途切れたら送信を止める（再開すれば送る）。</summary>
        private const float SendIdleTimeout = 0.5f;

        private string _portText;
        private string _polyLingPortText;
        private string _listenPortText;
        private readonly MocopiMuscleSender _sender = new MocopiMuscleSender();
        private long  _lastAccepted = -1;
        private float _lastAcceptedChange = -1f;
        private readonly MocopiLiveReceiver _receiver = new MocopiLiveReceiver();
        private readonly MocopiMotionRecorder _recorder = new MocopiMotionRecorder();
        private string _message = "";
        private string _baseDir;

        private GUIStyle _label, _field, _button, _toggle;
        private Rect _window = new Rect(10, 10, 560, 400);

        private void Awake()
        {
            _portText = port.ToString();
            _polyLingPortText = polyLingPort.ToString();
            _listenPortText = listenPort.ToString();
            _baseDir = Path.Combine(Application.persistentDataPath, "MocopiClips");
            _receiver.SkeletonDefinition += _recorder.OnSkeletonDefinition;
            _receiver.FrameData += _recorder.OnFrame;
            if (avatar != null)
            {
                _receiver.SkeletonDefinition += avatar.InitializeSkeleton;
                _receiver.FrameData += ForwardFrameToAvatar;
            }
        }

        private void OnDestroy()
        {
            _receiver.Dispose();
            _sender.Dispose();
        }

        private void LateUpdate()
        {
            if (!_sender.CanSend) return;

            // mocopi から新しいデータが来ているあいだだけ送る
            long accepted = _receiver.AcceptedPackets;
            if (accepted != _lastAccepted)
            {
                _lastAccepted = accepted;
                _lastAcceptedChange = Time.unscaledTime;
            }
            if (!_recorder.HasSkeleton || _lastAcceptedChange < 0f
                || Time.unscaledTime - _lastAcceptedChange > SendIdleTimeout) return;

            _sender.Send(Time.realtimeSinceStartupAsDouble);
        }

        private void ConnectPolyLing()
        {
            if (!int.TryParse(_polyLingPortText.Trim(), out int p))
            {
                _message = "PolyLing のポート番号が正しくありません。";
                return;
            }
            polyLingPort = p;
            var animator = avatar != null ? avatar.GetComponent<Animator>() : null;
            _message = _sender.Connect(animator, polyLingIp, p)
                ? $"PolyLing へ接続しています（{_sender.Destination}）"
                : _sender.LastError;
        }

        private void StartListen()
        {
            if (!int.TryParse(_listenPortText.Trim(), out int p))
            {
                _message = "待ち受けのポート番号が正しくありません。";
                return;
            }
            listenPort = p;
            var animator = avatar != null ? avatar.GetComponent<Animator>() : null;
            _message = _sender.Listen(animator, p, listenLan)
                ? $"待ち受けを始めました（{_sender.Destination}）"
                : _sender.LastError;
        }

        private string SenderStateText()
        {
            switch (_sender.State)
            {
                case SenderState.Listening:  return $"待ち受け中（接続 {_sender.ClientCount}）";
                case SenderState.Connected:  return "接続中";
                case SenderState.Connecting: return "接続しています…";
                default:                     return _sender.IsActive ? "未接続" : "切断";
            }
        }

        private void DisconnectPolyLing()
        {
            bool wasListening = _sender.IsListening;
            _sender.Disconnect();
            _message = wasListening ? "待ち受けを止めました" : "PolyLing への送信を止めました";
        }

        private void ForwardFrameToAvatar(int frameId, float timestamp, double unixTime, int[] ids,
            float[] rx, float[] ry, float[] rz, float[] rw, float[] px, float[] py, float[] pz)
        {
            var a = avatar;
            if (a == null) return;
            a.UpdateSkeleton(frameId, timestamp, unixTime, 0, 0, 0, 0, 0, false, ids, rx, ry, rz, rw, px, py, pz);
        }

        // ---------------- 操作 ----------------

        private void StartReceive()
        {
            if (!int.TryParse(_portText.Trim(), out int p) || p < 1 || p > 65535)
            {
                _message = "ポート番号が正しくありません。";
                return;
            }
            IPAddress filter = null;
            string ip = mocopiIp.Trim();
            if (ip.Length > 0 && !IPAddress.TryParse(ip, out filter))
            {
                _message = "IP アドレスが正しくありません。";
                return;
            }
            port = p;
            _message = _receiver.Start(p, filter)
                ? $"受信を始めました（ポート {p}, 送信元 {(filter == null ? "指定なし" : filter.ToString())}）"
                : _receiver.LastError;
        }

        private void Record()
        {
            if (!_receiver.IsRunning) StartReceive();
            if (!_receiver.IsRunning) return;
            _recorder.Record();
            _message = "録画中";
        }

        private void Pause()
        {
            _recorder.Pause();
            _message = "一時停止中（録画ボタンで再開）";
        }

        private void StopAndSave()
        {
            _recorder.Stop(out var skel, out var frames);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                _message = "保存先ファイル名が空です。記録は破棄しました。";
                return;
            }
            MocopiClipBuilder.ResolvePaths(fileName, _baseDir, out string motionPath, out string csvPath);
            var r = MocopiClipBuilder.Save(skel, frames, motionPath, csvPath);
            _message = r.Ok ? $"{r.Message}\n{r.MotionPath}\n{r.RestCsvPath}" : r.Message;
        }

        // ---------------- 画面 ----------------

        private void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 18 };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 20, fixedHeight = 40 };
            _toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 18 };
        }

        private void OnGUI()
        {
            EnsureStyles();
            _window = GUILayout.Window(0x4D4F4350, _window, DrawWindow, "mocopi → PolyLing モーション");
        }

        private void DrawWindow(int id)
        {
            var state = _recorder.State;
            bool idle = state == RecorderState.Stopped;

            GUI.enabled = idle;
            GUILayout.BeginHorizontal();
            GUILayout.Label("mocopi IP", _label, GUILayout.Width(120));
            mocopiIp = GUILayout.TextField(mocopiIp, _field, GUILayout.Width(220));
            GUILayout.Label("ポート", _label, GUILayout.Width(60));
            _portText = GUILayout.TextField(_portText, _field, GUILayout.Width(90));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("保存先", _label, GUILayout.Width(120));
            fileName = GUILayout.TextField(fileName, _field, GUILayout.Width(390));
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            GUILayout.Label("保存フォルダ: " + _baseDir, _label);

            bool sending = _sender.IsActive;
            GUI.enabled = !sending;
            serverMode = GUILayout.Toggle(serverMode, "サーバモード", _toggle);
            GUILayout.BeginHorizontal();
            if (serverMode)
            {
                GUILayout.Label("待ち受けポート", _label, GUILayout.Width(150));
                _listenPortText = GUILayout.TextField(_listenPortText, _field, GUILayout.Width(90));
                listenLan = GUILayout.Toggle(listenLan, "LAN から受ける", _toggle);
            }
            else
            {
                GUILayout.Label("PolyLing IP", _label, GUILayout.Width(120));
                polyLingIp = GUILayout.TextField(polyLingIp, _field, GUILayout.Width(220));
                GUILayout.Label("ポート", _label, GUILayout.Width(60));
                _polyLingPortText = GUILayout.TextField(_polyLingPortText, _field, GUILayout.Width(90));
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(serverMode ? "待ち受け開始" : "接続", _button))
            {
                if (serverMode) StartListen(); else ConnectPolyLing();
            }
            GUI.enabled = sending;
            if (GUILayout.Button(serverMode ? "待ち受け停止" : "切断", _button)) DisconnectPolyLing();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = idle;
            if (GUILayout.Button(_receiver.IsRunning ? "受信し直す" : "受信開始", _button)) StartReceive();
            GUI.enabled = state != RecorderState.Recording;
            if (GUILayout.Button(state == RecorderState.Paused ? "● 再開" : "● 録画", _button)) Record();
            GUI.enabled = state == RecorderState.Recording;
            if (GUILayout.Button("一時停止", _button)) Pause();
            GUI.enabled = !idle;
            if (GUILayout.Button("■ 停止・保存", _button)) StopAndSave();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            string st = state == RecorderState.Recording ? "録画中" : state == RecorderState.Paused ? "一時停止" : "停止";
            GUILayout.Label(
                $"状態: {st}   受信: {(_receiver.IsRunning ? "中" : "停止")}   骨定義: {(_recorder.HasSkeleton ? "あり" : "なし")}\n" +
                $"最後の送信元: {_receiver.LastSender}   受理 {_receiver.AcceptedPackets} / 除外 {_receiver.RejectedPackets}\n" +
                $"記録: {_recorder.FrameCount} フレーム, {_recorder.Duration:F2} 秒\n" +
                $"送信: {SenderStateText()} {_sender.Destination}   送信 {_sender.SentCount} / 見送り {_sender.SkippedCount}" +
                (string.IsNullOrEmpty(_sender.LastError) ? "" : "\n送信: " + _sender.LastError),
                _label);
            if (!string.IsNullOrEmpty(_message)) GUILayout.Label(_message, _label);

            GUI.DragWindow();
        }
    }
}
