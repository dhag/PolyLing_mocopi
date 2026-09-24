// MocopiLiveReceiver.cs
// mocopi の UDP を 1 名分だけ受ける。送信元 IP を指定したときはそれ以外を捨てる。
// 解析は mocopi 公式の Sony.MMF.MocopiMotionFormat を使う（MocopiUdpReceiver.cs:309-393 と同じ手順）。
// 公式 MocopiUdpReceiver は送信元 IP をイベントに渡さないため、受信部だけ自前で持つ。
// イベントは受信スレッドから呼ばれる。

using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Sony.MMF;
using UnityEngine;

namespace MocopiToPolyLing
{
    public sealed class MocopiLiveReceiver : IDisposable
    {
        /// <summary>骨定義（boneIds, parentIds, rot xyzw, pos xyz）。値は mocopi 座標のまま。</summary>
        public event Action<int[], int[], float[], float[], float[], float[], float[], float[], float[]> SkeletonDefinition;

        /// <summary>フレーム（frameId, timestamp[秒], unixTime, boneIds, rot xyzw, pos xyz）。値は mocopi 座標のまま。</summary>
        public event Action<int, float, double, int[], float[], float[], float[], float[], float[], float[], float[]> FrameData;

        public int Port { get; private set; }
        public IPAddress SenderFilter { get; private set; }
        public bool IsRunning => _thread != null;

        /// <summary>最後に受け取ったパケットの送信元（フィルタで捨てたものも含む）。</summary>
        public string LastSender => _lastSender;
        public string LastError => _lastError;
        public long AcceptedPackets => Interlocked.Read(ref _accepted);
        public long RejectedPackets => Interlocked.Read(ref _rejected);

        private UdpClient _client;
        private Thread _thread;
        private volatile bool _stop;
        private volatile string _lastSender = "";
        private volatile string _lastError = "";
        private long _accepted;
        private long _rejected;

        /// <param name="port">受信ポート</param>
        /// <param name="senderFilter">送信元 IP。null はすべて受ける。</param>
        public bool Start(int port, IPAddress senderFilter)
        {
            Stop();
            Port = port;
            SenderFilter = senderFilter;
            _lastError = "";
            _lastSender = "";
            Interlocked.Exchange(ref _accepted, 0);
            Interlocked.Exchange(ref _rejected, 0);
            try
            {
                _client = new UdpClient(port);
            }
            catch (SocketException e)
            {
                _lastError = $"ポート {port} を開けません: {e.Message}";
                _client = null;
                return false;
            }
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "MocopiLiveReceiver" };
            _thread.Start();
            return true;
        }

        public void Stop()
        {
            _stop = true;
            var c = _client;
            _client = null;
            if (c != null) c.Close();          // ブロック中の Receive を抜けさせる
            var t = _thread;
            _thread = null;
            if (t != null && t != Thread.CurrentThread) t.Join(1000);
        }

        public void Dispose() => Stop();

        private void Loop()
        {
            var client = _client;
            while (!_stop && client != null)
            {
                byte[] msg;
                IPEndPoint remote = null;
                try
                {
                    msg = client.Receive(ref remote);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException e)
                {
                    if (_stop) break;
                    _lastError = e.Message;
                    continue;
                }

                _lastSender = remote != null ? remote.Address.ToString() : "";
                var filter = SenderFilter;
                if (filter != null && (remote == null || !remote.Address.Equals(filter)))
                {
                    Interlocked.Increment(ref _rejected);
                    continue;
                }

                try
                {
                    Handle(msg);
                    Interlocked.Increment(ref _accepted);
                }
                catch (Exception e)
                {
                    _lastError = e.Message;
                    Debug.LogException(e);
                }
            }
        }

        private void Handle(byte[] message)
        {
            int n = message.Length;
            if (!MocopiMotionFormat.IsMmfBytes(n, message)) return;

            if (MocopiMotionFormat.IsSkeletonDefinitionBytes(n, message))
            {
                if (MocopiMotionFormat.ConvertBytesToSkeletonDefinition(
                        n, message,
                        out ulong _, out int _, out int size,
                        out IntPtr pIds, out IntPtr pParents,
                        out IntPtr pRx, out IntPtr pRy, out IntPtr pRz, out IntPtr pRw,
                        out IntPtr pPx, out IntPtr pPy, out IntPtr pPz))
                {
                    SkeletonDefinition?.Invoke(
                        Ints(pIds, size), Ints(pParents, size),
                        Floats(pRx, size), Floats(pRy, size), Floats(pRz, size), Floats(pRw, size),
                        Floats(pPx, size), Floats(pPy, size), Floats(pPz, size));
                }
            }
            else if (MocopiMotionFormat.IsFrameDataBytes(n, message))
            {
                if (MocopiMotionFormat.ConvertBytesToFrameData(
                        n, message,
                        out ulong _, out int _,
                        out int frameId, out float timestamp, out double unixTime,
                        out byte _, out byte _, out byte _, out byte _, out byte _, out bool _,
                        out int size,
                        out IntPtr pIds,
                        out IntPtr pRx, out IntPtr pRy, out IntPtr pRz, out IntPtr pRw,
                        out IntPtr pPx, out IntPtr pPy, out IntPtr pPz))
                {
                    FrameData?.Invoke(
                        frameId, timestamp, unixTime,
                        Ints(pIds, size),
                        Floats(pRx, size), Floats(pRy, size), Floats(pRz, size), Floats(pRw, size),
                        Floats(pPx, size), Floats(pPy, size), Floats(pPz, size));
                }
            }
        }

        private static int[] Ints(IntPtr p, int n)
        {
            var a = new int[n];
            if (n > 0) Marshal.Copy(p, a, 0, n);
            return a;
        }

        private static float[] Floats(IntPtr p, int n)
        {
            var a = new float[n];
            if (n > 0) Marshal.Copy(p, a, 0, n);
            return a;
        }
    }
}
