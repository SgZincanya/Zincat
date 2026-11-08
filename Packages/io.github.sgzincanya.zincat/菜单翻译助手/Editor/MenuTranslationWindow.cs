using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zincat.MenuTranslation
{
    public sealed partial class MenuTranslationWindow : EditorWindow
    {
        const string MaType = "nadena.dev.modular_avatar.core.ModularAvatarMenuItem";
        const string MaGroupType = "nadena.dev.modular_avatar.core.ModularAvatarMenuGroup";
        const string MenuType = "VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu";
        [Serializable] public sealed class TranslationFile
        {
            public int version = 1;
            public string project;
            public List<Entry> entries = new List<Entry>();
        }
        [Serializable] public sealed class Entry
        {
            public string objectId;
            public bool restoreRawLabel;
            public string rawLabel;
            public string assetGuid;
            public long localId;
            public string menuPath;
            public int controlIndex;
            public string original;
            public string translation = "";
        }
        [Serializable] sealed class Change
        {
            public Entry entry;
            public Object asset;
            public bool enabled = true;
            public string status;
        }
        [SerializeField] List<Change> changes = new List<Change>();
        [SerializeField] string notice = "选中菜单资源或 Avatar，复制待翻译内容后粘贴到聊天。";
        [SerializeField] string lastBackup;
        [SerializeField] Object[] targets = new Object[0];
        [SerializeField] bool initializedTargets;
        [SerializeField] bool includeVrc = true, includeMa = true;
        string search = "";
        int filter;
        MessageType noticeType = MessageType.Info;

        [MenuItem("Zincat/菜单翻译助手")]
        public static void Open()
        {
            var window = GetWindow<MenuTranslationWindow>("菜单翻译助手");
            if (window.position.width < 700) window.position = new Rect(window.position.x, window.position.y, 920, 720);
        }
        void OnEnable() { minSize = new Vector2(600, 640); }
        public void CreateGUI()
        {
            if (!initializedTargets)
            {
                if (targets == null || targets.Length == 0) targets = Selection.objects;
                initializedTargets = true;
            }
            SyncPreviewToTargets();
            BuildInterface();
        }

        void SetNotice(string text, MessageType type) { notice = text; noticeType = type; }
        void Run(Action action)
        {
            try { action(); }
            catch (Exception e) { Debug.LogException(e); SetNotice("未完成：" + e.Message, MessageType.Error); }
            RefreshPreview();
        }
        static bool IsMenu(Object obj) { return obj != null && obj.GetType().FullName == MenuType; }
        static bool IsMa(Object obj) { return obj != null && obj.GetType().FullName == MaType; }
        static string HierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null) { transform = transform.parent; path = transform.name + "/" + path; }
            return path;
        }
        static string MaLocation(Component component)
        {
            string asset = AssetDatabase.GetAssetPath(component);
            if (string.IsNullOrEmpty(asset)) asset = component.gameObject.scene.path;
            return asset + " · " + HierarchyPath(component.transform);
        }
        static string MaDisplay(Object obj)
        {
            var label = new SerializedObject(obj).FindProperty("label");
            if (label == null) throw new InvalidOperationException("当前 MA 版本没有 label 字段，无法安全修改。");
            return string.IsNullOrEmpty(label.stringValue) ? ((Component)obj).gameObject.name : label.stringValue;
        }
        static Entry MakeMaEntry(Object obj, string original, string translation)
        {
            var component = (Component)obj;
            var id = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            if (id.identifierType == 0 || id.assetGUID.ToString() == "00000000000000000000000000000000" || id.targetObjectId == 0)
                throw new InvalidOperationException("MA 对象“" + component.gameObject.name + "”还没有稳定标识。请先保存场景或 Prefab，然后重新扫描。");
            return new Entry { objectId = id.ToString(), original = original, translation = translation, menuPath = MaLocation(component) };
        }
        void ScanObject(Object obj, HashSet<Object> visited, HashSet<Object> menus, TranslationFile file)
        {
            if (obj == null || !visited.Add(obj)) return;
            if (IsMenu(obj)) { if (includeVrc) menus.Add(obj); return; }
            var go = obj as GameObject;
            if (go != null)
            {
                foreach (var component in go.GetComponentsInChildren<Component>(true)) ScanObject(component, visited, menus, file);
                return;
            }
            var so = new SerializedObject(obj);
            if (includeMa && IsMa(obj)) file.entries.Add(MakeMaEntry(obj, MaDisplay(obj), ""));
            var p = so.GetIterator();
            while (p.Next(true))
            {
                if (p.propertyType != SerializedPropertyType.ObjectReference) continue;
                var reference = p.objectReferenceValue;
                if (includeVrc && IsMenu(reference)) menus.Add(reference);
            }
            // These are the two MA mechanisms that collect menu items from a different hierarchy.
            if ((includeMa || includeVrc) && (IsMa(obj) || obj.GetType().FullName == MaGroupType))
            {
                string field = IsMa(obj) ? "menuSource_otherObjectChildren" : "targetObject";
                var external = so.FindProperty(field);
                var mode = so.FindProperty("MenuSource");
                if (external != null && (mode == null || mode.intValue == 1)) ScanObject(external.objectReferenceValue, visited, menus, file);
            }
        }
        TranslationFile ScanTargets()
        {
            var file = NewFile();
            var menus = new HashSet<Object>();
            var visited = new HashSet<Object>();
            foreach (var obj in targets) ScanObject(obj, visited, menus, file);
            var sorted = new List<Object>(menus);
            sorted.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
            var menuVisited = new HashSet<Object>();
            foreach (var menu in sorted) Collect(menu, menuVisited, file);
            return file;
        }
        static SerializedProperty Controls(SerializedObject so)
        {
            var p = so.FindProperty("controls");
            if (p == null || !p.isArray) throw new InvalidOperationException("菜单结构不受支持：" + so.targetObject.name);
            return p;
        }
        static Entry MakeEntry(Object asset, int index, string original, string translation)
        {
            string guid; long id;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out id) || string.IsNullOrEmpty(guid))
                throw new InvalidOperationException("菜单尚未保存为资源：" + asset.name);
            return new Entry { assetGuid = guid, localId = id, menuPath = AssetDatabase.GetAssetPath(asset), controlIndex = index, original = original, translation = translation };
        }
        static void Collect(Object asset, HashSet<Object> visited, TranslationFile file)
        {
            if (!IsMenu(asset) || !visited.Add(asset)) return;
            var controls = Controls(new SerializedObject(asset));
            for (int i = 0; i < controls.arraySize; i++)
            {
                var control = controls.GetArrayElementAtIndex(i);
                var name = control.FindPropertyRelative("name");
                if (name == null) throw new InvalidOperationException("菜单没有名称字段。");
                file.entries.Add(MakeEntry(asset, i, name.stringValue, ""));
                var sub = control.FindPropertyRelative("subMenu");
                if (sub != null) Collect(sub.objectReferenceValue, visited, file);
            }
        }
        static TranslationFile NewFile() { return new TranslationFile { project = new DirectoryInfo(Path.GetDirectoryName(Application.dataPath)).Name }; }
        static void Write(string path, TranslationFile file) { File.WriteAllText(path, JsonUtility.ToJson(file, true), new UTF8Encoding(false)); }

        void CopyMenus()
        {
            if (!SyncPreviewToTargets()) return;
            var file = ScanTargets();
            if (file.entries.Count == 0) throw new InvalidOperationException("没有找到菜单。请添加 Avatar、MA 菜单对象或 VRC 菜单资源，并勾选相应来源。");
            var rows = new List<MenuTranslationClipboard.Row>();
            foreach (var e in file.entries)
                rows.Add(new MenuTranslationClipboard.Row { objectId = e.objectId, guid = e.assetGuid, localId = e.localId, index = e.controlIndex, original = e.original, translation = "" });
            EditorGUIUtility.systemCopyBuffer = MenuTranslationClipboard.Write(rows);
            SetNotice("已复制 " + rows.Count + " 条文字。现在粘贴到聊天；内容已自带翻译说明。", MessageType.Info);
            ShowNotification(new GUIContent("已复制待翻译内容"));
        }
        void PasteTranslation()
        {
            var rows = MenuTranslationClipboard.Read(EditorGUIUtility.systemCopyBuffer);
            var file = NewFile();
            foreach (var row in rows)
                file.entries.Add(new Entry { objectId = row.objectId, assetGuid = row.guid, localId = row.localId, controlIndex = row.index, original = row.original, translation = row.translation, menuPath = AssetDatabase.GUIDToAssetPath(row.guid) });
            LoadTranslation(file);
        }

        static Object Resolve(Entry e)
        {
            if (!string.IsNullOrEmpty(e.objectId))
            {
                GlobalObjectId id;
                if (!GlobalObjectId.TryParse(e.objectId, out id)) return null;
                var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
                return IsMa(resolved) ? resolved : null;
            }
            var path = AssetDatabase.GUIDToAssetPath(e.assetGuid ?? "");
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                string guid; long id;
                if (IsMenu(asset) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out id) && id == e.localId) return asset;
            }
            return null;
        }
        static string Validate(Change c)
        {
            if (string.IsNullOrWhiteSpace(c.entry.original)) return "跳过：原名称为空";
            if (!c.entry.restoreRawLabel && string.IsNullOrWhiteSpace(c.entry.translation)) return "跳过：译文为空";
            if (c.entry.original == c.entry.translation) return "跳过：文字相同";
            c.asset = Resolve(c.entry);
            if (c.asset == null) return "跳过：找不到原菜单资源";
            if (IsMa(c.asset))
            {
                var component = (Component)c.asset;
                string path = EditorUtility.IsPersistent(component) ? AssetDatabase.GetAssetPath(component) : component.gameObject.scene.path;
                if (string.IsNullOrEmpty(path)) return "跳过：请先保存此 MA 对象所在场景或 Prefab";
                if (!AssetDatabase.IsOpenForEdit(path)) return "跳过：场景或 Prefab 不可编辑";
                c.entry.menuPath = MaLocation(component);
                if (MaDisplay(c.asset) != c.entry.original) return "跳过：MA 显示名称已变化，请重新复制";
                return null;
            }
            if (!AssetDatabase.IsOpenForEdit(c.asset)) return "跳过：资源不可编辑";
            var controls = Controls(new SerializedObject(c.asset));
            int index = c.entry.controlIndex;
            if (index < 0 || index >= controls.arraySize) return "跳过：菜单条目已变化";
            var name = controls.GetArrayElementAtIndex(index).FindPropertyRelative("name");
            if (name == null || name.stringValue != c.entry.original) return "跳过：当前名称与导出时不同，请重新导出";
            return null;
        }
        void Import()
        {
            var path = EditorUtility.OpenFilePanel("读取翻译或还原文件", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            LoadFile(path);
        }
        void LoadFile(string path)
        {
            LoadTranslation(JsonUtility.FromJson<TranslationFile>(File.ReadAllText(path, Encoding.UTF8)));
        }
        static string EntryKey(Entry entry)
        {
            return string.IsNullOrEmpty(entry.objectId) ? entry.assetGuid + ":" + entry.localId + ":" + entry.controlIndex : "ma@" + entry.objectId;
        }
        bool SyncPreviewToTargets()
        {
            var liveTargets = new List<Object>();
            foreach (var target in targets ?? new Object[0]) if (target != null && !liveTargets.Contains(target)) liveTargets.Add(target);
            if (targets == null || liveTargets.Count != targets.Length)
            {
                targets = liveTargets.ToArray();
                if (sourcePanel != null) BuildSources();
            }
            if (targets == null || targets.Length == 0)
            {
                changes.Clear(); search = ""; filter = 0;
                SetNotice("尚未添加翻译对象，预览已清空。", MessageType.Info);
                RefreshPreview(); return true;
            }
            try
            {
                var scanned = ScanTargets();
                var previous = new Dictionary<string, Change>();
                foreach (var change in changes) previous[EntryKey(change.entry)] = change;
                var current = new List<Change>();
                foreach (var entry in scanned.entries)
                {
                    Change old;
                    if (previous.TryGetValue(EntryKey(entry), out old) && old.entry.original == entry.original)
                    {
                        old.entry.menuPath = entry.menuPath;
                        old.status = Validate(old);
                        current.Add(old);
                    }
                    else
                    {
                        var change = new Change { entry = entry };
                        change.status = Validate(change);
                        current.Add(change);
                    }
                }
                changes = current;
                SetNotice(current.Count == 0 ? "当前对象没有符合所选来源的菜单。" : "当前对象共 " + current.Count + " 条菜单文字。可复制翻译，也可直接填写译文。", MessageType.Info);
                RefreshPreview(); return true;
            }
            catch (Exception e)
            {
                changes.Clear();
                SetNotice("无法预览当前对象：" + e.Message, MessageType.Error);
                RefreshPreview(); return false;
            }
        }
        bool previewRefreshQueued;
        void OnHierarchyChange() { QueueScopeRefresh(); }
        void OnProjectChange() { QueueScopeRefresh(); }
        void QueueScopeRefresh()
        {
            if (previewRefreshQueued) return;
            previewRefreshQueued = true;
            EditorApplication.delayCall += () => {
                if (this == null) return;
                previewRefreshQueued = false;
                SyncPreviewToTargets();
            };
        }
        void LoadTranslation(TranslationFile file)
        {
            if (file == null || file.version != 1 || file.entries == null || file.entries.Count == 0)
                throw new InvalidOperationException("文件格式不正确或没有条目。");
            var incoming = new Dictionary<string, Entry>();
            foreach (var entry in file.entries)
            {
                if (entry == null) throw new InvalidOperationException("内容含有空条目。");
                string key = EntryKey(entry);
                if (incoming.ContainsKey(key)) throw new InvalidOperationException("内容含有重复条目，已停止读取。");
                incoming.Add(key, entry);
            }
            if (!SyncPreviewToTargets()) return;
            if (changes.Count == 0) throw new InvalidOperationException("请先添加需要翻译的对象，并勾选对应菜单来源。");
            int matched = 0;
            foreach (var change in changes)
            {
                Entry translated;
                if (!incoming.TryGetValue(EntryKey(change.entry), out translated) || translated.original != change.entry.original) continue;
                change.entry.translation = translated.translation;
                change.entry.restoreRawLabel = translated.restoreRawLabel;
                change.entry.rawLabel = translated.rawLabel;
                change.enabled = true;
                change.status = Validate(change);
                matched++;
            }
            search = ""; filter = 0;
            SetNotice(matched == 0 ? "译文与当前翻译对象不匹配，未导入。请复制当前对象的菜单重新翻译。"
                : "已匹配当前对象的 " + matched + " 条译文。" + (incoming.Count > matched ? "另有 " + (incoming.Count - matched) + " 条不属于当前范围或原文已变化，未导入。" : "请核对后应用。"),
                matched == 0 ? MessageType.Warning : MessageType.Info);
        }
        static void SavePersistentTarget(Object asset)
        {
            if (!EditorUtility.IsPersistent(asset)) return;
            var component = asset as Component;
            if (component != null) PrefabUtility.SavePrefabAsset(component.transform.root.gameObject);
            else AssetDatabase.SaveAssetIfDirty(asset);
        }
        void Apply()
        {
            if (!SyncPreviewToTargets()) return;
            var selected = new List<Change>();
            foreach (var c in changes)
            {
                if (!c.enabled || c.status != null) continue;
                c.status = Validate(c);
                if (c.status == null) selected.Add(c);
            }
            if (selected.Count == 0) { SetNotice("没有可以应用的条目。请检查译文和勾选状态。", MessageType.Warning); return; }
            var backup = NewFile();
            var assets = new HashSet<Object>();
            foreach (var c in selected)
            {
                Entry undoEntry;
                if (IsMa(c.asset))
                {
                    undoEntry = MakeMaEntry(c.asset, c.entry.translation, c.entry.original);
                    undoEntry.restoreRawLabel = true;
                    undoEntry.rawLabel = new SerializedObject(c.asset).FindProperty("label").stringValue;
                }
                else undoEntry = MakeEntry(c.asset, c.entry.controlIndex, c.entry.translation, c.entry.original);
                backup.entries.Add(undoEntry);
                assets.Add(c.asset);
            }
            var directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "MenuTranslationBackups");
            Directory.CreateDirectory(directory);
            var backupPath = Path.Combine(directory, "还原_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json");
            Write(backupPath, backup);
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("翻译表情菜单");
            foreach (var asset in assets) Undo.RegisterCompleteObjectUndo(asset, "翻译表情菜单");
            try
            {
                foreach (var c in selected)
                {
                    var so = new SerializedObject(c.asset);
                    if (IsMa(c.asset)) so.FindProperty("label").stringValue = c.entry.restoreRawLabel ? c.entry.rawLabel : c.entry.translation;
                    else Controls(so).GetArrayElementAtIndex(c.entry.controlIndex).FindPropertyRelative("name").stringValue = c.entry.translation;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(c.asset);
                    var component = c.asset as Component;
                    if (component != null && !EditorUtility.IsPersistent(component))
                    {
                        if (PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                        EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                    }
                }
                foreach (var asset in assets) SavePersistentTarget(asset);
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                foreach (var asset in assets) { EditorUtility.SetDirty(asset); SavePersistentTarget(asset); }
                throw;
            }
            foreach (var c in selected) { c.status = "已应用"; c.enabled = false; }
            lastBackup = backupPath;
            SetNotice("已应用 " + selected.Count + " 条翻译并自动备份。MA 场景对象或 Prefab 编辑模式中的修改，请保存对应场景或 Prefab。", MessageType.Info);
            Debug.Log("[菜单翻译助手] " + notice + " " + backupPath);
        }
    }
}
