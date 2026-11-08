#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Zincat.AvatarSplitTest
{
    internal static class SplitNaming
    {
        public static string NameFor(string sourceName, SplitGroup group)
        {
            var names = group.outfitNames.Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => System.Text.RegularExpressions.Regex.Replace(n.Trim(), @"\s+", " ")).ToArray();
            var prefix = sourceName + "V" + group.version;
            return names.Length == 0 ? prefix : prefix + "｜" + string.Join("·", names);
        }
        public static int NextVersion(AvatarSplitPlan plan) => plan.groups.Count == 0 ? 1 : plan.groups.Max(g => g.version) + 1;
        public static bool Apply(AvatarSplitPlan plan, GameObject source)
        {
            var used = new System.Collections.Generic.HashSet<int>();
            bool changed = false;
            int next = 1;
            foreach (var group in plan.groups)
            {
                if (group.version < 1 || !used.Add(group.version))
                {
                    while (used.Contains(next)) next++;
                    group.version = next; used.Add(next); changed = true;
                }
                var name = NameFor(source.name, group);
                if (group.name == name) continue;
                bool generatedWasCurrent = group.generatedRevision == group.revision;
                group.name = name; group.Invalidate(); changed = true;
                // A metadata-only rename can update an existing result directly.
                // Changed clothing selections already invalidate the revision beforehand.
                var generated = generatedWasCurrent ? SplitIds.Avatar(group) : null;
                if (generated)
                {
                    generated.name = name; group.generatedRevision = group.revision;
                    EditorUtility.SetDirty(generated); EditorSceneManager.MarkSceneDirty(generated.scene);
                }
            }
            if (changed) SplitIds.SaveLater(plan);
            return changed;
        }
        public static SplitGroup Add(AvatarSplitPlan plan, GameObject source)
        {
            var group = new SplitGroup { version = NextVersion(plan) };
            plan.groups.Add(group); Apply(plan, source); return group;
        }
    }

    internal static class SplitSourceStore
    {
        public static void EnsureSaved(GameObject avatar)
        {
            var scene = avatar.scene;
            if (!scene.IsValid() || !scene.isLoaded || EditorUtility.IsPersistent(avatar))
                throw new InvalidOperationException("请拖入场景中的源 Avatar。");
            string path = scene.path;
            if (string.IsNullOrEmpty(path))
            {
                var folder = AvatarSplitBuilder.Root + "/Data/Scenes";
                AvatarSplitBuilder.EnsureFolder(folder);
                var name = SafeName(avatar.name);
                path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + ".unity");
            }
            if ((scene.isDirty || string.IsNullOrEmpty(scene.path)) && !EditorSceneManager.SaveScene(scene, path))
                throw new InvalidOperationException("自动保存源场景失败，请检查场景文件是否可写。");
        }

        public static string SafeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Avatar" : name;
        }
        public static AvatarSplitPlan Create(GameObject source)
        {
            EnsureSaved(source);
            return SplitConfigStore.Attach(source);
        }
        public static AvatarSplitPlan Find(GameObject source)
        {
            var config = source.GetComponentsInChildren<AvatarSplitConfig>(true).FirstOrDefault(c => c.Source == source);
            if (config) return SplitConfigStore.Read(config);
            var folder = AvatarSplitBuilder.Root + "/Data";
            if (!AssetDatabase.IsValidFolder(folder)) return null;
            var id = SplitIds.Of(source);
            foreach (string guid in AssetDatabase.FindAssets("t:AvatarSplitPlan", new[] { folder }))
            {
                var plan = AssetDatabase.LoadAssetAtPath<AvatarSplitPlan>(AssetDatabase.GUIDToAssetPath(guid));
                if (plan && plan.sourceId == id) return SplitConfigStore.Attach(source, plan);
            }
            return null;
        }
    }
}
#endif
