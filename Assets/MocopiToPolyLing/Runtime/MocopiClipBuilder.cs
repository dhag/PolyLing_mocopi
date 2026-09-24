// MocopiClipBuilder.cs
// 記録結果を PolyLing モーション（*.plmotion.json）と、他モデルへ当てるためのソース rest
// （UnityBone CSV。Runtime の UnityClipApplier.LoadSourceRestCsv が読む先頭 16 列）に書き出す。
//
// ■ トラック
//   bakedBones（targetKind = "humanoid"、id = Humanoid 名）。対応表は mocopi 公式
//   MocopiAvatar.HUMAN_BONE_NAME_TO_MOCOPI_BONE_ID（18 骨）。
//   Humanoid に対応しない mocopi 骨（torso_1 など）の回転は、Humanoid 上の親までの積に畳み込む。
//   例：LeftShoulder = torso_7 × l_shoulder、Neck = torso_7 × neck_1。
//   Humanoid の親子は UnityClipApplier.Apply.cs:364-376 の正準表と同じになる。
//   Hips だけ位置も入れる（適用側は回転のみ使う：UnityClipApplier.Canon.cs:308-322）。
//   muscles（HumanTrait.MuscleName 名）と body（pos = bodyPosition、rot = bodyRotation）も入れる。
//   値は MocopiSourceAvatar（mocopi 公式と同じ組み方の Humanoid）に各フレームを当てて取った HumanPose。
//   入れるのは mocopi が動かす Humanoid 骨のマッスルだけ（指・目・顎は入れない）。
//   PolyLing はマッスルを持つクリップだけ再生時に配信できる。
//
// ■ ソース rest CSV
//   骨定義の rest を親から積んでワールド回転・位置を出す。全 27 骨を書き、Humanoid 列は対応骨だけ埋める。
//   RestL 列は mocopi 骨の親相対ローカル。

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Mocopi.Receiver;
using Poly_Ling.Motion;
using UnityEngine;

namespace MocopiToPolyLing
{
    public static class MocopiClipBuilder
    {
        public const string MotionExt = ".plmotion.json";
        public const string RestCsvExt = ".sourcerest.csv";

        public sealed class Result
        {
            public bool Ok;
            public string Message;
            public string MotionPath;
            public string RestCsvPath;
        }

        /// <summary>保存先のファイル名から、モーションと CSV のパスを決める。</summary>
        public static void ResolvePaths(string fileName, string baseDir, out string motionPath, out string csvPath)
        {
            string p = fileName.Trim();
            if (!Path.IsPathRooted(p)) p = Path.Combine(baseDir, p);
            if (p.EndsWith(MotionExt, System.StringComparison.OrdinalIgnoreCase))
                p = p.Substring(0, p.Length - MotionExt.Length);
            else if (p.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
                p = p.Substring(0, p.Length - ".json".Length);
            motionPath = p + MotionExt;
            csvPath = p + RestCsvExt;
        }

        public static Result Save(MocopiSkeleton skel, List<MocopiFrame> frames, string motionPath, string csvPath)
        {
            var r = new Result { MotionPath = motionPath, RestCsvPath = csvPath };
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

            // Humanoid 名 ⇔ mocopi 骨 ID
            var humOfId = new Dictionary<int, string>();
            foreach (var kv in MocopiAvatar.HUMAN_BONE_NAME_TO_MOCOPI_BONE_ID)
                if (skel.ParentOf.ContainsKey(kv.Value)) humOfId[kv.Value] = kv.Key;

            // Humanoid 骨ごとに、畳み込む mocopi 骨の列（上から順）
            var chains = new List<KeyValuePair<string, List<int>>>();
            foreach (var kv in humOfId)
            {
                var chain = new List<int> { kv.Key };
                int p = skel.ParentOf[kv.Key];
                while (p >= 0 && !humOfId.ContainsKey(p) && skel.ParentOf.ContainsKey(p))
                {
                    chain.Insert(0, p);
                    p = skel.ParentOf[p];
                }
                chains.Add(new KeyValuePair<string, List<int>>(kv.Value, chain));
            }

            // ---- モーション ----
            var dto = new MotionClipDTO
            {
                name = Path.GetFileName(motionPath).Replace(MotionExt, ""),
                frameRate = MocopiMotionRecorder.SensorFps,
                space = "local",
                loop = false,
                metadata = new MotionMetadataDTO { createdWith = "MocopiToPolyLing" },
            };

            foreach (var ch in chains)
            {
                var track = new MotionTrackDTO { id = ch.Key, targetKind = "humanoid" };
                bool isRoot = ch.Value.Count == 1 && ch.Value[0] == MocopiMotionRecorder.RootBoneId;
                foreach (var f in frames)
                {
                    Quaternion q = Quaternion.identity;
                    foreach (int id in ch.Value)
                    {
                        Quaternion l = f.LocalRot.TryGetValue(id, out var fq) ? fq : skel.RestLocalRot[id];
                        q = q * l;
                    }
                    q = Normalize(q);
                    var key = new MotionKeyDTO { t = f.T, rot = new[] { q.x, q.y, q.z, q.w } };
                    if (isRoot && f.HasRootPos) key.pos = new[] { f.RootPos.x, f.RootPos.y, f.RootPos.z };
                    track.keys.Add(key);
                }
                dto.bakedBones.Add(track);
            }

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
                var body = new MotionTrackDTO { id = "body", targetKind = "body" };
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

            // ---- ソース rest CSV ----
            try
            {
                File.WriteAllText(csvPath, BuildRestCsv(skel, humOfId), new UTF8Encoding(false));
            }
            catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException)
            {
                r.Message = "モーションは保存しましたが、rest CSV の保存に失敗しました: " + e.Message;
                return r;
            }

            r.Ok = true;
            r.Message = $"保存しました（{frames.Count} フレーム, {frames[frames.Count - 1].T:F2} 秒, {chains.Count} 骨, マッスル {muscleTrackCount}）";
            return r;
        }

