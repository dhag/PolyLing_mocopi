// MocopiMotionRecorder.cs
// 受信スレッドから呼ばれ、記録中のフレームだけをバッファへ積む。
// 座標は mocopi 公式と同じ式で Unity 左手系へ直して持つ（MocopiAvatar.cs:1044-1070：x と w を反転）。
// フレームで使う位置はルート骨だけ（公式も親なし骨の位置しか使わない：MocopiAvatar.cs:772-775）。

using System.Collections.Generic;
using UnityEngine;

namespace MocopiToPolyLing
{
    public enum RecorderState { Stopped, Recording, Paused }

    /// <summary>骨定義（Unity 左手系に変換済み、骨 ID で引く）。</summary>
    public sealed class MocopiSkeleton
    {
        public int[] BoneIds;
        public Dictionary<int, int> ParentOf = new Dictionary<int, int>();
        public Dictionary<int, Quaternion> RestLocalRot = new Dictionary<int, Quaternion>();
        public Dictionary<int, Vector3> RestLocalPos = new Dictionary<int, Vector3>();
    }

    /// <summary>1 フレーム分（Unity 左手系に変換済み）。</summary>
    public sealed class MocopiFrame
    {
        public float T;                                   // 記録開始からの秒（一時停止分は詰める）
        public Dictionary<int, Quaternion> LocalRot;      // 骨 ID → ローカル回転
        public Vector3 RootPos;
        public bool HasRootPos;
    }

    public sealed class MocopiMotionRecorder
    {
        /// <summary>mocopi のセンサー周期（MocopiAvatar.cs:215 SENSOR_FPS）。再開直後の間隔に使う。</summary>
        public const float SensorFps = 50f;

        /// <summary>mocopi のルート骨 ID（MocopiAvatar.cs:63 "root"=0、:34 Hips=0）。</summary>
        public const int RootBoneId = 0;

        private readonly object _lock = new object();
        private RecorderState _state = RecorderState.Stopped;
        private MocopiSkeleton _skeleton;
        private List<MocopiFrame> _frames = new List<MocopiFrame>();

        private bool _needRebase;          // 次のフレームで時刻の起点を取り直す
        private float _offset;             // T = timestamp - _offset
        private float _lastT;
        private float _lastTimestamp;

        public RecorderState State { get { lock (_lock) return _state; } }
        public bool HasSkeleton { get { lock (_lock) return _skeleton != null; } }
        public int FrameCount { get { lock (_lock) return _frames.Count; } }
        public float Duration { get { lock (_lock) return _frames.Count > 0 ? _frames[_frames.Count - 1].T : 0f; } }

        public static Quaternion ToUnityRot(float x, float y, float z, float w) => new Quaternion(-x, y, z, -w);
        public static Vector3 ToUnityPos(float x, float y, float z) => new Vector3(-x, y, z);

        // ---------------- 受信スレッドから ----------------

        public void OnSkeletonDefinition(int[] ids, int[] parents,
            float[] rx, float[] ry, float[] rz, float[] rw, float[] px, float[] py, float[] pz)
        {
            var s = new MocopiSkeleton { BoneIds = ids };
            for (int i = 0; i < ids.Length; i++)
            {
                s.ParentOf[ids[i]] = parents[i];
                s.RestLocalRot[ids[i]] = ToUnityRot(rx[i], ry[i], rz[i], rw[i]);
                s.RestLocalPos[ids[i]] = ToUnityPos(px[i], py[i], pz[i]);
            }
            lock (_lock) _skeleton = s;
        }

        public void OnFrame(int frameId, float timestamp, double unixTime, int[] ids,
            float[] rx, float[] ry, float[] rz, float[] rw, float[] px, float[] py, float[] pz)
        {
            lock (_lock)
            {
                if (_state != RecorderState.Recording) return;

                float t;
                if (_needRebase || _frames.Count == 0 || timestamp < _lastTimestamp)
                {
                    // 記録開始・再開直後・送信側の時刻が戻ったとき：直前のキーから 1 周期後に置く
                    t = _frames.Count == 0 ? 0f : _lastT + 1f / SensorFps;
                    _offset = timestamp - t;
                    _needRebase = false;
                }
                else
                {
                    t = timestamp - _offset;
                }

                var f = new MocopiFrame { T = t, LocalRot = new Dictionary<int, Quaternion>(ids.Length) };
                for (int i = 0; i < ids.Length; i++)
                {
                    f.LocalRot[ids[i]] = ToUnityRot(rx[i], ry[i], rz[i], rw[i]);
                    if (ids[i] == RootBoneId)
                    {
                        f.RootPos = ToUnityPos(px[i], py[i], pz[i]);
                        f.HasRootPos = true;
                    }
                }
                _frames.Add(f);
                _lastT = t;
                _lastTimestamp = timestamp;
            }
        }

        // ---------------- メインスレッドから ----------------

        /// <summary>停止中なら新規に記録を始める。一時停止中なら再開する。</summary>
        public void Record()
        {
            lock (_lock)
            {
                if (_state == RecorderState.Stopped)
                {
                    _frames = new List<MocopiFrame>();
                    _lastT = 0f;
                }
                _needRebase = true;
                _state = RecorderState.Recording;
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                if (_state == RecorderState.Recording) _state = RecorderState.Paused;
            }
        }

        /// <summary>記録を止め、記録済みの骨定義とフレームを渡す。</summary>
        public void Stop(out MocopiSkeleton skeleton, out List<MocopiFrame> frames)
        {
            lock (_lock)
            {
                _state = RecorderState.Stopped;
                skeleton = _skeleton;
                frames = _frames;
                _frames = new List<MocopiFrame>();
            }
        }
    }
}
