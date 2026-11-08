#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;

namespace Zincat.AvatarSplitTest
{
    public sealed class AvatarSplitWindow : EditorWindow
    {
        [SerializeField] AvatarSplitPlan plan;
        [SerializeField] AvatarSplitConfig avatarConfig;
        [SerializeField] string editSourceId, editConfigId;
        [SerializeField] int activeGroup;
        [SerializeField] string search = "";
        GameObject source;
        bool hasPlaceholder;
        string sourceName, lastAppearance;
        List<OutfitMenuChoice> choices = new List<OutfitMenuChoice>();
        Dictionary<string, OutfitMenuChoice> choicesById = new Dictionary<string, OutfitMenuChoice>();
        readonly Dictionary<string, Label> summaries = new Dictionary<string, Label>();
        readonly Dictionary<string, Label> groupNames = new Dictionary<string, Label>();
        [SerializeField] bool showUploadInfo = true;
        Image coverThumb;
        Label coverState, extraState, queuedState;
        Button retryExtra, skipExtra, photoExtra, directExtra;
        Label uploadSelectionLabel;
        Label detailName;
        VisualElement sourcePanel, actions, workspace, sidebar, detail;
        ScrollView groupList, detailScroll;
        VisualElement outfitList;
        ObjectField sourceField;
        Label saveLabel, notice, stageLabel, countLabel;
        Button generateButton, uploadButton;
        string statusText;
        bool localWork, compact, responsiveReady;
        IVisualElementScheduledItem poll;
        Color noticeColor;