        private static string BuildRestCsv(MocopiSkeleton skel, Dictionary<int, string> humOfId)
        {
            var worldRot = new Dictionary<int, Quaternion>();
            var worldPos = new Dictionary<int, Vector3>();

            // 親から順に積む（骨定義の並びに依存しない）
            void Solve(int id)
            {
                if (worldRot.ContainsKey(id)) return;
                int p = skel.ParentOf[id];
                if (p >= 0 && skel.ParentOf.ContainsKey(p))
                {
                    Solve(p);
                    worldRot[id] = Normalize(worldRot[p] * skel.RestLocalRot[id]);
                    worldPos[id] = worldPos[p] + worldRot[p] * skel.RestLocalPos[id];
                }
                else
                {
                    worldRot[id] = Normalize(skel.RestLocalRot[id]);
                    worldPos[id] = skel.RestLocalPos[id];
                }
            }

            var sb = new StringBuilder();
            sb.Append(";UnityBoneCSV,version,3,space,unity,units,m,root,mocopi\n");
            sb.Append("UnityBone,Name,NameEn,Humanoid,Parent,PosX,PosY,PosZ,")
              .Append("RestLX,RestLY,RestLZ,RestLW,RestWX,RestWY,RestWZ,RestWW\n");

            foreach (int id in skel.BoneIds)
            {
                Solve(id);
                int p = skel.ParentOf[id];
                string parent = (p >= 0 && skel.ParentOf.ContainsKey(p)) ? BoneName(p) : "";
                string hum = humOfId.TryGetValue(id, out var h) ? h : "";
                Vector3 wp = worldPos[id];
                Quaternion rl = skel.RestLocalRot[id];
                Quaternion rw = worldRot[id];
                sb.Append("UnityBone,").Append(BoneName(id)).Append(",\"\",")
                  .Append(hum).Append(',').Append(parent).Append(',')
                  .Append(F(wp.x)).Append(',').Append(F(wp.y)).Append(',').Append(F(wp.z)).Append(',')
                  .Append(F(rl.x)).Append(',').Append(F(rl.y)).Append(',').Append(F(rl.z)).Append(',').Append(F(rl.w)).Append(',')
                  .Append(F(rw.x)).Append(',').Append(F(rw.y)).Append(',').Append(F(rw.z)).Append(',').Append(F(rw.w))
                  .Append('\n');
            }
            return sb.ToString();
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

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
