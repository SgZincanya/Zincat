using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Zincat.EditorTools
{
    // Unity supplies the actual package items, including updates to existing assets.
    [InitializeOnLoad]
    public static class PackageImportLocator
    {
        private const string ResultKey = "Zincat.ImportLocator.Results";
        private const string HistoryKey = "Zincat.ImportLocator.History";
        private const string PendingKey = "Zincat.ImportLocator.Pending";
        private const string EnabledKey = "Zincat.ImportLocator.AutoLocate";
        private const string AutoMenu = "Zincat/导入定位/导入后自动定位";
        private static bool batchReaderInitialized;
        private static Func<bool> batchIsImporting;
        private static HistoryData persistentHistory;
        private static bool historyReadFailed;

        [Serializable]
        private class FolderRecord
        {
            public string guid;
            public string path;
        }

        [Serializable]
        private class HistoryData
        {
            public List<FolderRecord> folders = new List<FolderRecord>();
            public List<string> recentGuids = new List<string>();
        }

        private static string HistoryFile => Path.GetFullPath(Path.Combine(Application.dataPath, "../UserSettings/ZincatImportHistory.json"));

        [Serializable]
        private class Results
        {
            public List<string> paths = new List<string>();
        }

        static PackageImportLocator()
        {
            AssetDatabase.onImportPackageItemsCompleted += OnItemsCompleted;
            EditorApplication.update += Update;
            EditorApplication.delayCall += MigrateSessionHistory;
        }

        private static Results Read(string key = ResultKey)
        {
            string json = SessionState.GetString(key, "");
            return string.IsNullOrEmpty(json) ? new Results() : JsonUtility.FromJson<Results>(json) ?? new Results();
        }

        private static void OnItemsCompleted(string[] items)
        {
            // Preserve the previous result, including one recorded before this update.
            RememberFolders(GetFolders());
            // Merge imports while a batch is running, then present them once it finishes.
            var results = SessionState.GetBool(PendingKey, false) ? Read() : new Results();
            foreach (string item in items ?? new string[0])
            {
                if (string.IsNullOrEmpty(item)) continue;
                string path = item.Replace('\\', '/').TrimEnd('/');
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if (!results.paths.Contains(path)) results.paths.Add(path);
            }
            SessionState.SetString(ResultKey, JsonUtility.ToJson(results));
            SessionState.SetBool(PendingKey, true);
            RememberFolders(GetFolders(), true);
            PackageImportBadges.Invalidate();
        }

        private static void Update()
        {
            if (!SessionState.GetBool(PendingKey, false) || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || IsBatchImporting()) return;
            SessionState.SetBool(PendingKey, false);
            string[] folders = GetFolders();
            RememberFolders(folders, true);
            foreach (var window in Resources.FindObjectsOfTypeAll<PackageImportFolderWindow>()) window.Repaint();
            if (!EditorPrefs.GetBool(EnabledKey, true)) return;
            if (folders.Length == 1) Locate(folders[0]);
            else if (folders.Length > 1) ShowRecentResults();
        }

        private static bool IsBatchImporting()
        {
            // The locator also works in projects with the older standalone importer.
            if (!batchReaderInitialized)
            {
                batchReaderInitialized = true;
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Zincat.EditorTools.PackageImportQueue"))
                    .FirstOrDefault(t => t != null);
                var getter = type == null ? null : type.GetProperty("IsImporting")?.GetGetMethod();
                if (getter != null) batchIsImporting = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
            }
            return batchIsImporting != null && batchIsImporting();
        }

        private static void RememberFolders(IEnumerable<string> folders, bool updateRecent = false)
        {
            var current = folders.ToArray();
            var history = ReadPersistentHistory();
            bool changed = false;
            foreach (string folder in current.Concat(Read(HistoryKey).paths).Distinct())
            {
                if (!AssetDatabase.IsValidFolder(folder)) continue;
                string guid = AssetDatabase.AssetPathToGUID(folder);
                if (string.IsNullOrEmpty(guid)) continue;
                var record = history.folders.FirstOrDefault(entry => entry.guid == guid);
                if (record == null)
                {
                    history.folders.Add(new FolderRecord { guid = guid, path = folder });
                    changed = true;
                }
                else if (record.path != folder) { record.path = folder; changed = true; }
            }
            if (updateRecent)
            {
                var recent = current.Where(AssetDatabase.IsValidFolder).Select(p => AssetDatabase.AssetPathToGUID(p))
                    .Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();
                if (!history.recentGuids.SequenceEqual(recent)) { history.recentGuids = recent; changed = true; }
            }
            if (changed || Read(HistoryKey).paths.Count > 0)
            {
                if (SavePersistentHistory()) SessionState.SetString(HistoryKey, "");
                else SessionState.SetString(HistoryKey, JsonUtility.ToJson(new Results { paths = PersistentFolders().ToList() }));
            }
            PackageImportBadges.Invalidate();
        }

        private static void MigrateSessionHistory()
        {
            if (EditorApplication.isUpdating || EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += MigrateSessionHistory;
                return;
            }
            RememberFolders(GetFolders(), Read().paths.Count > 0);
        }

        private static HistoryData ReadPersistentHistory()
        {
            if (persistentHistory != null) return persistentHistory;
            persistentHistory = new HistoryData();
            try
            {
                if (File.Exists(HistoryFile))
                {
                    var loaded = JsonUtility.FromJson<HistoryData>(File.ReadAllText(HistoryFile));
                    if (loaded == null || loaded.folders == null) throw new InvalidDataException("历史记录格式无效。");
                    persistentHistory = loaded;
                    if (persistentHistory.recentGuids == null) persistentHistory.recentGuids = new List<string>();
                    persistentHistory.folders.RemoveAll(entry => entry == null);
                }
            }
            catch (Exception ex)
            {
                historyReadFailed = true;
                Debug.LogWarning("[Zincat导入定位] 无法读取历史，已保留原文件，暂不覆盖：" + ex.Message);
            }
            return persistentHistory;
        }

        private static bool SavePersistentHistory()
        {
            if (historyReadFailed) return false;
            string temporary = HistoryFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile));
                File.WriteAllText(temporary, JsonUtility.ToJson(persistentHistory, true));
                // An explicit same-volume backup is compatible with Unity's Mono runtime;
                // the null-backup variant can fail on subsequent editor sessions.
                if (File.Exists(HistoryFile)) File.Replace(temporary, HistoryFile, HistoryFile + ".bak");
                else File.Move(temporary, HistoryFile);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Zincat导入定位] 历史保存失败，本次会话记录仍保留：" + ex.Message);
                return false;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static IEnumerable<string> PersistentFolders()
        {
            foreach (var entry in ReadPersistentHistory().folders)
            {
                string folder = string.IsNullOrEmpty(entry.guid) ? entry.path : AssetDatabase.GUIDToAssetPath(entry.guid);
                if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder)) yield return folder;
            }
        }

        internal static string[] GetHistoryFolders()
        {
            // Each import determines its own product root. Do not merge older sibling
            // products into their common author folder when a new product is imported.
            return PersistentFolders().Concat(Read(HistoryKey).paths).Concat(GetFolders()).Where(AssetDatabase.IsValidFolder)
                .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static string[] GetRecentFolders()
        {
            var recent = ReadPersistentHistory().recentGuids;
            if (recent.Count == 0 && Read().paths.Count > 0) return GetFolders();
            return recent.Select(g => AssetDatabase.GUIDToAssetPath(g))
                .Where(AssetDatabase.IsValidFolder).Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static bool CanRemoveHistoryRow => !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
            !IsBatchImporting() && !SessionState.GetBool(PendingKey, false);

        internal static bool RemoveHistoryRow(string folder)
        {
            if (!CanRemoveHistoryRow || string.IsNullOrEmpty(folder)) return false;
            // Commit any legacy session results before discarding their raw path cache.
            RememberFolders(GetFolders(), Read().paths.Count > 0);
            var previous = ReadPersistentHistory();
            if (historyReadFailed) return false;
            string guid = AssetDatabase.AssetPathToGUID(folder);
            var updated = new HistoryData
            {
                folders = previous.folders.Where(entry => entry.path != folder &&
                    (string.IsNullOrEmpty(guid) || entry.guid != guid)).ToList(),
                recentGuids = previous.recentGuids.Where(entry => entry != guid).ToList()
            };
            if (updated.folders.Count == previous.folders.Count && updated.recentGuids.Count == previous.recentGuids.Count) return false;
            persistentHistory = updated;
            if (!SavePersistentHistory()) { persistentHistory = previous; return false; }
            // Both views now read the saved GUID lists. Keeping the old import path
            // cache would recreate a removed record on the next script reload.
            SessionState.SetString(HistoryKey, "");
            SessionState.SetString(ResultKey, "");
            SessionState.SetBool(PendingKey, false);
            PackageImportBadges.Invalidate();
            foreach (var window in Resources.FindObjectsOfTypeAll<PackageImportFolderWindow>()) window.Repaint();
            return true;
        }

        internal static void ClearHistory()
        {
            ReadPersistentHistory();
            if (historyReadFailed)
            {
                Debug.LogWarning("[Zincat导入定位] 历史文件读取失败，未覆盖或清空磁盘历史。");
                return;
            }
            var previous = persistentHistory;
            persistentHistory = new HistoryData();
            if (!SavePersistentHistory()) { persistentHistory = previous; return; }
            SessionState.SetString(HistoryKey, "");
            SessionState.SetString(ResultKey, "");
            SessionState.SetBool(PendingKey, false);
            PackageImportBadges.Invalidate();
            foreach (var window in Resources.FindObjectsOfTypeAll<PackageImportFolderWindow>()) window.Repaint();
        }

        internal static string[] GetFolders()
        {
            var paths = Read().paths.Where(p => AssetDatabase.IsValidFolder(p) ||
                AssetDatabase.LoadMainAssetAtPath(p) != null).ToArray();
            // Ignore package folder entries when real files give us a more precise location.
            var files = paths.Where(p => !AssetDatabase.IsValidFolder(p)).ToArray();
            var directories = (files.Length > 0 ? files.Select(p => Path.GetDirectoryName(p).Replace('\\', '/')) : paths)
                .Distinct(StringComparer.Ordinal).ToArray();
            var folders = new List<string>();
            foreach (var group in directories.GroupBy(p => p.Split('/').Length > 1 ? p.Split('/')[1] : ""))
            {
                string common = group.First();
                foreach (string directory in group.Skip(1))
                    while (common != "Assets" && directory != common &&
                        !directory.StartsWith(common + "/", StringComparison.Ordinal))
                        common = common.Substring(0, common.LastIndexOf('/'));
                if (AssetDatabase.IsValidFolder(common)) folders.Add(common);
            }
            // A file directly in Assets should not hide the other, useful folders.
            return folders.Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static void Locate(string folder)
        {
            var asset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(folder);
            if (asset == null) return;
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        [MenuItem(AutoMenu)]
        private static void ToggleAuto()
        {
            EditorPrefs.SetBool(EnabledKey, !EditorPrefs.GetBool(EnabledKey, true));
        }

        [MenuItem(AutoMenu, true)]
        private static bool ValidateAuto()
        {
            Menu.SetChecked(AutoMenu, EditorPrefs.GetBool(EnabledKey, true));
            return true;
        }

        [MenuItem("Zincat/导入定位/最近导入结果")]
        private static void ShowRecentResults()
        {
            var window = EditorWindow.GetWindow<PackageImportRecentResultsWindow>("最近导入结果");
            window.minSize = new Vector2(420, 180);
            window.Show();
            window.Repaint();
        }

        [MenuItem("Zincat/导入定位/导入历史")]
        private static void ShowResults()
        {
            var window = EditorWindow.GetWindow<PackageImportResultsWindow>("导入历史");
            window.minSize = new Vector2(420, 180);
            window.Show();
            window.Repaint();
        }
    }

    [InitializeOnLoad]
    internal static class PackageImportBadges
    {
        private const string PreferenceKey = "Zincat.ImportLocator.ShowBadges";
        private const string MenuPath = "Zincat/导入定位/显示导入角标";
        private static HashSet<string> markedGuids;
        private static Texture2D badge;

        static PackageImportBadges()
        {
            EditorApplication.projectWindowItemOnGUI += Draw;
            EditorApplication.projectChanged += Invalidate;
            AssemblyReloadEvents.beforeAssemblyReload += DestroyBadge;
            EditorApplication.quitting += DestroyBadge;
        }

        internal static void Invalidate()
        {
            markedGuids = null;
            EditorApplication.RepaintProjectWindow();
        }

        internal static bool IsMarked(string guid)
        {
            if (markedGuids == null)
                markedGuids = new HashSet<string>(PackageImportLocator.GetHistoryFolders()
                    .Select(p => AssetDatabase.AssetPathToGUID(p)).Where(g => !string.IsNullOrEmpty(g)), StringComparer.Ordinal);
            return !string.IsNullOrEmpty(guid) && markedGuids.Contains(guid);
        }

        private static void Draw(string guid, Rect itemRect)
        {
            if (!EditorPrefs.GetBool(PreferenceKey, true) || !IsMarked(guid)) return;
            bool grid = itemRect.height > 20;
            float iconSize = grid ? Mathf.Min(itemRect.width, itemRect.height - EditorGUIUtility.singleLineHeight) : 16;
            float size = grid ? 10 : 7;
            var icon = new Rect(grid ? itemRect.center.x - iconSize / 2 : itemRect.x,
                grid ? itemRect.y : itemRect.center.y - iconSize / 2, iconSize, iconSize);
            var rect = new Rect(icon.xMax - size, icon.yMax - size, size, size);
            if (Event.current.type == EventType.Repaint)
            {
                Color previous = GUI.color;
                try
                {
                    bool dark = EditorGUIUtility.isProSkin;
                    GUI.color = previous * (dark ? new Color32(56, 56, 56, 255) : new Color32(194, 194, 194, 255));
                    GUI.DrawTexture(rect, GetBadge());
                    GUI.color = previous * (dark ? new Color32(118, 182, 255, 255) : new Color32(38, 105, 189, 255));
                    var fill = new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2);
                    GUI.DrawTexture(fill, GetBadge());
                }
                finally { GUI.color = previous; }
            }
            GUI.Label(rect, new GUIContent("", "已记录在导入历史中"), GUIStyle.none);
        }

        private static Texture2D GetBadge()
        {
            if (badge != null) return badge;
            const int resolution = 32;
            badge = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
            {
                name = "Zincat Import History Badge",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear
            };
            var pixels = new Color[resolution * resolution];
            for (int y = 0; y < resolution; y++)
                for (int x = 0; x < resolution; x++)
                {
                    float radius = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(16, 16));
                    pixels[y * resolution + x] = new Color(1, 1, 1, Mathf.Clamp01(16 - radius));
                }
            badge.SetPixels(pixels);
            badge.Apply(false, true);
            return badge;
        }

        private static void DestroyBadge()
        {
            if (badge != null) UnityEngine.Object.DestroyImmediate(badge);
            badge = null;
        }

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            EditorPrefs.SetBool(PreferenceKey, !EditorPrefs.GetBool(PreferenceKey, true));
            Invalidate();
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateToggle()
        {
            Menu.SetChecked(MenuPath, EditorPrefs.GetBool(PreferenceKey, true));
            return true;
        }
    }

    public sealed class PackageImportResultsWindow : PackageImportFolderWindow
    {
        protected override bool ShowHistory => true;
    }

    public sealed class PackageImportRecentResultsWindow : PackageImportFolderWindow
    {
        protected override bool ShowHistory => false;
    }

    public abstract class PackageImportFolderWindow : EditorWindow
    {
        private Vector2 scroll;
        protected abstract bool ShowHistory { get; }

        internal string[] GetDisplayedFolders()
        {
            return ShowHistory ? PackageImportLocator.GetHistoryFolders() : PackageImportLocator.GetRecentFolders();
        }

        protected virtual void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(ShowHistory ? "导入历史" : "最近一次导入", EditorStyles.boldLabel);
            if (ShowHistory && GUILayout.Button("清空记录", GUILayout.Width(80))) PackageImportLocator.ClearHistory();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(ShowHistory ? "累计保留各次导入的目录。" : "仅显示最近一次导入；批量导入时显示最近一批。", MessageType.Info);
            string[] folders = GetDisplayedFolders();
            if (folders.Length == 0)
                EditorGUILayout.LabelField("暂无可定位的结果，请先导入一个 unitypackage。");
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (string folder in folders)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.SelectableLabel(folder, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("定位", GUILayout.Width(55))) PackageImportLocator.Locate(folder);
                using (new EditorGUI.DisabledScope(!PackageImportLocator.CanRemoveHistoryRow))
                    if (GUILayout.Button("移除", GUILayout.Width(55))) PackageImportLocator.RemoveHistoryRow(folder);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