        [MenuItem("Zincat/衣服菜单/模型拆分", false, 1020)]
        public static void Open()
        {
            var window = GetWindow<AvatarSplitWindow>("模型拆分");
            var selectedConfig = Selection.activeGameObject ? Selection.activeGameObject.GetComponent<AvatarSplitConfig>() : null;
            if (SplitIds.Editing && selectedConfig && selectedConfig.Source)
            {
                window.avatarConfig = selectedConfig; window.plan = SplitConfigStore.Read(selectedConfig);
                window.LoadPlanData(); window.BuildUI();
            }
            window.titleContent = new GUIContent("模型拆分");
            window.minSize = new Vector2(560, 620); window.Show();
        }
        void OnEnable()
        {
            titleContent = new GUIContent("模型拆分");
            Undo.undoRedoPerformed += UndoChanged;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }
        void OnDisable()
        {
            Undo.undoRedoPerformed -= UndoChanged;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            EditorApplication.update -= RestoreWhenReady;
            poll?.Pause();
            if (SplitIds.Editing) SplitIds.Flush();
        }
        void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode && source && plan)
            {
                editSourceId = plan.sourceId;
                if (avatarConfig) editConfigId = SplitIds.Of(avatarConfig);
            }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.update -= RestoreWhenReady;
                EditorApplication.update += RestoreWhenReady;
            }
            UpdateActions();
        }
        void RestoreWhenReady()
        {
            if (!this) { EditorApplication.update -= RestoreWhenReady; return; }
            if (!SplitIds.Editing || EditorApplication.isUpdating || EditorApplication.isCompiling) return;
            EditorApplication.update -= RestoreWhenReady;
            source = null; avatarConfig = null; plan = null;
            statusText = null; LoadPlanData(); BuildUI();
        }
        internal void RestoreAfterPlayForTest() => RestoreWhenReady();
        public void CreateGUI()
        {
            LoadPlanData(); BuildUI();
            poll?.Pause(); poll = rootVisualElement.schedule.Execute(Tick).Every(750);
        }
        internal void ConfigureForTest(AvatarSplitPlan value) { plan = value; CreateGUI(); }
        internal void LayoutForTest(float width) { UpdateResponsive(width); }

        void Run(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                SetNotice(e.Message, true);
                if (!(e is InvalidOperationException)) Debug.LogException(e);
            }
            UpdateActions();
        }
        void RunLater(string label, Action action)
        {
            if (localWork) return;
            localWork = true; SetNotice(label); UpdateActions();
            rootVisualElement.schedule.Execute(() =>
            {
                try { Run(action); }
                finally { localWork = false; UpdateActions(); }
            }).StartingIn(30);
        }
        void SetNotice(string text, bool error = false)
        {
            statusText = text; noticeColor = error ? (SplitUi.Dark ? new Color32(255,164,154,255) : new Color32(163,36,30,255)) : SplitUi.Muted;
            if (notice == null) return;
            notice.text = text ?? ""; notice.style.color = noticeColor;
            notice.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
        void LoadPlanData()
        {
            if (!SplitIds.Editing) return;
            if (!avatarConfig && !string.IsNullOrEmpty(editConfigId)) avatarConfig = SplitIds.Resolve(editConfigId) as AvatarSplitConfig;
            if (!avatarConfig && !plan && !string.IsNullOrEmpty(editSourceId))
            {
                var editSource = SplitIds.Resolve(editSourceId) as GameObject;
                if (editSource) plan = SplitSourceStore.Find(editSource);
            }
            if (avatarConfig) plan = SplitConfigStore.Read(avatarConfig);
            if (plan && !plan.config && AssetDatabase.Contains(plan))
            {
                var oldSource = SplitIds.Resolve(plan.sourceId) as GameObject;
                if (oldSource) plan = SplitSourceStore.Find(oldSource) ?? plan;
            }
            if (plan && plan.config) avatarConfig = plan.config;
            source = plan ? SplitIds.Resolve(plan.sourceId) as GameObject : null;
            choices.Clear(); choicesById.Clear();
            if (!source) return;
            sourceName = source.name;
            editSourceId = plan.sourceId;
            if (avatarConfig) editConfigId = SplitIds.Of(avatarConfig);
            try
            {
                SplitNaming.Apply(plan, source);
                choices = AvatarOutfitMenus.Read(source, out var placeholder);
                hasPlaceholder = placeholder;
                choicesById = choices.ToDictionary(c => c.Id);
                UpdateChoiceNames(); SplitNaming.Apply(plan, source);
            }
            catch (Exception e) { statusText = e.Message; }
        }
        void BuildUI()
        {
            var root = rootVisualElement; root.Clear();
            root.style.backgroundColor = SplitUi.Background; root.style.flexGrow = 1;
            root.style.paddingLeft = root.style.paddingRight = 16;
            root.style.paddingTop = root.style.paddingBottom = 14;
            var heading = SplitUi.Row(); heading.style.marginBottom = 5;
            var title = SplitUi.Label("模型拆分", 22); title.style.flexGrow = 1; heading.Add(title);
            saveLabel = SplitUi.Label("配置随场景保存", 12, true); heading.Add(saveLabel); root.Add(heading);
            var intro = SplitUi.Label("同一个角色，按衣服组合生成不同版本。", 13, true);
            intro.style.marginBottom = 12; root.Add(intro);

            sourcePanel = new VisualElement(); SplitUi.Box(sourcePanel, 12); sourcePanel.style.marginBottom = 10;
            sourceField = new ObjectField("源 Avatar") { objectType = typeof(VRCAvatarDescriptor), allowSceneObjects = true,
                value = source ? source.GetComponent<VRCAvatarDescriptor>() : null, name = "split-source" };
            sourceField.RegisterValueChangedCallback(evt =>
            {
                Run(() => ChooseSource(evt.newValue as VRCAvatarDescriptor));
                sourceField?.SetValueWithoutNotify(source ? source.GetComponent<VRCAvatarDescriptor>() : null);
            });
            sourcePanel.Add(sourceField);
            var sourceTools = SplitUi.Row(); sourceTools.style.flexWrap = Wrap.Wrap; sourceTools.style.marginTop = 8;
            var naming = SplitUi.Label(source ? "命名：" + source.name + "V1｜已选衣服名称" : "拖入角色，自动读取已绑定的衣服菜单", 12, true);
            naming.style.flexGrow = 1; naming.style.minWidth = 180; sourceTools.Add(naming);
            sourceTools.Add(SplitUi.Button("刷新菜单", () => Run(RefreshMenus)));
            var photo = SplitUi.Button("分组拍照…", () => SplitPhotoWindow.Open(source)); photo.SetEnabled(source); sourceTools.Add(photo);
            if (!source && plan && !string.IsNullOrEmpty(plan.sourceScene))
                sourceTools.Add(SplitUi.Button("打开源场景", () => Run(() =>
                {
                    if (!UnityEngine.SceneManagement.SceneManager.GetSceneByPath(plan.sourceScene).isLoaded)
                        EditorSceneManager.OpenScene(plan.sourceScene, OpenSceneMode.Additive);
                    LoadPlanData(); BuildUI();
                })));
            sourcePanel.Add(sourceTools);
            var more = new Foldout { text = "配置存放位置", value = false };
            more.style.marginTop = 5;
            more.Add(SplitUi.Label("分组选择和蓝图号保存在源 Avatar 下的“衣服拆分配置”中，随完整 Avatar 一起复制。", 12, true));
            var locateConfig = SplitUi.Button("定位 Avatar 配置", () => Run(() =>
            {
                if (plan && plan.config) { Selection.activeGameObject = plan.config.gameObject; EditorGUIUtility.PingObject(plan.config.gameObject); }
            }));
            more.Add(locateConfig); sourcePanel.Add(more); root.Add(sourcePanel);

            actions = SplitUi.Row(); actions.style.flexWrap = Wrap.Wrap;
            generateButton = SplitUi.Button("生成拆分结果", () => RunLater("正在生成拆分结果…", () =>
            {
                SplitIds.Flush(); AvatarSplitBuilder.Generate(plan);
                DrawGroups(); DrawDetails();
                SetNotice("已更新源场景中的拆分结果。选择一个版本预览，检查后再打开上传队列。");
            }), true);
            generateButton.style.flexGrow = 1; generateButton.style.flexBasis = 180; generateButton.style.height = 34;
            generateButton.tooltip = "在源场景中更新本方案的副本，保留原角色和各组蓝图。";
            uploadButton = SplitUi.Button("打开 CAU 上传队列…", () => RunLater("正在准备上传队列…", () =>
            {
                SplitIds.Flush(); CauBridge.PrepareQueue(plan); DrawGroups(); DrawDetails(); SetNotice("队列已交给 CAU。封面与名称将在模型上传成功后同步，请检查后再开始上传。");
            }));
            uploadButton.name = "split-upload";
            uploadButton.style.flexGrow = 1; uploadButton.style.flexBasis = 180; uploadButton.style.height = 34;
            uploadButton.tooltip = "检查并确认队列后打开 CAU；不会立即上传。";
            actions.Add(generateButton); actions.Add(uploadButton); root.Add(actions);
            stageLabel = SplitUi.Label("", 12, true); stageLabel.style.marginTop = 6; stageLabel.style.marginBottom = 8; root.Add(stageLabel);
            notice = SplitUi.Label("", 13, true); notice.style.marginBottom = 8; root.Add(notice); SetNotice(statusText);

            workspace = new VisualElement { name = "split-workspace" };
            workspace.style.flexGrow = 1; workspace.style.minHeight = 180;
            sidebar = new VisualElement { name = "split-groups" }; SplitUi.Box(sidebar, 10);
            var listHeading = SplitUi.Row(); listHeading.style.marginBottom = 8;
            var groupTitle = SplitUi.Label("版本分组", 14); groupTitle.style.flexGrow = 1; listHeading.Add(groupTitle);
            listHeading.Add(SplitUi.Button("添加", () => Run(() => AddGroup(null)))); sidebar.Add(listHeading);
            groupList = new ScrollView(ScrollViewMode.Vertical); groupList.style.flexGrow = 1; sidebar.Add(groupList);
            detail = new VisualElement { name = "split-detail" }; SplitUi.Box(detail, 14); detail.style.flexGrow = 1; detail.style.minWidth = 0;
            workspace.Add(sidebar); workspace.Add(detail); root.Add(workspace);
            DrawGroups(); DrawDetails(); SplitUi.Apply(root); responsiveReady = false; UpdateResponsive(position.width); UpdateActions();
            lastAppearance = EditorGUIUtility.isProSkin.ToString();
            root.RegisterCallback<GeometryChangedEvent>(evt => UpdateResponsive(evt.newRect.width));
        }
        void ChooseSource(VRCAvatarDescriptor avatar)
        {
            if (!SplitIds.Editing) throw new InvalidOperationException("请退出游戏模式后选择源 Avatar。");
            if (!avatar) { if (sourceField != null) sourceField.SetValueWithoutNotify(source ? source.GetComponent<VRCAvatarDescriptor>() : null); return; }
            if (EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid())
                throw new InvalidOperationException("请拖入场景中的 Avatar。");
            AvatarSplitBuilder.Collection(avatar.gameObject);
            SplitSourceStore.EnsureSaved(avatar.gameObject);
            var nextId = SplitIds.Of(avatar.gameObject);
            if (!plan || plan.sourceId != nextId) plan = SplitSourceStore.Find(avatar.gameObject) ?? SplitSourceStore.Create(avatar.gameObject);
            avatarConfig = plan.config;
            activeGroup = 0; statusText = null; LoadPlanData(); BuildUI();
        }
        void RefreshMenus()
        {
            if (!SplitIds.Editing) throw new InvalidOperationException("请退出游戏模式后刷新衣服菜单。");
            if (!source) throw new InvalidOperationException("先拖入源 Avatar，或打开方案的源场景。");
            SplitSourceStore.EnsureSaved(source); plan.sourceScene = source.scene.path;
            LoadPlanData(); BuildUI(); SetNotice("已读取 " + choices.Count + " 套衣服，按菜单名称显示。");
        }
        void UpdateChoiceNames()
        {
            foreach (var g in plan.groups)
            {
                bool changed = false;
                var names = new List<string>();
                for (int i = 0; i < g.outfitIds.Count; i++)
                {
                    string id = g.outfitIds[i];
                    string previous = i < g.outfitNames.Count ? g.outfitNames[i] : "（已失效）";
                    if (!choicesById.TryGetValue(id, out var choice))
                    {
                        // Scene Save As changes scene GUIDs. Recover only when
                        // the same saved menu name has one unambiguous match.
                        var matches = choices.Where(c => c.Name == previous).ToArray();
                        if (matches.Length == 1 && !g.outfitIds.Contains(matches[0].Id))
                        { choice = matches[0]; g.outfitIds[i] = choice.Id; changed = true; }
                    }
                    names.Add(choice != null ? choice.Name : previous);
                }
                if (!changed && names.SequenceEqual(g.outfitNames)) continue;
                g.outfitNames = names; g.Invalidate(); SplitIds.SaveLater(plan);
            }
        }
        void DrawGroups()
        {
            if (groupList == null) return;
            groupList.Clear(); summaries.Clear(); groupNames.Clear();
            if (!plan || plan.groups.Count == 0) { groupList.Add(SplitUi.Label("添加分组，选择要保留的衣服。", 13, true)); return; }
            var uploadTools = SplitUi.Row(); uploadTools.style.marginBottom = 6;
            uploadSelectionLabel = SplitUi.Label("", 12, true); uploadSelectionLabel.style.flexGrow = 1; uploadTools.Add(uploadSelectionLabel);
            uploadTools.Add(SplitUi.Button("全选上传", () => Run(() =>
            {
                Undo.RecordObject(plan, "全选上传分组"); foreach (var group in plan.groups) group.excludeFromUpload = false;
                SplitIds.SaveLater(plan); DrawGroups();
            }))); groupList.Add(uploadTools);
            activeGroup = Mathf.Clamp(activeGroup, 0, plan.groups.Count - 1);
            for (int i = 0; i < plan.groups.Count; i++)
            {
                int index = i; var g = plan.groups[i];
                var selectionRow = SplitUi.Row(); selectionRow.style.alignItems = Align.Center; selectionRow.style.minWidth = 0;
                var uploadToggle = new Toggle { value = g.UploadSelected, name = "split-upload-select-" + g.id, tooltip = "勾选加入下次 CAU 上传队列；取消不影响拆分或拍照。" };
                uploadToggle.style.width = 22; uploadToggle.style.flexShrink = 0; uploadToggle.style.marginRight = 4;
                uploadToggle.RegisterValueChangedCallback(evt => Run(() =>
                {
                    Undo.RecordObject(plan, "选择上传分组"); g.excludeFromUpload = !evt.newValue; SplitIds.SaveLater(plan);
                })); selectionRow.Add(uploadToggle);
                var row = new Button(() => { activeGroup = index; DrawGroups(); DrawDetails(); UpdateActions(); }) { name = "split-group-" + g.id };
                row.AddToClassList("wardrobe-state-button");
                row.style.flexDirection = FlexDirection.Column; row.style.alignItems = Align.FlexStart;
                row.style.minHeight = 56; row.style.marginBottom = 6; row.style.flexGrow = 1; row.style.flexShrink = 1; row.style.minWidth = 0;
                row.style.paddingLeft = row.style.paddingRight = 10; row.style.paddingTop = row.style.paddingBottom = 8;
                row.style.backgroundColor = index == activeGroup ? SplitUi.Selected : SplitUi.Surface;
                row.style.borderTopLeftRadius = row.style.borderTopRightRadius = 6;
                row.style.borderBottomLeftRadius = row.style.borderBottomRightRadius = 6;
                SplitUi.Outline(row, index == activeGroup ? new Color32(124,182,255,255) : SplitUi.Border);
                var title = SplitUi.Label(g.name, 13); title.pickingMode = PickingMode.Ignore; title.style.alignSelf = Align.Stretch;
                title.tooltip = g.name; groupNames[g.id] = title;
                row.Add(title);
                var summary = SplitUi.Label(GroupStatus(g), 12, true); summary.pickingMode = PickingMode.Ignore;
                summary.style.marginTop = 3; row.Add(summary); summaries[g.id] = summary;
                selectionRow.Add(row); groupList.Add(selectionRow);
            }
            if (uploadSelectionLabel != null) uploadSelectionLabel.text = "上传 " + plan.groups.Count(g => g.UploadSelected) + "/" + plan.groups.Count;
            SplitUi.Apply(groupList);
        }
        static string GroupStatus(SplitGroup g) => g.outfitIds.Count + " 套衣服 · " +
            (g.generatedRevision != g.revision || string.IsNullOrEmpty(g.generatedAvatarId) ? "待生成" : g.uploadSucceeded ? "已上传" : "可预览");
        SplitGroup Current => plan && activeGroup >= 0 && activeGroup < plan.groups.Count ? plan.groups[activeGroup] : null;
        void DrawDetails()
        {
            if (detail == null) return;
            detail.Clear(); coverThumb = null; coverState = extraState = queuedState = null; retryExtra = skipExtra = photoExtra = directExtra = null; var g = Current;
            if (g == null)
            {
                detail.Add(SplitUi.Label("先选择源 Avatar", 18));
                var empty = SplitUi.Label("读取它的衣服菜单后，在这里勾选每个版本需要的衣服。", 13, true); empty.style.marginTop = 10; detail.Add(empty);
                return;
            }
            var head = SplitUi.Row();
            var name = SplitUi.Label(g.name, 17); detailName = name;
            name.tooltip = g.name; name.style.flexGrow = 1; name.style.flexShrink = 1; name.style.minWidth = 0; head.Add(name);
            head.Add(SplitUi.Button("分组选项…", () => GroupMenu(g))); detail.Add(head);
            countLabel = SplitUi.Label("", 12, true); countLabel.style.marginTop = 5; countLabel.style.marginBottom = 10; detail.Add(countLabel);
            DrawUploadInfo(g);
            var tools = SplitUi.Row(); tools.style.flexWrap = Wrap.Wrap;
            var find = new ToolbarSearchField { value = search, name = "split-search", tooltip = "搜索衣服菜单名称" };
            find.style.flexGrow = 1; find.style.minWidth = 120; find.style.marginRight = 8;
            find.RegisterValueChangedCallback(evt => { search = evt.newValue; DrawOutfits(); }); tools.Add(find);
            tools.Add(SplitUi.Button("全选", () => Run(() => SetSelection(g, choices.Select(c => c.Id)))));
            tools.Add(SplitUi.Button("清空", () => Run(() => SetSelection(g, Array.Empty<string>())))); detail.Add(tools);
            detailScroll = new ScrollView(ScrollViewMode.Vertical); detailScroll.style.flexGrow = 1; detailScroll.style.marginTop = 8;
            detailScroll.style.flexBasis = 0; detailScroll.style.minHeight = 40;
            detailScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            detailScroll.contentContainer.style.width = Length.Percent(100);
            detailScroll.contentContainer.style.minWidth = 0;
            outfitList = new VisualElement(); detailScroll.Add(outfitList); detail.Add(detailScroll); DrawOutfits();
            if (!string.IsNullOrEmpty(g.generatedAvatarId))
            {
                var preview = SplitUi.Button("预览此版本", () => Run(() => Preview(g)));
                preview.tooltip = "只启用这一个生成副本，其余生成副本关闭。"; preview.style.marginTop = 8; detail.Add(preview);
            }
            var advanced = new Foldout { text = "蓝图与检查", value = false, name = "split-advanced" };
            advanced.style.marginTop = 8;
            var id = new TextField("蓝图号") { value = g.blueprintId ?? "", name = "split-blueprint" };
            id.tooltip = "填写完整 avtr_ 蓝图号更新已有 Avatar；留空则新建。仅保存设置，不会上传。";
            advanced.Add(id);
            var blueprintActions = SplitUi.Row();
            blueprintActions.Add(SplitUi.Button("应用蓝图号", () => Run(() =>
            {
                CauBridge.AssignBlueprint(plan, g, id.value); DrawDetails();
                SetNotice(string.IsNullOrEmpty(g.blueprintId) ? "蓝图号已清空，下次上传将创建新 Avatar。" : "蓝图号已保存，下次上传将更新此 Avatar。");
            })));
            blueprintActions.Add(SplitUi.Button("清空并新建", () => Run(() =>
            { CauBridge.AssignBlueprint(plan, g, ""); DrawDetails(); SetNotice("蓝图号已清空，下次上传将创建新 Avatar。"); })));
            advanced.Add(blueprintActions);
            advanced.Add(SplitUi.Label(string.IsNullOrEmpty(g.blueprintId) ? "当前：新建私有 Avatar，上传后自动保存蓝图号。" :
                "当前：更新此蓝图对应的 Avatar。需要登录拥有该 Avatar 的账号。", 12, true));
            var version = new IntegerField("版本编号") { value = g.version, isDelayed = true };
            version.SetEnabled(source);
            version.RegisterValueChangedCallback(evt => Run(() =>
            {
                if (evt.newValue < 1 || plan.groups.Any(other => other != g && other.version == evt.newValue))
                { version.SetValueWithoutNotify(g.version); throw new InvalidOperationException("版本编号需大于 0，并与其他分组不同。"); }
                Undo.RecordObject(plan, "修改分组版本编号"); g.version = evt.newValue; SplitNaming.Apply(plan, source); DrawGroups(); DrawDetails(); UpdateActions();
            }));
            advanced.Add(version);
            if (!string.IsNullOrEmpty(g.generatedAvatarId)) advanced.Add(SplitUi.Button("运行详细检查…", () => RunLater("正在检查组件和动画…", () =>
            {
                EnsureScene(g); var avatar = SplitIds.Avatar(g); if (!avatar) throw new InvalidOperationException("生成副本已移除，请重新生成。");
                g.issues = AvatarSplitBuilder.Inspect(avatar, g.removedPaths); SplitIds.SaveLater(plan); DrawDetails();
                SetNotice(g.issues.Count == 0 ? "详细检查未发现问题。" : "发现 " + g.issues.Count + " 项参考提示，请结合 SDK 构建结果判断。");
            })));
            if (g.issues.Count > 0)
            {
                var reports = new Foldout { text = "参考提示 · " + g.issues.Count + " 项（不阻止上传）", value = false };
                foreach (var issue in g.issues.Take(8)) reports.Add(SplitUi.Label(issue, 12, true));
                advanced.Add(reports);
            }
            detailScroll.Add(advanced); SplitUi.Apply(detail); UpdateCount();
        }
        void DrawOutfits()
        {
            if (outfitList == null) return;
            outfitList.Clear(); var g = Current; if (g == null) return;
            if (!source) { outfitList.Add(SplitUi.Label("打开源场景后，可继续编辑衣服选择。", 13, true)); return; }
            int visible = 0;
            foreach (var choice in choices)
            {
                if (!string.IsNullOrEmpty(search) && choice.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                visible++;
                var row = SplitUi.Row(); row.style.minHeight = 36; row.style.paddingTop = row.style.paddingBottom = 3;
                var toggle = new Toggle { text = choice.Name, value = g.outfitIds.Contains(choice.Id), tooltip = choice.Label.tooltip, name = "split-outfit-" + choice.Id };
                toggle.style.flexGrow = 1; toggle.style.flexShrink = 1; toggle.style.flexBasis = 0; toggle.style.minWidth = 0;
                var text = toggle.Q<Label>(className: "unity-toggle__text");
                if (text != null) { text.style.whiteSpace = WhiteSpace.Normal; text.style.flexShrink = 1; text.style.minWidth = 0; }
                toggle.RegisterValueChangedCallback(evt => Run(() =>
                {
                    var ids = new HashSet<string>(g.outfitIds);
                    if (evt.newValue) ids.Add(choice.Id); else ids.Remove(choice.Id);
                    SetSelection(g, ids, false);
                }));
                row.Add(toggle);
                var locate = SplitUi.Button("定位", () => { Selection.activeGameObject = choice.Menu; EditorGUIUtility.PingObject(choice.Menu); });
                locate.style.width = 48; locate.style.minHeight = 28; row.Add(locate); outfitList.Add(row);
            }
            if (visible == 0) outfitList.Add(SplitUi.Label(choices.Count == 0 ? "暂未读到衣服，请检查源 Avatar 的衣服菜单并刷新。" : "没有匹配的衣服菜单。", 13, true));
            var missing = g.outfitIds.Where(id => !choicesById.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
            {
                outfitList.Add(SplitUi.Label(missing.Length + " 项旧选择已失效。刷新菜单，或移除失效项。", 12, true));
                outfitList.Add(SplitUi.Button("移除失效项", () => Run(() => SetSelection(g, g.outfitIds.Except(missing)))));
            }
            SplitUi.Apply(outfitList); UpdateCount();
        }
        void SetSelection(SplitGroup g, IEnumerable<string> selected, bool redraw = true)
        {
            Undo.RecordObject(plan, "修改分组衣服");
            var selectedSet = new HashSet<string>(selected);
            g.outfitIds = choices.Where(c => selectedSet.Contains(c.Id)).Select(c => c.Id).ToList();
            g.outfitNames = g.outfitIds.Select(id => choicesById[id].Name).ToList();
            g.Invalidate(); SplitNaming.Apply(plan, source); SplitIds.SaveLater(plan);
            if (redraw) DrawOutfits(); UpdateCount(); UpdateSummaries(); UpdateActions();
        }
        void AddGroup(SplitGroup copy)
        {
            if (!plan || !source) throw new InvalidOperationException("先选择源 Avatar。");
            Undo.RecordObject(plan, "添加衣服分组");
            var g = SplitNaming.Add(plan, source);
            if (copy != null) { g.outfitIds = new List<string>(copy.outfitIds); g.outfitNames = new List<string>(copy.outfitNames); SplitNaming.Apply(plan, source); }
            activeGroup = plan.groups.Count - 1; SplitIds.SaveLater(plan); DrawGroups(); DrawDetails();
        }
        void GroupMenu(SplitGroup g)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("复制此分组"), false, () => Run(() => AddGroup(g)));
            int index = plan.groups.IndexOf(g);
            void Move(int offset)
            {
                Undo.RecordObject(plan, "调整分组顺序"); plan.groups.RemoveAt(index); plan.groups.Insert(index + offset, g);
                activeGroup = index + offset; SplitIds.SaveLater(plan); DrawGroups(); DrawDetails();
            }
            if (index > 0) menu.AddItem(new GUIContent("上移"), false, () => Run(() => Move(-1))); else menu.AddDisabledItem(new GUIContent("上移"));
            if (index + 1 < plan.groups.Count) menu.AddItem(new GUIContent("下移"), false, () => Run(() => Move(1))); else menu.AddDisabledItem(new GUIContent("下移"));
            menu.AddSeparator(""); menu.AddItem(new GUIContent("移除分组"), false, () => Run(() =>
            {
                Undo.RecordObject(plan, "移除衣服分组"); plan.groups.Remove(g); activeGroup = Mathf.Clamp(activeGroup, 0, plan.groups.Count - 1);
                SplitIds.SaveLater(plan); DrawGroups(); DrawDetails(); SetNotice("已移除分组，可用 Ctrl+Z 恢复。线上 Avatar 保留。");
            }));
            menu.ShowAsContext();
        }
        void UpdateCount()
        {
            var g = Current;
            if (countLabel != null && g != null) countLabel.text = "保留 " + g.outfitIds.Count + " / " + choices.Count + " 套衣服" +
                (hasPlaceholder ? " · 占位衣服自动保留" : "");
        }
        void UpdateSummaries()
        {
            if (!plan) return;
            foreach (var g in plan.groups)
            {
                if (summaries.TryGetValue(g.id, out var label)) label.text = GroupStatus(g);
                if (groupNames.TryGetValue(g.id, out var title)) { title.text = g.name; title.tooltip = g.name; }
            }
            if (detailName != null && Current != null) { detailName.text = Current.name; detailName.tooltip = Current.name; }
        }
        void UpdateActions()
        {
            if (generateButton == null) return;
            bool idle = !localWork && !EditorApplication.isCompiling && !EditorApplication.isPlayingOrWillChangePlaymode && !CauBridge.Busy;
            bool selected = plan && source && plan.groups.Count > 0 && plan.groups.All(g => g.outfitIds.Count > 0);
            var uploading = CauBridge.SelectedUploads(plan);
            bool uploadReady = plan && source && uploading.Length > 0 && uploading.All(g => g.outfitIds.Count > 0 && g.generatedRevision == g.revision && !string.IsNullOrEmpty(g.generatedAvatarId));
            if (uploadSelectionLabel != null) uploadSelectionLabel.text = "上传 " + uploading.Length + "/" + (plan ? plan.groups.Count : 0);
            uploadButton.text = "打开 CAU 上传队列（" + uploading.Length + "）…";
            sourcePanel.SetEnabled(idle); workspace.SetEnabled(idle);
            generateButton.SetEnabled(idle && selected); uploadButton.SetEnabled(idle && uploadReady && CauBridge.Available && !CauExtraUploads.Busy);
            generateButton.style.opacity = generateButton.enabledSelf ? 1 : .45f;
            uploadButton.style.opacity = uploadButton.enabledSelf ? 1 : .45f;
            stageLabel.text = !SplitIds.Editing ? "游戏模式下暂停扫描与修改；退出后自动恢复源 Avatar" : !idle ? localWork ? "处理中…" : "等待 Unity 或当前上传任务完成" : !source ? "先选择一个源 Avatar" :
                uploading.Length == 0 && plan && source ? "尚未选择上传分组，请勾选左侧需要上传的版本" : uploadReady ? "已选择 " + uploading.Length + " 个版本，检查后可打开 CAU 队列" : !selected ? "为每个版本勾选至少一套衣服" : "选择已记录，生成后可预览与上传";
            saveLabel.text = plan && SplitIds.IsPending(plan) ? "正在记录配置…" : source && source.scene.isDirty ? "配置已记录 · 场景待保存" : "配置已保存";
        }
        void DrawUploadInfo(SplitGroup g)
        {
            var card = new Foldout { text = "封面与名称", value = showUploadInfo, name = "split-upload-info" };
            card.RegisterValueChangedCallback(evt => showUploadInfo = evt.newValue);
            SplitUi.Box(card, 10); card.style.marginBottom = 10;
            var row = SplitUi.Row(); row.style.flexWrap = Wrap.Wrap; row.style.minWidth = 0;
            coverThumb = new Image { scaleMode = ScaleMode.ScaleToFit, name = "split-cover-preview" };
            coverThumb.style.width = coverThumb.style.minWidth = coverThumb.style.maxWidth = 96;
            coverThumb.style.height = coverThumb.style.minHeight = coverThumb.style.maxHeight = 72;
            coverThumb.style.alignSelf = Align.FlexStart; coverThumb.style.marginRight = 10; coverThumb.style.marginTop = 6;
            coverThumb.style.flexShrink = 0; row.Add(coverThumb);
            var fields = new VisualElement(); fields.style.flexGrow = 1; fields.style.flexBasis = 180; fields.style.minWidth = 160;
            var uploadName = new TextField("VRC 模型名称") { value = g.uploadName ?? "", isDelayed = true, name = "split-upload-name" };
            uploadName.style.marginTop = 6; uploadName.tooltip = "留空使用自动名称：" + g.name + "。可直接更新线上名称，或在模型上传成功后同步到原蓝图。";
            uploadName.RegisterValueChangedCallback(evt => Run(() =>
            {
                Undo.RecordObject(plan, "修改 VRC 模型名称"); g.uploadName = evt.newValue.Trim(); uploadName.SetValueWithoutNotify(g.uploadName);
                SplitIds.SaveLater(plan); RefreshUploadInfo(); SetNotice("名称已保存，可直接更新线上信息：" + g.UploadName);
            })); fields.Add(uploadName);
            coverState = SplitUi.Label("", 12, true); coverState.style.marginTop = 5; fields.Add(coverState);
            row.Add(fields); card.Add(row);
            queuedState = SplitUi.Label("", 12, true); queuedState.style.marginTop = 5; card.Add(queuedState);
            extraState = SplitUi.Label("", 12, true); extraState.name = "split-extra-status"; extraState.style.marginTop = 5; card.Add(extraState);
            var buttons = SplitUi.Row(); buttons.style.flexWrap = Wrap.Wrap; buttons.style.marginTop = 6;
            photoExtra = SplitUi.Button("拍摄封面…", () => SplitPhotoWindow.Open(source, g.id)); buttons.Add(photoExtra);
            directExtra = SplitUi.Button("仅更新名字/封面…", ShowDirectUpdateMenu, true); directExtra.name = "split-direct-update";
            directExtra.tooltip = "只更新已有蓝图的名字或封面，跳过模型构建。支持当前版本或左侧已勾选版本。"; buttons.Add(directExtra);
            retryExtra = SplitUi.Button("重试同步", () => Run(() => { var job = CurrentExtraJob(); if (job != null) CauExtraUploads.Retry(job); RefreshUploadInfo(); })); buttons.Add(retryExtra);
            skipExtra = SplitUi.Button("跳过此次同步", () => Run(() => { var job = CurrentExtraJob(); if (job != null) CauExtraUploads.Skip(job); RefreshUploadInfo(); })); buttons.Add(skipExtra);
            card.Add(buttons); detail.Add(card); RefreshUploadInfo();
        }
        CauExtraJob CurrentExtraJob() => CauExtraUploads.LatestJob(plan, Current);
        void ShowDirectUpdateMenu()
        {
            var menu = new GenericMenu();
            var current = Current == null ? Array.Empty<SplitGroup>() : new[] { Current };
            var selected = CauBridge.SelectedUploads(plan);
            void Add(string label, SplitGroup[] groups, bool name, bool cover)
            {
                bool ready = groups.Length > 0 && groups.All(g => CauExtraUploads.HasBlueprint(g) && (!cover || (SplitPhoto.CoverTexture(g) && g.photoCoverSignature == SplitPhoto.Signature(plan, g))));
                if (!ready) { menu.AddDisabledItem(new GUIContent(label)); return; }
                menu.AddItem(new GUIContent(label), false, () => Run(() =>
                {
                    SplitIds.Flush(); int count = CauExtraUploads.QueueDirect(plan, groups, name, cover);
                    if (count > 0) SetNotice("已提交 " + count + " 个版本的线上信息更新，不构建或上传模型。");
                    RefreshUploadInfo();
                }));
            }
            Add("当前版本：名称和封面", current, true, true);
            Add("当前版本：仅名称", current, true, false);
            Add("当前版本：仅封面", current, false, true);
            menu.AddSeparator("");
            string prefix = "已勾选 " + selected.Length + " 个版本：";
            Add(prefix + "名称和封面", selected, true, true);
            Add(prefix + "仅名称", selected, true, false);
            Add(prefix + "仅封面", selected, false, true);
            menu.ShowAsContext();
        }
        void RefreshUploadInfo()
        {
            var g = Current; if (g == null || coverThumb == null) return;
            var texture = SplitPhoto.CoverTexture(g); coverThumb.image = texture;
            coverThumb.style.display = texture ? DisplayStyle.Flex : DisplayStyle.None;
            bool valid = texture && g.photoCoverSignature == SplitPhoto.Signature(plan, g);
            coverState.text = valid ? "已发送给上传" : !string.IsNullOrEmpty(g.photoCoverGuid) ? "封面已失效，请重新拍摄并发送" : string.IsNullOrEmpty(g.blueprintId) ? "尚未发送封面" : "未绑定封面，保留线上原图";
            var profile = CauExtraUploads.State.profiles.LastOrDefault(x => x.settingGuid == g.cauSettingGuid && x.groupId == g.id && x.sourceKey == SplitConfigStore.Key(plan));
            bool changed = profile != null && (profile.desiredName != g.UploadName || (profile.imageHash ?? "") != (string.IsNullOrEmpty(g.photoCoverGuid) ? "" : g.photoCoverHash) || (!string.IsNullOrEmpty(g.photoCoverGuid) && !valid));
            queuedState.text = profile == null ? "可直接更新已有蓝图，也可随 CAU 上传后同步。" : changed ? "名称或封面已修改，可直接更新，或重新交给 CAU。" : "交接名称：" + profile.desiredName;
            var job = CurrentExtraJob(); extraState.text = job != null ? CauExtraUploads.Status(job) : profile != null ? "已交接 · 等待你在 CAU 开始上传" : "";
            extraState.style.display = string.IsNullOrEmpty(extraState.text) ? DisplayStyle.None : DisplayStyle.Flex;
            bool idle = SplitIds.Editing && !localWork && !CauBridge.Busy && !CauExtraUploads.Busy;
            retryExtra.style.display = job != null && job.status == CauExtraStatus.Failed && (job.modelSucceeded || job.metadataOnly) ? DisplayStyle.Flex : DisplayStyle.None; retryExtra.SetEnabled(idle);
            skipExtra.style.display = job != null && (job.status == CauExtraStatus.Pending || job.status == CauExtraStatus.Failed) ? DisplayStyle.Flex : DisplayStyle.None; skipExtra.SetEnabled(idle);
            photoExtra.SetEnabled(idle && source);
            directExtra.SetEnabled(idle && source && plan && plan.groups.Any(CauExtraUploads.HasBlueprint));
        }
        void Tick()
        {
            if (!SplitIds.Editing) { UpdateActions(); return; }
            if (source && source.name != sourceName)
            { sourceName = source.name; SplitNaming.Apply(plan, source); BuildUI(); }
            if (lastAppearance != EditorGUIUtility.isProSkin.ToString()) BuildUI();
            UpdateActions(); UpdateSummaries(); RefreshUploadInfo();
        }
        void UndoChanged() { if (!SplitIds.Editing) return; if (source && plan) { SplitNaming.Apply(plan, source); SplitIds.SaveLater(plan); } DrawGroups(); DrawDetails(); UpdateActions(); }
        void UpdateResponsive(float width)
        {
            if (workspace == null) return;
            bool narrow = width < 760;
            if (responsiveReady && compact == narrow) return;
            compact = narrow; responsiveReady = true;
            workspace.style.flexDirection = narrow ? FlexDirection.Column : FlexDirection.Row;
            sidebar.style.width = narrow ? new StyleLength(Length.Percent(100)) : new StyleLength(210);
            sidebar.style.height = narrow ? new StyleLength(130) : new StyleLength(StyleKeyword.Auto);
            sidebar.style.flexShrink = 0; sidebar.style.marginRight = narrow ? 0 : 10; sidebar.style.marginBottom = narrow ? 8 : 0;
        }
        static void EnsureScene(SplitGroup g)
        {
            if (!UnityEngine.SceneManagement.SceneManager.GetSceneByPath(g.generatedScene).isLoaded)
                EditorSceneManager.OpenScene(g.generatedScene, OpenSceneMode.Additive);
        }
        static void Preview(SplitGroup g)
        {
            EnsureScene(g); AvatarSplitBuilder.DisableGeneratedPreviews(); var avatar = SplitIds.Avatar(g);
            if (!avatar) throw new InvalidOperationException("副本已移除，请重新生成。");
            avatar.SetActive(true); Selection.activeGameObject = avatar; EditorGUIUtility.PingObject(avatar);
            if (SceneView.lastActiveSceneView) SceneView.lastActiveSceneView.FrameSelected();
        }
    }
}
#endif
