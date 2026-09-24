// MocopiSourceAvatar.cs
// mocopi の骨定義から、表示に使わない Humanoid アバター（Transform 群＋Avatar）を組み、
// 記録した 1 フレームの骨の回転をそこへ当てて HumanPose（マッスル・bodyPosition・bodyRotation）を取り出す。
//
// 組み方は mocopi 公式 MocopiAvatar と同じ（MocopiAvatar.cs:580-725）：
//   骨定義の rest で骨の GameObject を作り、HUMAN_BONE_NAME_TO_MOCOPI_BONE_ID で Humanoid 骨を対応させ、
//   同じ HumanDescription（twist 0.5、stretch 0.05、feetSpacing 0、hasTranslationDoF false）で
//   AvatarBuilder.BuildHumanAvatar する。フレームの当て方も同じで、位置はルート骨だけフレームの値、
//   他の骨は rest の位置（MocopiAvatar.cs:772-775, 786-790）。
// 公式はこの HumanPose を表示アバターへ SetHumanPose している。ここでは記録用に値を取り出す。
//
// メインスレッドから使う（GameObject・AvatarBuilder・HumanPoseHandler）。

using System;
using System.Collections.Generic;
using Mocopi.Receiver;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MocopiToPolyLing
{
    public sealed class MocopiSourceAvatar : IDisposable
    {
        private GameObject _root;
        private readonly Dictionary<int, Transform> _bones = new Dictionary<int, Transform>();
        private Avatar _avatar;
        private HumanPoseHandler _handler;
        private HumanPose _pose;
        private MocopiSkeleton _skel;

        /// <summary>Humanoid に対応させた骨（Humanoid 骨の番号＝HumanTrait.BoneName の添字）。</summary>
        public readonly List<int> HumanBoneIndices = new List<int>();

        /// <summary>骨定義から組む。失敗したら false（error に理由）。</summary>
        public bool Build(MocopiSkeleton skel, out string error)
        {
            Dispose();
            error = "";
            _skel = skel;
            _root = new GameObject("MocopiSourceAvatar") { hideFlags = HideFlags.HideAndDontSave };

            foreach (int id in skel.BoneIds)
            {
                var go = new GameObject(MocopiClipBuilder.BoneName(id)) { hideFlags = HideFlags.HideAndDontSave };
                _bones[id] = go.transform;
            }
            foreach (int id in skel.BoneIds)
            {
                int p = skel.ParentOf[id];
                var t = _bones[id];
                t.SetParent(p >= 0 && _bones.TryGetValue(p, out var pt) ? pt : _root.transform, false);
                t.localPosition = skel.RestLocalPos[id];
                t.localRotation = skel.RestLocalRot[id];
            }

            var human = new List<HumanBone>();
            var boneNames = HumanTrait.BoneName;
            for (int bi = 0; bi < boneNames.Length; bi++)
            {
                if (!MocopiAvatar.HUMAN_BONE_NAME_TO_MOCOPI_BONE_ID.TryGetValue(boneNames[bi], out int id)) continue;
                if (!_bones.ContainsKey(id)) continue;
                var hb = new HumanBone { humanName = boneNames[bi], boneName = MocopiClipBuilder.BoneName(id) };
                hb.limit.useDefaultValues = true;
                human.Add(hb);
                HumanBoneIndices.Add(bi);
            }

            var skeleton = new List<SkeletonBone>
            {
                new SkeletonBone { name = _root.name, position = Vector3.zero, rotation = Quaternion.identity, scale = Vector3.one },
            };
            foreach (int id in skel.BoneIds)
            {
                var t = _bones[id];
                skeleton.Add(new SkeletonBone
                {
                    name = t.name, position = t.localPosition, rotation = t.localRotation, scale = Vector3.one,
                });
            }

            var desc = new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0.0f,
                hasTranslationDoF = false,
            };

            _avatar = AvatarBuilder.BuildHumanAvatar(_root, desc);
            if (_avatar == null || !_avatar.isValid || !_avatar.isHuman)
            {
                error = "骨定義から Humanoid アバターを組めませんでした。";
                Dispose();
                return false;
            }
            _handler = new HumanPoseHandler(_avatar, _root.transform);
            _pose = new HumanPose();
            return true;
        }

        /// <summary>フレームを当てて HumanPose を返す（muscles は Unity が持つ配列なので呼び出し側で写すこと）。</summary>
        public HumanPose Sample(MocopiFrame f)
        {
            foreach (var kv in _bones)
            {
                int id = kv.Key;
                var t = kv.Value;
                t.localRotation = f.LocalRot.TryGetValue(id, out var q) ? q : _skel.RestLocalRot[id];
                t.localPosition = (id == MocopiMotionRecorder.RootBoneId && f.HasRootPos) ? f.RootPos : _skel.RestLocalPos[id];
            }
            _handler.GetHumanPose(ref _pose);
            return _pose;
        }

        public void Dispose()
        {
            _handler?.Dispose();
            _handler = null;
            if (_avatar != null) Object.Destroy(_avatar);
            _avatar = null;
            foreach (var t in _bones.Values) if (t != null) Object.Destroy(t.gameObject);
            _bones.Clear();
            if (_root != null) Object.Destroy(_root);
            _root = null;
            HumanBoneIndices.Clear();
        }
    }
}
