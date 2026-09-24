// MocopiRecorderSceneBuilder.cs
// メニュー「Tools/MocopiToPolyLing/録画シーンを作成」で MocopiRecorder.unity を作る。
// カメラ・ライト・アバターの配置は mocopi 公式サンプル ReceiverSample.unity と同じ値。
// アバターは公式サンプルの MocopiAvatar.fbx（GUID で引く）。

using System.Linq;
using Mocopi.Receiver;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MocopiToPolyLing
{
    public static class MocopiRecorderSceneBuilder
    {
        private const string AvatarFbxGuid = "cccbbdb3cbdde464ba23dc13bf813538";   // MocopiAvatar.fbx.meta
        private const string ScenePath = "Assets/MocopiToPolyLing/MocopiRecorder.unity";

        [MenuItem("Tools/MocopiToPolyLing/録画シーンを作成")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            string fbxPath = AssetDatabase.GUIDToAssetPath(AvatarFbxGuid);
            var fbx = string.IsNullOrEmpty(fbxPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx == null)
            {
                EditorUtility.DisplayDialog("MocopiToPolyLing",
                    "mocopi 公式サンプルの MocopiAvatar.fbx が見つかりません。", "OK");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cam.tag = "MainCamera";
            cam.transform.SetPositionAndRotation(new Vector3(0f, 1f, 3f), new Quaternion(0f, 1f, 0f, 0f));

            var light = new GameObject("Directional Light", typeof(Light));
            light.GetComponent<Light>().type = LightType.Directional;
            light.transform.SetPositionAndRotation(new Vector3(0f, 3f, 0f),
                new Quaternion(0.40821788f, -0.23456968f, 0.10938163f, 0.8754261f));

            var av = (GameObject)PrefabUtility.InstantiatePrefab(fbx, scene);
            av.name = "MocopiAvatar";
            av.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var animator = av.GetComponent<Animator>();
            if (animator == null) animator = av.AddComponent<Animator>();
            if (animator.avatar == null)
                animator.avatar = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Avatar>().FirstOrDefault();
            var mocopiAvatar = av.GetComponent<MocopiAvatar>();
            if (mocopiAvatar == null) mocopiAvatar = av.AddComponent<MocopiAvatar>();

            var app = new GameObject("MocopiRecorder").AddComponent<MocopiRecorderApp>();
            app.avatar = mocopiAvatar;

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[MocopiToPolyLing] シーンを作成しました: " + ScenePath);
        }
    }
}
