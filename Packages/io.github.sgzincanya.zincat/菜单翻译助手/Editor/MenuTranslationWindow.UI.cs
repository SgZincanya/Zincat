using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Zincat.MenuTranslation
{
    public sealed partial class MenuTranslationWindow
    {
        VisualElement workspace, sourcePanel, previewRows, sourceDetails;
        Button manageSources;
        bool sourceExpanded;
        bool? lastCompact;
        Label summaryLabel, statusLabel;
        Button applyButton;
        TextField searchField;
        ToolbarMenu filterMenu;
        Color background, surface, textColor, secondary, line, accent;

        static Color Hex(string hex) { Color result; ColorUtility.TryParseHtmlString(hex, out result); return result; }
        void Palette()
        {
            bool dark = EditorGUIUtility.isProSkin;
            background = Hex(dark ? "#202124" : "#F5F5F7");
            surface = Hex(dark ? "#292B2F" : "#FFFFFF");
            textColor = Hex(dark ? "#F5F5F7" : "#1D1D1F");
            secondary = Hex(dark ? "#B8BDC7" : "#5F636B");
            line = Hex(dark ? "#44484F" : "#D9DCE3");
            accent = Hex(dark ? "#0A64D6" : "#0067D9");
        }
        Label Text(string value, int size = 13, bool muted = false)
        {
            var label = new Label(value);
            label.style.fontSize = size;
            label.style.unityFontStyleAndWeight = FontStyle.Normal;
            label.style.color = muted ? secondary : textColor;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.minWidth = 0;
            label.style.flexShrink = 0;
            return label;
        }
        static void Padding(VisualElement view, int value)
        {
            view.style.paddingLeft = value; view.style.paddingRight = value;
            view.style.paddingTop = value; view.style.paddingBottom = value;
        }
        void Card(VisualElement view)
        {
            view.style.backgroundColor = surface;
            view.style.borderTopLeftRadius = 10; view.style.borderTopRightRadius = 10;
            view.style.borderBottomLeftRadius = 10; view.style.borderBottomRightRadius = 10;
            view.style.borderTopWidth = 1; view.style.borderBottomWidth = 1;
            view.style.borderLeftWidth = 1; view.style.borderRightWidth = 1;
            view.style.borderTopColor = line; view.style.borderBottomColor = line;
            view.style.borderLeftColor = line; view.style.borderRightColor = line;
        }
        Button ActionButton(string title, Action action, bool primary = false)
        {
            var button = new Button(() => Run(action)) { text = title };
            button.style.height = 32; button.style.fontSize = 13;
            button.style.unityFontStyleAndWeight = FontStyle.Normal;
            button.style.marginLeft = 0; button.style.marginRight = 0;
            button.style.borderTopLeftRadius = 6; button.style.borderTopRightRadius = 6;
            button.style.borderBottomLeftRadius = 6; button.style.borderBottomRightRadius = 6;
            if (primary) { button.style.backgroundColor = accent; button.style.color = Color.white; }
            return button;
        }
        void BuildInterface()
        {
            Palette();
            var root = rootVisualElement;
            root.Clear(); root.style.backgroundColor = background;
            root.style.color = textColor; root.style.flexGrow = 1;
            root.style.unityFontStyleAndWeight = FontStyle.Normal;
            Padding(root, 18);
            var header = new VisualElement { style = { marginBottom = 12, flexShrink = 0, minHeight = 58 } };
            var title = Text("菜单翻译", 23);
            header.Add(title);
            var subtitle = Text("选择对象   →   复制到聊天   →   粘贴译文并核对", 13, true);
            subtitle.style.marginTop = 6; header.Add(subtitle); root.Add(header);

            workspace = new VisualElement { style = { flexGrow = 1, flexBasis = 0, minHeight = 0, minWidth = 0 } };
            root.Add(workspace);
            sourcePanel = new VisualElement(); Card(sourcePanel); Padding(sourcePanel, 14);
            sourcePanel.style.flexShrink = 0;
            workspace.Add(sourcePanel);
            BuildSources();

            var preview = new VisualElement { style = { flexGrow = 1, flexBasis = 0, minWidth = 0, minHeight = 0, overflow = Overflow.Hidden } };
            Card(preview); Padding(preview, 14); workspace.Add(preview);
            var previewHeader = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 0 } };
            var previewTitle = Text("翻译预览", 16);
            previewTitle.style.flexGrow = 1; previewTitle.style.flexShrink = 1; previewHeader.Add(previewTitle);
            var paste = ActionButton("粘贴翻译结果", PasteTranslation);
            paste.style.flexShrink = 0;
            paste.tooltip = "读取剪贴板中的完整翻译代码块，校验后显示预览。";
            previewHeader.Add(paste); preview.Add(previewHeader);
            summaryLabel = Text("", 12, true); summaryLabel.style.marginTop = 6; summaryLabel.style.marginBottom = 12;
            preview.Add(summaryLabel);
            var tools = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 8, flexShrink = 0, minWidth = 0 } };
            searchField = new TextField { value = search, tooltip = "搜索原文、译文或对象位置", style = { flexGrow = 1, minWidth = 50 } };
            searchField.label = "搜索"; searchField.labelElement.style.minWidth = 30;
            searchField.labelElement.style.width = 30;
            searchField.RegisterValueChangedCallback(evt => { search = evt.newValue; RenderRows(); });
            tools.Add(searchField);
            filterMenu = new ToolbarMenu { text = "全部条目" };
            foreach (var item in new[] { "全部条目", "可应用", "已跳过" })
            {
                string label = item;
                filterMenu.menu.AppendAction(label, _ => { filter = label == "可应用" ? 1 : label == "已跳过" ? 2 : 0; filterMenu.text = label; RenderRows(); });
            }
            filterMenu.style.flexShrink = 0;
            tools.Add(filterMenu); preview.Add(tools);
            var listTools = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 6, flexShrink = 0 } };
            var all = new Button(() => { foreach (var c in changes) if (Visible(c) && c.status == null) c.enabled = true; RefreshPreview(); }) { text = "全选当前列表" };
            var none = new Button(() => { foreach (var c in changes) if (Visible(c)) c.enabled = false; RefreshPreview(); }) { text = "取消勾选" };
            all.style.height = 24; none.style.height = 24; listTools.Add(all); listTools.Add(none); preview.Add(listTools);
            var scrollView = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1, flexBasis = 0, minHeight = 48, minWidth = 0 } };
            scrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scrollView.contentViewport.style.minWidth = 0;
            previewRows = scrollView.contentContainer;
            previewRows.style.width = Length.Percent(100);
            previewRows.style.maxWidth = Length.Percent(100);
            previewRows.style.minWidth = 0;
            preview.Add(scrollView);

            statusLabel = Text(notice, 13); statusLabel.style.marginTop = 12;
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            root.Add(statusLabel);
            var footer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 10, flexShrink = 0 } };
            var backups = ActionButton("备份与还原", ShowBackupMenu); footer.Add(backups);
            var safeHint = Text("仅修改显示文字 · 自动备份", 12, true);
            safeHint.style.flexGrow = 1; safeHint.style.flexShrink = 1; safeHint.style.marginLeft = 12; footer.Add(safeHint);
            applyButton = ActionButton("应用翻译", Apply, true); applyButton.style.minWidth = 150; footer.Add(applyButton);
            root.Add(footer);
            root.RegisterCallback<GeometryChangedEvent>(_ => AdaptLayout());
            AdaptLayout(); RefreshPreview();
        }
        void AdaptLayout()
        {
            if (workspace == null) return;
            bool compact = rootVisualElement.resolvedStyle.width > 0 ? rootVisualElement.resolvedStyle.width < 780 : position.width < 780;
            if (lastCompact == compact) return;
            lastCompact = compact;
            workspace.style.flexDirection = compact ? FlexDirection.Column : FlexDirection.Row;
            sourcePanel.style.width = compact ? new StyleLength(StyleKeyword.Auto) : new StyleLength(230);
            sourcePanel.style.marginRight = compact ? 0 : 12;
            sourcePanel.style.marginBottom = compact ? 10 : 0;
            if (sourceDetails != null) sourceDetails.style.display = !compact || sourceExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            if (manageSources != null)
            {
                manageSources.style.display = compact ? DisplayStyle.Flex : DisplayStyle.None;
                manageSources.text = sourceExpanded ? "收起对象" : "管理对象";
            }
        }
        void ChangeTargets(Object[] value)
        {
            var filtered = new List<Object>();
            foreach (var obj in value) if (obj != null && !filtered.Contains(obj)) filtered.Add(obj);
            targets = filtered.ToArray();
            initializedTargets = true;
            search = ""; filter = 0;
            SyncPreviewToTargets();
            BuildSources();
        }
        void BuildSources()
        {
            sourcePanel.Clear();
            var top = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, minWidth = 0 } };
            var heading = Text("翻译对象 · " + targets.Length, 15);
            heading.style.flexGrow = 1; top.Add(heading);
            manageSources = new Button(() => { sourceExpanded = !sourceExpanded; lastCompact = null; AdaptLayout(); }) { text = "管理对象" };
            manageSources.style.height = 26; manageSources.style.flexShrink = 0; top.Add(manageSources);
            sourcePanel.Add(top);
            sourceDetails = new VisualElement { style = { minWidth = 0, marginTop = 6 } };
            var targetList = new ScrollView(ScrollViewMode.Vertical) { style = { maxHeight = 84, minWidth = 0 } };
            targetList.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            targetList.contentContainer.style.width = Length.Percent(100);
            targetList.contentContainer.style.minWidth = 0;
            if (targets.Length == 0) targetList.Add(Text("拖入对象，或使用 Unity 当前选择。", 12, true));
            for (int i = 0; i < targets.Length; i++)
            {
                int index = i;
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, minWidth = 0, flexShrink = 0 } };
                var field = new ObjectField { objectType = typeof(Object), allowSceneObjects = true, value = targets[index] };
                field.style.minWidth = 0; field.style.flexGrow = 1; field.style.flexShrink = 1;
                field.RegisterValueChangedCallback(evt => { var copy = (Object[])targets.Clone(); copy[index] = evt.newValue; ChangeTargets(copy); });
                row.Add(field);
                var remove = new Button(() => {
                    var copy = new List<Object>(targets); copy.RemoveAt(index); ChangeTargets(copy.ToArray());
                }) { text = "移除", tooltip = "仅从翻译范围移除，不会删除 Unity 对象。" };
                remove.style.width = 44; remove.style.flexShrink = 0;
                row.Add(remove); targetList.Add(row);
            }
            sourceDetails.Add(targetList);
            var objectActions = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4, minWidth = 0 } };
            var selected = ActionButton("使用当前选择", () => ChangeTargets(Selection.objects));
            selected.style.flexGrow = 1; objectActions.Add(selected);
            var clear = ActionButton("清空对象", () => ChangeTargets(new Object[0]));
            clear.SetEnabled(targets.Length > 0); clear.style.marginLeft = 6; objectActions.Add(clear);
            sourceDetails.Add(objectActions); sourcePanel.Add(sourceDetails);
            if (!sourcePanel.ClassListContains("drop-ready"))
            {
                sourcePanel.AddToClassList("drop-ready");
                sourcePanel.RegisterCallback<DragUpdatedEvent>(evt => { DragAndDrop.visualMode = DragAndDropVisualMode.Copy; evt.StopPropagation(); });
                sourcePanel.RegisterCallback<DragPerformEvent>(evt => {
                    DragAndDrop.AcceptDrag(); ChangeTargets(DragAndDrop.objectReferences);
                    evt.StopPropagation();
                });
            }
            var scopes = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 8, marginBottom = 6, minWidth = 0 } };
            var vrc = new Toggle("VRC 菜单") { value = includeVrc };
            var ma = new Toggle("MA 菜单") { value = includeMa };
            foreach (var toggle in new[] { vrc, ma })
            {
                toggle.labelElement.style.minWidth = 0; toggle.labelElement.style.width = StyleKeyword.Auto;
                toggle.labelElement.style.flexShrink = 0;
                toggle.style.flexShrink = 0; toggle.style.marginRight = 12;
            }
            vrc.RegisterValueChangedCallback(evt => { includeVrc = evt.newValue; SyncPreviewToTargets(); });
            ma.RegisterValueChangedCallback(evt => { includeMa = evt.newValue; SyncPreviewToTargets(); });
            scopes.Add(vrc); scopes.Add(ma); sourcePanel.Add(scopes);
            var copyButton = ActionButton("复制待翻译内容", CopyMenus);
            copyButton.tooltip = "包含子菜单。复制后直接粘贴到聊天。";
            copyButton.SetEnabled(targets.Length > 0 && (includeVrc || includeMa));
            sourcePanel.Add(copyButton);
            vrc.RegisterValueChangedCallback(_ => copyButton.SetEnabled(targets.Length > 0 && (includeVrc || includeMa)));
            ma.RegisterValueChangedCallback(_ => copyButton.SetEnabled(targets.Length > 0 && (includeVrc || includeMa)));
            lastCompact = null; AdaptLayout();
            NormalizeTextWeight(sourcePanel);
        }
        bool Visible(Change c)
        {
            if (filter == 1 && c.status != null || filter == 2 && c.status == null) return false;
            return string.IsNullOrEmpty(search) || (c.entry.original + " " + c.entry.translation + " " + c.entry.menuPath).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        void RefreshPreview()
        {
            if (previewRows == null) return;
            if (searchField != null) searchField.SetValueWithoutNotify(search);
            if (filterMenu != null) filterMenu.text = filter == 1 ? "可应用" : filter == 2 ? "已跳过" : "全部条目";
            UpdateSummary(); RenderRows();
            NormalizeTextWeight(rootVisualElement);
        }
        static void NormalizeTextWeight(VisualElement container)
        {
            // Editor field labels can have their own bold style; normalize those too.
            // Keep Unity's native editor font/fallbacks rather than depending on OS fonts.
            container.Query<TextElement>().ForEach(element => element.style.unityFontStyleAndWeight = FontStyle.Normal);
        }
        void UpdateSummary()
        {
            int ready = 0, skipped = 0, ma = 0;
            foreach (var c in changes)
            {
                if (c.status != null) skipped++; else if (c.enabled) ready++;
                if (!string.IsNullOrEmpty(c.entry.objectId)) ma++;
            }
            summaryLabel.text = changes.Count == 0 ? "当前翻译对象没有可预览的菜单条目。" : "共 " + changes.Count + " 条  ·  MA " + ma + " / VRC " + (changes.Count - ma) + "  ·  勾选 " + ready + "  ·  跳过 " + skipped;
            statusLabel.text = (noticeType == MessageType.Error ? "未完成  ·  " : "") + notice;
            applyButton.text = "应用 " + ready + " 条翻译"; applyButton.SetEnabled(ready > 0);
        }
        void RenderRows()
        {
            previewRows.Clear();
            if (changes.Count == 0)
            {
                var empty = new VisualElement { style = { paddingTop = 45, paddingBottom = 35, alignItems = Align.Center } };
                empty.Add(Text(targets.Length == 0 ? "尚未添加翻译对象" : "没有找到菜单条目", 17));
                var description = Text(targets.Length == 0 ? "添加对象后，这里会自动显示对应菜单。" : "检查对象和 VRC / MA 菜单来源。", 13, true);
                description.style.marginTop = 10; empty.Add(description); previewRows.Add(empty); return;
            }
            int visible = 0;
            foreach (var c in changes)
            {
                if (!Visible(c)) continue;
                visible++;
                var row = new VisualElement { style = { paddingTop = 10, paddingBottom = 10, borderBottomWidth = 1, borderBottomColor = line, minWidth = 0, width = Length.Percent(100), flexShrink = 0, overflow = Overflow.Hidden } };
                var top = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart } };
                var check = new Toggle { value = c.enabled, tooltip = "勾选后应用此条译文", style = { width = 22, flexShrink = 0, marginTop = 2 } };
                check.SetEnabled(c.status == null); check.RegisterValueChangedCallback(evt => { c.enabled = evt.newValue; UpdateSummary(); });
                top.Add(check);
                var original = Text(c.entry.original, 13); original.style.flexGrow = 1; original.style.flexShrink = 1; original.style.minWidth = 0;
                original.tooltip = c.entry.original; top.Add(original);
                var type = Text(string.IsNullOrEmpty(c.entry.objectId) ? "VRC" : "MA", 11, true); type.style.marginLeft = 8;
                top.Add(type); row.Add(top);
                var translated = new TextField("译文") { value = c.entry.translation ?? "", multiline = true };
                translated.labelElement.style.minWidth = 30; translated.labelElement.style.width = 30;
                translated.style.minWidth = 0; translated.style.flexShrink = 0;
                var textInput = translated.Q<VisualElement>(className: "unity-base-field__input");
                if (textInput != null) { textInput.style.minWidth = 0; textInput.style.flexShrink = 1; }
                translated.style.marginTop = 7; translated.style.fontSize = 13;
                row.Add(translated);
                var status = Text(c.status ?? "", 12, true); status.style.marginTop = 5;
                status.style.display = c.status == null ? DisplayStyle.None : DisplayStyle.Flex; row.Add(status);
                translated.RegisterValueChangedCallback(evt => {
                    c.entry.translation = evt.newValue; c.status = Validate(c);
                    status.text = c.status ?? ""; status.style.display = c.status == null ? DisplayStyle.None : DisplayStyle.Flex;
                    check.SetEnabled(c.status == null); UpdateSummary();
                });
                var location = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 4 } };
                var path = Text(c.entry.menuPath ?? "", 11, true); path.tooltip = c.entry.menuPath;
                path.style.whiteSpace = WhiteSpace.NoWrap; path.style.overflow = Overflow.Hidden;
                path.style.textOverflow = TextOverflow.Ellipsis; path.style.flexGrow = 1; path.style.flexShrink = 1; path.style.minWidth = 0;
                location.Add(path);
                var locate = new Button(() => { var obj = Resolve(c.entry); if (obj != null) EditorGUIUtility.PingObject(obj); }) { text = "定位" };
                locate.style.flexShrink = 0; locate.style.marginLeft = 8; location.Add(locate); row.Add(location); previewRows.Add(row);
            }
            if (visible == 0) previewRows.Add(Text("没有符合当前搜索或筛选的条目。", 13, true));
            NormalizeTextWeight(previewRows);
        }
        void ShowBackupMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("读取还原备份…"), false, () => Run(Import));
            menu.AddItem(new GUIContent("打开备份位置"), false, () => Run(() => {
                string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "MenuTranslationBackups");
                if (!Directory.Exists(path)) { SetNotice("尚未生成备份。应用翻译前会自动创建。", MessageType.Info); return; }
                EditorUtility.RevealInFinder(string.IsNullOrEmpty(lastBackup) || !File.Exists(lastBackup) ? path : lastBackup);
            }));
            menu.ShowAsContext();
        }
    }
}
