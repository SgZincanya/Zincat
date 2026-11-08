#if UNITY_EDITOR
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Zincat.AvatarSplitTest
{
    internal static class SplitConfigRecovery
    {
        [Serializable]
        sealed class Snapshot { public string sourceId; public string data; }
        static string PathFor(AvatarSplitConfig config) => config && !string.IsNullOrEmpty(config.Key) &&
            Regex.IsMatch(config.Key, @"^[a-zA-Z0-9_-]+$") ? "Library/ZincatAvatarSplitRecovery/" + config.Key + ".json" : null;
        public static void Backup(AvatarSplitPlan plan)
        {
            var path = PathFor(plan.config);
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var data = JsonUtility.ToJson(new Snapshot { sourceId = plan.sourceId, data = plan.config.Data });
                if (File.Exists(path) && File.ReadAllText(path) == data) return;
                File.WriteAllText(path + ".tmp", data);
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
                else File.Move(path + ".tmp", path);
            }
            catch (Exception e) { Debug.LogWarning("[衣服分组] 本地恢复记录未写入，请使用 Ctrl+S 保存场景：" + e.Message); }
        }
        public static bool Restore(AvatarSplitConfig config, AvatarSplitPlan plan)
        {
            var path = PathFor(config);
            if (path == null || !File.Exists(path) || !config.Source) return false;
            try
            {
                var scenePath = config.Source.scene.path;
                if (string.IsNullOrEmpty(scenePath) || !File.Exists(scenePath) ||
                    File.GetLastWriteTimeUtc(path) <= File.GetLastWriteTimeUtc(scenePath)) return false;
                var saved = JsonUtility.FromJson<Snapshot>(File.ReadAllText(path));
                if (saved == null || saved.sourceId != SplitIds.Of(config.Source) || string.IsNullOrEmpty(saved.data)) return false;
                JsonUtility.FromJsonOverwrite(saved.data, plan);
                return true;
            }
            catch (Exception e) { Debug.LogWarning("[衣服分组] 无法读取本地恢复记录，继续使用 Avatar 配置：" + e.Message); return false; }
        }
    }
}
#endif
