#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Zincat.AvatarSplitTest
{
    [InitializeOnLoad]
    internal static class AvatarSplitMigration
    {
        const string Request = "Library/ZincatAvatarSplitMigration.request";
        const string Result = "Library/ZincatAvatarSplitMigration.result.txt";
        static AvatarSplitMigration() { if (File.Exists(Request)) EditorApplication.update += WhenReady; }
        static void WhenReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || CauBridge.Busy) return;
            EditorApplication.update -= WhenReady;
            if (!File.Exists(Request)) return;
            File.Delete(Request);
            try { Run(); AvatarSplitWindow.Open(); }
            catch (Exception e) { File.WriteAllText(Result, "FAIL: " + e); Debug.LogException(e); }
        }
        public static void Run()
        {
            var data = AvatarSplitBuilder.Root + "/Data";
            var reports = new List<string>();
            var oldPaths = new List<string>();
            if (AssetDatabase.IsValidFolder(data))
            {
                // Finish every migration before removing any legacy asset.
                foreach (var guid in AssetDatabase.FindAssets("t:AvatarSplitPlan", new[] { data }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var old = AssetDatabase.LoadAssetAtPath<AvatarSplitPlan>(path);
                    if (!old) continue;
                    if (!string.IsNullOrEmpty(old.sourceScene) && File.Exists(old.sourceScene) &&
                        !UnityEngine.SceneManagement.SceneManager.GetSceneByPath(old.sourceScene).isLoaded)
                        EditorSceneManager.OpenScene(old.sourceScene, OpenSceneMode.Additive);
                    var source = SplitIds.Resolve(old.sourceId) as GameObject;
                    if (!source)
                    {
                        // A deleted/recreated source cannot be matched safely. Archive the exact
                        // original outside Assets rather than guessing or losing its records.
                        var archive = "Library/ZincatAvatarSplitArchive/" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        Directory.CreateDirectory(archive);
                        File.Copy(path, archive + "/" + Path.GetFileName(path), true);
                        if (File.Exists(path + ".meta")) File.Copy(path + ".meta", archive + "/" + Path.GetFileName(path) + ".meta", true);
                        oldPaths.Add(path);
                        reports.Add("已归档失效旧方案（源对象已不存在）：" + path + " → " + archive);
                        continue;
                    }
                    var migrated = SplitConfigStore.Attach(source, old);
                    SplitIds.Save(migrated);
                    var checkedPlan = SplitConfigStore.Read(migrated.config, true);
                    if (checkedPlan.groups.Count != old.groups.Count || old.groups.Any(g => !checkedPlan.groups.Any(c =>
                        c.id == g.id && c.blueprintId == g.blueprintId && c.outfitIds.Count == g.outfitIds.Count)))
                        throw new InvalidOperationException("迁移校验不一致，保留旧方案：" + path);
                    oldPaths.Add(path);
                    reports.Add("已迁移：" + source.name + "，" + old.groups.Count + " 个分组；衣服选择及蓝图号已保存到 Avatar。");
                    foreach (var g in checkedPlan.groups)
                        reports.Add("  " + g.name + " / " + g.outfitIds.Count + " 套 / " + (g.blueprintId ?? ""));
                }
            }
            var plans = SplitConfigStore.LoadedPlans().ToArray();
            var keep = new HashSet<string>(plans.SelectMany(p => p.groups).Select(g => g.cauSettingGuid).Where(g => !string.IsNullOrEmpty(g)));
            var uploadFolder = AvatarSplitBuilder.Root + "/UploadSettings";
            var remove = new List<string>();
            if (AssetDatabase.IsValidFolder(data))
            {
                foreach (var guid in AssetDatabase.FindAssets("", new[] { data }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!Path.GetFileName(path).StartsWith("CAU_", StringComparison.Ordinal) || !path.EndsWith(".asset")) continue;
                    if (CauBridge.Type("AvatarUploadSetting") != AssetDatabase.GetMainAssetTypeAtPath(path)) continue;
                    if (keep.Contains(guid))
                    {
                        AvatarSplitBuilder.EnsureFolder(uploadFolder);
                        var destination = AssetDatabase.GenerateUniqueAssetPath(uploadFolder + "/" + Path.GetFileName(path));
                        string error = AssetDatabase.MoveAsset(path, destination);
                        if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                        reports.Add("保留并归档上传配置：" + destination);
                    }
                    else remove.Add(path);
                }
            }
            CauBridge.PruneDeletedSettings(new HashSet<string>(remove));
            foreach (var path in remove.Concat(oldPaths))
            {
                if (!path.StartsWith(data + "/", StringComparison.Ordinal)) throw new InvalidOperationException("清理路径越界。");
                if (!AssetDatabase.DeleteAsset(path)) throw new InvalidOperationException("无法清理：" + path);
                reports.Add("已清理旧文件：" + path);
            }
            SplitIds.Flush(); AssetDatabase.SaveAssets();
            reports.Add("PASS: 迁移与清理完成；未执行上传。");
            File.WriteAllLines(Result, reports); Debug.Log(string.Join("\n", reports));
        }
    }
}
#endif
