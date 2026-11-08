#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Zincat.ColorOverview
{
    public sealed partial class ColorOverviewWindow
    {
        private int uiSource;
        private bool uiSettings;
        private string uiSearch = "";
        private GUIStyle uiHeading, uiStatusStyle;

        private void ReadResult(string text)
        {
            AiAction(() =>
            {
                aiPreview = null; aiPendingText = text;
                aiPreview = ResolveAiResult(text);
            });
        }
        private void ApplyResult()
        {
            AiAction(() =>
            {
                sets.Clear(); sets.AddRange(aiPreview);
                SaveCurrentList();
                status = null; aiStatus = "已载入 " + sets.Count + " 组";
                aiPreview = null; aiShowText = false;
            });
        }
        private void ShowMoreMenu(Rect rect)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("输入 AI 返回文本…"), false, () => { aiShowText = !aiShowText; Repaint(); });
            if (!string.IsNullOrEmpty(aiSnapshot?.Prompt))
                menu.AddItem(new GUIContent("重新复制上次提示词"), false, () => EditorGUIUtility.systemCopyBuffer = aiSnapshot.Prompt);
            else menu.AddDisabledItem(new GUIContent("重新复制上次提示词"));
            menu.AddSeparator("");
            if (clearUndo != null) menu.AddItem(new GUIContent("撤销上次清除"), false, UndoClear);
            menu.AddItem(new GUIContent("使用说明"), false, () => EditorUtility.DisplayDialog("使用说明",
                "选中衣服资源目录或预制体 → 复制提示词并发给 AI → 粘贴 AI 返回的完整代码块 → 核对分组并生成。\n\n选择具体套装或角色目录可以减少字符数。路径筛选、角度与隐藏部件选项在“截图与识别设置”里。\n\n截图直接绘制资源原始姿态，不执行穿衣构建。AI 分组可展开核对、取消条目或定位资源；未识别项不会自动补猜。", "关闭"));
            menu.DropDown(rect);
        }
        private void DrawOverviewUI()
        {
            if (uiHeading == null) uiHeading = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16 };
            if (uiStatusStyle == null) uiStatusStyle = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip, wordWrap = false };
            float oldLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 92;
            try
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Space(10);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(12);
                        using (new EditorGUILayout.VerticalScope())
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Label("配色总览", uiHeading, GUILayout.Height(28));
                                GUILayout.FlexibleSpace();
                                Rect moreRect = GUILayoutUtility.GetRect(66, 26, GUILayout.Width(66), GUILayout.Height(26));
                                if (GUI.Button(moreRect, "更多 ▾")) ShowMoreMenu(moreRect);
                            }
                            GUILayout.Space(6);
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                uiSource = EditorGUILayout.Popup(uiSource, new[] { "选中资源", "最近导入" }, GUILayout.Width(112), GUILayout.Height(28));
                                if (GUILayout.Button(new GUIContent("复制提示词", "只复制工程路径和限定查找目录，交给能读本机文件的 AI。"), GUILayout.Height(28)))
                                    AiAction(() => BuildAiPrompt(uiSource == 0 ? SelectedScopes() : RecentScopes()));
                                if (GUILayout.Button(new GUIContent("粘贴 AI 结果", "读取剪贴板中的完整分组代码块。"), GUILayout.Height(28)))
                                    ReadResult(EditorGUIUtility.systemCopyBuffer);
                            }
                            if (aiShowText)
                            {
                                GUILayout.Space(6);
                                aiPasteText = EditorGUILayout.TextArea(aiPasteText, GUILayout.Height(64));
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    if (GUILayout.Button("解析文本", GUILayout.Height(26))) ReadResult(aiPasteText);
                                    if (GUILayout.Button("收起", GUILayout.Width(64), GUILayout.Height(26))) aiShowText = false;
                                }
                            }
                            if (aiError && !string.IsNullOrEmpty(aiStatus))
                                EditorGUILayout.HelpBox(aiStatus, MessageType.Warning);
                            else if (!string.IsNullOrEmpty(aiStatus))
                                GUILayout.Label(new GUIContent(aiStatus, aiStatus), uiStatusStyle, GUILayout.Height(21));
                            if (aiPreview != null)
                            {
                                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                                {
                                    GUILayout.Label("待载入 · " + aiPreview.Count + " 组 · " + aiPreview.Sum(s => s.Variants.Count) + " 项", EditorStyles.boldLabel);
                                    GUILayout.Label(string.Join(" / ", aiPreview.Select(s => s.Name)), EditorStyles.wordWrappedLabel);
                                    using (new EditorGUILayout.HorizontalScope())
                                    {
                                        if (GUILayout.Button("载入分组", GUILayout.Height(28))) ApplyResult();
                                        if (GUILayout.Button("取消", GUILayout.Width(64), GUILayout.Height(28))) { aiPreview = null; aiStatus = null; }
                                    }
                                }
                            }
                            GUILayout.Space(8);
                            bool listChanged = false;
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Label("截图列表", EditorStyles.boldLabel, GUILayout.Width(66));
                                uiSearch = EditorGUILayout.TextField(new GUIContent("", "搜索分组、配色或资源路径"), uiSearch, EditorStyles.toolbarSearchField);
                                if (GUILayout.Button("全选", EditorStyles.miniButtonLeft, GUILayout.Width(44), GUILayout.Height(24)))
                                    { foreach (var set in sets) { set.Enabled = true; foreach (var v in set.Variants) v.Enabled = true; } listChanged = true; }
                                if (GUILayout.Button("全不选", EditorStyles.miniButtonRight, GUILayout.Width(52), GUILayout.Height(24)))
                                    { foreach (var set in sets) set.Enabled = false; listChanged = true; }
                                using (new EditorGUI.DisabledScope(sets.Count == 0))
                                    if (GUILayout.Button(new GUIContent("全部清除", "只清空截图列表，不删除工程资源。"), EditorStyles.miniButton, GUILayout.Width(64), GUILayout.Height(24)))
                                    { BeginClear(); sets.Clear(); listChanged = true; }
                            }
                            GUILayout.Space(4);
                            scroll = EditorGUILayout.BeginScrollView(scroll, GUIStyle.none, GUI.skin.verticalScrollbar);
                            foreach (var set in sets.ToArray())
                            {
                                bool groupMatches = string.IsNullOrEmpty(uiSearch) || set.Name.IndexOf(uiSearch, StringComparison.OrdinalIgnoreCase) >= 0;
                                var visible = set.Variants.Where(v => groupMatches || v.Name.IndexOf(uiSearch, StringComparison.OrdinalIgnoreCase) >= 0 || v.Path.IndexOf(uiSearch, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                                if (!groupMatches && visible.Length == 0) continue;
                                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                                {
                                    using (new EditorGUILayout.HorizontalScope(GUILayout.Height(28)))
                                    {
                                        bool enabled = EditorGUILayout.Toggle(set.Enabled, GUILayout.Width(18));
                                        if (enabled != set.Enabled) { set.Enabled = enabled; listChanged = true; }
                                        set.Expanded = EditorGUILayout.Foldout(set.Expanded, set.Name, true, EditorStyles.foldout);
                                        GUILayout.Label(set.Variants.Count(v => v.Enabled) + " / " + set.Variants.Count, EditorStyles.miniLabel, GUILayout.Width(56));
                                        if (GUILayout.Button(new GUIContent("清除", "移出本组，不删除资源。"), EditorStyles.miniButton, GUILayout.Width(46), GUILayout.Height(23)))
                                        { BeginClear(); sets.Remove(set); listChanged = true; }
                                    }
                                    if (set.Expanded || !string.IsNullOrEmpty(uiSearch))
                                    foreach (var v in visible)
                                    {
                                        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(26)))
                                        {
                                            GUILayout.Space(18);
                                            bool enabled = EditorGUILayout.ToggleLeft(new GUIContent(v.Name + (v.Swaps.Count > 0 ? " · 材质配色" : ""), v.Path), v.Enabled);
                                            if (enabled != v.Enabled) { v.Enabled = enabled; listChanged = true; }
                                            if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(46), GUILayout.Height(23)))
                                                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(v.Path));
                                            if (GUILayout.Button(new GUIContent("清除", "移出此配色，不删除资源。"), EditorStyles.miniButton, GUILayout.Width(46), GUILayout.Height(23)))
                                            { BeginClear(); set.Variants.Remove(v); if (set.Variants.Count == 0) sets.Remove(set); listChanged = true; }
                                        }
                                    }
                                }
                            }
                            if (sets.Count == 0) GUILayout.Label("列表为空", EditorStyles.centeredGreyMiniLabel);
                            EditorGUILayout.EndScrollView();
                            if (listChanged) AiAction(SaveCurrentList);
                            GUILayout.Space(6);
                            uiSettings = EditorGUILayout.Foldout(uiSettings, "截图与识别设置", true);
                            if (uiSettings)
                            {
                                resolution = EditorGUILayout.IntPopup("每格清晰度", resolution, new[] { "384", "512", "768", "1024" }, new[] {384, 512, 768, 1024});
                                columns = EditorGUILayout.IntSlider("每行数量", columns, 2, 8);
                                yaw = EditorGUILayout.Slider("水平角度", yaw, -180, 180);
                                pitch = EditorGUILayout.Slider("俯仰角度", pitch, -45, 45);
                                includeInactive = EditorGUILayout.Toggle(new GUIContent("显示隐藏部件", "包含预制体默认隐藏的网格，互斥款式可能重叠。"), includeInactive);
                                aiFilter = EditorGUILayout.TextField(new GUIContent("识别路径筛选", "留空不过滤；填写角色名可减少候选预制体。"), aiFilter);
                            }
                            GUILayout.Space(4);
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Label("保存到", GUILayout.Width(48));
                                output = EditorGUILayout.TextField(output);
                                if (GUILayout.Button("选择…", GUILayout.Width(58), GUILayout.Height(24)))
                                {
                                    string chosen = EditorUtility.OpenFolderPanel("选择保存位置", output, "");
                                    if (!string.IsNullOrEmpty(chosen)) output = chosen;
                                }
                            }
                            GUILayout.Space(6);
                            int selectedGroups = sets.Count(s => s.Enabled && s.Variants.Any(v => v.Enabled));
                            int selectedItems = sets.Where(s => s.Enabled).Sum(s => s.Variants.Count(v => v.Enabled));
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Label(selectedGroups + " 组 · " + selectedItems + " 项", EditorStyles.miniLabel);
                                GUILayout.FlexibleSpace();
                                using (new EditorGUI.DisabledScope(!Directory.Exists(output)))
                                    if (GUILayout.Button("打开目录", GUILayout.Width(84), GUILayout.Height(30))) EditorUtility.RevealInFinder(output);
                                using (new EditorGUI.DisabledScope(selectedItems == 0 || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling))
                                    if (GUILayout.Button("生成总览图", GUILayout.Width(142), GUILayout.Height(30))) Run();
                            }
                            if (!string.IsNullOrEmpty(status) && !status.StartsWith("当前") && !status.StartsWith("已扫描"))
                                GUILayout.Label(new GUIContent(status, status), uiStatusStyle, GUILayout.Height(21));
                        }
                        GUILayout.Space(12);
                    }
                    GUILayout.Space(10);
                }
            }
            finally { EditorGUIUtility.labelWidth = oldLabelWidth; }
        }
    }
}
#endif

