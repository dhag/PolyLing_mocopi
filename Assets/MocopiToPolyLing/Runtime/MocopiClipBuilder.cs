// MocopiClipBuilder.cs
// 記録結果を PolyLing モーション（*.plmotion.json）に書き出す。
//
// ■ トラック
//   muscles（HumanTrait.MuscleName 名）と body（id・targetKind とも "body"。pos = bodyPosition、rot = bodyRotation）。
//   値は MocopiSourceAvatar（mocopi 公式と同じ組み方の Humanoid）に各フレームを当てて取った HumanPose。
//   入れるのは mocopi が動かす Humanoid 骨のマッスルだけ（指・目・顎は入れない）。
//   PolyLing はマッスルを持つクリップだけ再生時に配信できる。
//
// ■ 書かないもの（PolyLing モーション version 4 の契約。PolyLing_外部連携の契約.md §5）
//   bakedBones は「正準骨格上のローカル回転」で、マッスルから Unity で解いた値を
//   MotionClipSerializer.Save が添える。mocopi 骨の回転をここで入れると意味が食い違うので入れない。
//   sourceRest（基準姿勢）とソース rest CSV は廃止した。

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Mocopi.Receiver;
using Poly_Ling.Motion;
using UnityEngine;

namespace MocopiToPolyLing
{
    public static class MocopiClipBuilder
    {
        public const string MotionExt = ".plmotion.json";

        public sealed class Result
        {
            public bool Ok;
            public string Message;
            public string MotionPath;
        }

        /// <summary>保存先のファイル名から、モーションのパスを決める。</summary>
        public static string ResolvePath(string fileName, string baseDir)
        {
            string p = fileName.Trim();
            if (!Path.IsPathRooted(p)) p = Path.Combine(baseDir, p);
            if (p.EndsWith(MotionExt, System.StringComparison.OrdinalIgnoreCase))
                p = p.Substring(0, p.Length - MotionExt.Length);
            else if (p.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
                p = p.Substring(0, p.Length - ".json".Length);
            return p + MotionExt;
        }

        public static Result Save(MocopiSkeleton skel, List<MocopiFrame> frames, string motionPath)
        {
            var r = new Result { MotionPath = motionPath };
            if (skel == null)
            {
                r.Message = "骨定義を受信していません。mocopi 側で送信を始め直してから記録してください。";
                return r;
            }
            if (frames == null || frames.Count == 0)
            {
                r.Message = "記録したフレームがありません。";
                return r;
            }

            var dto = new MotionClipDTO
            {
                name = Path.GetFileName(motionPath).Replace(MotionExt, ""),
                frameRate = MocopiMotionRecorder.SensorFps,
                space = "local",
                loop = false,
                metadata = new MotionMetadataDTO { createdWith = "MocopiToPolyLing" },
            };

            // ---- マッスル・body（mocopi 公式と同じ Humanoid に当てて取る）----
            int muscleTrackCount = 0;
            using (var src = new MocopiSourceAvatar())
            {
                if (!src.Build(skel, out string avatarError))
                {
                    r.Message = avatarError;
                    return r;
                }
                var names = HumanTrait.MuscleName;
                var tracks = new MotionScalarTrackDTO[names.Length];
                foreach (int bi in src.HumanBoneIndices)
                    for (int dof = 0; dof < 3; dof++)
                    {
                        int mi = HumanTrait.MuscleFromBone(bi, dof);
                        if (mi >= 0 && mi < names.Length && tracks[mi] == null)
                            tracks[mi] = new MotionScalarTrackDTO { name = names[mi] };
                    }
                var body = new MotionTrackDTO { id = MotionClipDTO.BodyTrackId, targetKind = MotionClipDTO.TargetBody };
                foreach (var f in frames)
                {
                    var pose = src.Sample(f);
                    for (int mi = 0; mi < tracks.Length; mi++)
                        if (tracks[mi] != null && pose.muscles != null && mi < pose.muscles.Length)
                            tracks[mi].keys.Add(new MotionScalarKeyDTO { t = f.T, v = pose.muscles[mi] });
                    var bq = Normalize(pose.bodyRotation);
                    body.keys.Add(new MotionKeyDTO
                    {
                        t   = f.T,
                        pos = new[] { pose.bodyPosition.x, pose.bodyPosition.y, pose.bodyPosition.z },
                        rot = new[] { bq.x, bq.y, bq.z, bq.w },
                    });
                }
                foreach (var t in tracks)
                    if (t != null) { dto.muscles.Add(t); muscleTrackCount++; }
                dto.body = body;
            }

            var sr = MotionClipSerializer.Save(dto, motionPath);
            if (!sr.Success)
            {
                r.Message = "モーションの保存に失敗しました:\n" + sr.FormatIssues(10);
                return r;
            }

            r.Ok = true;
            r.Message = $"保存しました（{frames.Count} フレーム, {frames[frames.Count - 1].T:F2} 秒, マッスル {muscleTrackCount}）";
            return r;
        }

        internal static string BoneName(int id)
        {
            foreach (var kv in MocopiAvatar.MOCOPI_BONE_NAME_TO_MOCOPI_BONE_ID)
                if (kv.Value == id) return kv.Key;
            return "bone_" + id.ToString(CultureInfo.InvariantCulture);
        }

        private static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 1e-8f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }
    }
}
