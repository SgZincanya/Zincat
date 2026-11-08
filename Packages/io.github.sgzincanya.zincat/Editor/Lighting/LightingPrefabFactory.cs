// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Zincat.Lighting.Editor
{
    public static class LightingPrefabFactory
    {
        public const string PrefabPath = "Packages/io.github.sgzincanya.zincat/Lighting/Prefabs/Zincat 光照控制.prefab";

        public static GameObject CreateTemplate(string path = "Assets/Zincat/Lighting/Prefabs/Zincat 光照控制.prefab")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AssetDatabase.Refresh();
            var root = new GameObject("Zincat 光照控制");
            try
            {
                var settings = root.AddComponent<ZincatLightingPrefab>();
                LightingSetup.SynchronizeMenu(settings, false);
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [MenuItem("Zincat/光照控制")]
        public static void AddOrShow()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) { Debug.LogError("找不到 Zincat 光照控制预制体，请重新导入安装包。"); return; }
            var selected = Selection.activeGameObject;
            var avatar = selected == null ? null : selected.GetComponentInParent<VRCAvatarDescriptor>();
            if (avatar == null || EditorUtility.IsPersistent(avatar))
            { Selection.activeObject = prefab; EditorGUIUtility.PingObject(prefab); return; }
            var configurations = avatar.GetComponentsInChildren<ZincatLightingPrefab>(true);
            var existing = configurations.FirstOrDefault(s => s.enabled && s.gameObject.activeInHierarchy) ?? configurations.FirstOrDefault();
            if (existing != null) { Selection.activeGameObject = existing.gameObject; return; }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar.transform);
            Undo.RegisterCreatedObjectUndo(instance, "Add Zincat Lighting Prefab");
            Selection.activeGameObject = instance;
        }
    }
}
