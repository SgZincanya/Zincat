#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Zincat.AvatarSplitTest
{
    internal static class SplitConfigStore
    {
        static readonly Dictionary<int, AvatarSplitPlan> Cache = new Dictionary<int, AvatarSplitPlan>();
        internal static void ClearCache() => Cache.Clear();
        public static string Key(AvatarSplitPlan plan) => plan.config ? plan.config.Key : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(plan));
        public static AvatarSplitPlan Read(AvatarSplitConfig config, bool reload = false)
        {
            if (!SplitIds.Editing) throw new InvalidOperationException("请退出游戏模式后读取衣服菜单，运行期间暂停读取拆分配置。");
            if (!config || !config.Source || config.gameObject == config.Source || !config.transform.IsChildOf(config.Source.transform))
                throw new InvalidOperationException("拆分配置需要放在源 Avatar 内。");
            Cache.TryGetValue(config.GetInstanceID(), out var existing);
            if (!reload && existing) return existing;
            var plan = existing ? existing : ScriptableObject.CreateInstance<AvatarSplitPlan>();
            plan.hideFlags = HideFlags.HideAndDontSave;
            if (!string.IsNullOrEmpty(config.Data)) JsonUtility.FromJsonOverwrite(config.Data, plan);
            bool recovered = SplitConfigRecovery.Restore(config, plan);
            if (!recovered && config.PhotoPoseClip) plan.photoSettings.poseGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(config.PhotoPoseClip));
            plan.config = config; plan.name = config.Source.name + "_分组方案";
            bool copied = !string.IsNullOrEmpty(plan.sourceId) && plan.sourceId != SplitIds.Of(config.Source);
            plan.sourceId = SplitIds.Of(config.Source); plan.sourceScene = config.Source.scene.path;
            copied |= plan.groups.Any(g => config.References.Any(r => r.Id == g.id && r.Generated &&
                SplitIds.Of(r.Generated) != g.generatedAvatarId));
            // External output references on a duplicated Avatar must not replace the original's outputs.
            if (config.Output)
            {
                var output = config.Output.GetComponent<AvatarSplitOutputRoot>();
                if (output && output.Source && output.Source != config.Source) copied = true;
            }
            foreach (var g in plan.groups)
            {
                var refs = config.References.FirstOrDefault(r => r.Id == g.id);
                if (refs == null || recovered) continue;
                if (refs.PhotoCover) g.photoCoverGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(refs.PhotoCover));
                g.outfitIds = refs.Outfits.Select(o => o ? SplitIds.Of(o) : "").ToList();
                if (g.photoSelectionConfigured) g.photoOutfitIds = (refs.PhotoOutfits ?? new List<GameObject>()).Where(o => o).Select(o => SplitIds.Of(o)).ToList();
                g.generatedAvatarId = !copied && refs.Generated ? SplitIds.Of(refs.Generated) : "";
                g.generatedScene = !copied && refs.Generated ? refs.Generated.scene.path : "";
                if (copied) { g.generatedRevision = -1; g.cauSettingGuid = ""; }
            }
            if (copied)
            {
                config.Key = Guid.NewGuid().ToString("N"); config.Output = null;
                foreach (var refs in config.References) refs.Generated = null;
                EditorUtility.SetDirty(config);
            }
            plan.outputRootId = config.Output ? SplitIds.Of(config.Output) : "";
            SplitPhotoStorage.Upgrade(plan);
            Cache[config.GetInstanceID()] = plan;
            if (copied || recovered) Write(plan, false);
            return plan;
        }
        public static void Write(AvatarSplitPlan plan, bool saveScene = true)
        {
            if (EditorApplication.isPlaying || SplitIds.PlayModeSuspended) return;
            var config = plan.config;
            if (!config) return;
            if (config.gameObject != config.Source && config.transform.IsChildOf(config.Source.transform))
                config.gameObject.tag = "EditorOnly";
            config.PhotoPoseClip = SplitPhotoPose.Clip(plan.photoSettings);
            config.Data = JsonUtility.ToJson(plan);
            config.Output = SplitIds.Resolve(plan.outputRootId) as GameObject;
            config.References = plan.groups.Select(g => new AvatarSplitReferences {
                Id = g.id, Outfits = g.outfitIds.Select(id => SplitIds.Resolve(id) as GameObject).ToList(),
                Generated = SplitIds.Avatar(g),
                PhotoOutfits = g.photoOutfitIds.Select(id => SplitIds.Resolve(id) as GameObject).Where(o => o).ToList(),
                PhotoCover = SplitPhoto.CoverTexture(g)
            }).ToList();
            EditorUtility.SetDirty(config);
            if (PrefabUtility.IsPartOfPrefabInstance(config)) PrefabUtility.RecordPrefabInstancePropertyModifications(config);
            var scene = config.gameObject.scene;
            if (scene.IsValid() && scene.isLoaded)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                if (saveScene && !SplitIds.Reloading && !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
                    !string.IsNullOrEmpty(scene.path) && !EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException("拆分配置自动保存失败。");
            }
        }
        public static AvatarSplitPlan Attach(GameObject source, AvatarSplitPlan legacy = null)
        {
            if (!SplitIds.Editing) throw new InvalidOperationException("请退出游戏模式后设置衣服分组。");
            var existing = source.GetComponentsInChildren<AvatarSplitConfig>(true).FirstOrDefault(c => c.Source == source);
            if (existing)
            {
                var saved = Read(existing);
                if (existing.gameObject.tag != "EditorOnly") Write(saved);
                return saved;
            }
            var node = new GameObject("衣服拆分配置"); node.tag = "EditorOnly"; node.transform.SetParent(source.transform, false);
            Undo.RegisterCreatedObjectUndo(node, "创建 Avatar 拆分配置");
            var config = node.AddComponent<AvatarSplitConfig>(); config.Source = source;
            config.Key = legacy ? Key(legacy) : Guid.NewGuid().ToString("N");
            var plan = ScriptableObject.CreateInstance<AvatarSplitPlan>(); plan.hideFlags = HideFlags.HideAndDontSave;
            if (legacy) JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(legacy), plan);
            plan.config = config; plan.name = source.name + "_分组方案";
            plan.sourceId = SplitIds.Of(source); plan.sourceScene = source.scene.path;
            if (!legacy) { SplitNaming.Add(plan, source); SplitNaming.Add(plan, source); }
            Cache[config.GetInstanceID()] = plan;
            var output = SplitIds.Resolve(plan.outputRootId) as GameObject;
            if (output && output.TryGetComponent<AvatarSplitOutputRoot>(out var marker))
            { marker.Source = source; EditorUtility.SetDirty(marker); }
            SplitIds.Save(plan);
            return plan;
        }
        public static IEnumerable<AvatarSplitPlan> LoadedPlans()
        {
            foreach (var config in Resources.FindObjectsOfTypeAll<AvatarSplitConfig>())
                if (config && config.Source && config.gameObject.scene.IsValid() && config.gameObject.scene.isLoaded &&
                    !EditorUtility.IsPersistent(config)) yield return Read(config);
        }
    }
}
#endif
