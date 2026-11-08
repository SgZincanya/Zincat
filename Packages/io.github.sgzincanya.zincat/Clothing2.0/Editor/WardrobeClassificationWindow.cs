#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Zincat.Clothing.Editor
{
    /// <summary>
    /// 用户把衣服和分件从 Hierarchy 拖入卡片；完成时只为非空卡片生成 MA。
    /// 开关打开时隐藏分件，关闭时恢复分件原有状态。不扫描动画、不改衣服层级。
    /// </summary>
    public sealed class WardrobeClassificationWindow : EditorWindow
    {
        private static readonly string[] MainNames =
        {
            "外套", "内衬", "内衣", "裤_裙", "胖次", "袜子", "鞋子", "袖子_手套", "帽子", "其他1", "其他2", "其他3"
        };

        private const string GeneratedPrefix = "MA衣柜_";
        private const int MainCount = 12;
        private const int TotalCount = 21;
        private const int CardSize = 200;
        private static Color WindowColor => EditorGUIUtility.isProSkin ? new Color32(52, 52, 52, 255) : new Color(.95f, .95f, .96f);
        private static Color CardColor => EditorGUIUtility.isProSkin ? new Color32(58, 58, 58, 255) : Color.white;
        private static Color SelectedColor => EditorGUIUtility.isProSkin ? new Color(.17f, .23f, .31f) : new Color(.91f, .95f, 1f);
        private static Color AccentColor => new Color(.10f, .48f, .96f);
        private static Color MutedColor => WardrobeUiStyle.Muted;
        private static Color BorderColor => EditorGUIUtility.isProSkin ? new Color(.27f, .28f, .30f) : new Color(.84f, .85f, .87f);

        [SerializeField] private GameObject clothingRoot;
        [SerializeField] private string clothingMenuName;
        [SerializeField] private List<PartSlot> slots = new List<PartSlot>();
        [SerializeField] private bool showBackups;
        [SerializeField] private bool showUnassignedMeshes;
        [SerializeField] private bool editMode;
        [SerializeField] private GameObject loadedMenuRoot;
        [SerializeField] private bool reserveOutfitSwitch = true;
        [SerializeField] private bool enableBatchControl = true;
        [SerializeField] private List<WardrobePartPreset> partPresets = new List<WardrobePartPreset>();
        [SerializeField] private bool showPartPresets;
        [SerializeField] private string loadedConfigurationSignature;
        [SerializeField] private string loadedNamingSignature;
        [SerializeField] private bool reloadSavedMenuOnOpen;
        [SerializeField] private bool showMenuPreview;
        [SerializeField] private List<ClothingDraft> drafts = new List<ClothingDraft>();
        [SerializeField] private ConfigurationState loadedSceneSnapshot;
        [SerializeField] private string loadedSceneSignature;
        private IVisualElementScheduledItem hierarchyRefresh;
        private string meshHierarchySignature;
        private int hierarchyEvents, hierarchyPasses, fullRefreshes;
        private string lastSyncAttemptScene, lastSyncAttemptDraft;
        private int synchronizationAttempts;
        private HelpBox feedback;
        [NonSerialized] private int selectedIndex = -1;
        [NonSerialized] private HashSet<GameObject> selectedTargets = new HashSet<GameObject>();
        [NonSerialized] private bool lastAddBlockedTransfer;
        [NonSerialized] private HashSet<int> previewedSlots = new HashSet<int>();
        [NonSerialized] private Dictionary<GameObject, bool> previewObjectStates = new Dictionary<GameObject, bool>();
        [NonSerialized] private Dictionary<SkinnedMeshRenderer, Dictionary<int, float>> previewShapeStates = new Dictionary<SkinnedMeshRenderer, Dictionary<int, float>>();
        [NonSerialized] private Dictionary<Renderer, Material[]> previewMaterialStates = new Dictionary<Renderer, Material[]>();

        private VisualElement mainGrid;
        private VisualElement backupGrid;
        private VisualElement details;
        [SerializeField] private List<OutfitIconDraft> outfitIconDrafts = new List<OutfitIconDraft>();
        private ObjectField outfitIconField;
        private Button outfitIconButton;
        [NonSerialized] private bool iconCaptureBusy;
        private VisualElement iconSettings;
        [NonSerialized] private bool partIconsExpanded;
        private Label statusLabel;
        private Label meshStatusLabel;
        private Foldout unassignedMeshesFoldout;
        private ScrollView unassignedMeshesScroll;
        private VisualElement unassignedMeshesContent;
        private VisualElement rootDropZone;
        private Label rootNameLabel;
        private TextField clothingMenuNameField;
        private Button generateButton;
        private Button openAllButton;
        private Button closeAllButton;
        private VisualElement batchSettings;

        [MenuItem("Zincat/衣服菜单/分件制作")]
        private static void Open()
        {
            var window = GetWindow<WardrobeClassificationWindow>("衣服分件");
            window.minSize = new Vector2(760, 620);
            window.Show();
        }

        private void OnEnable()
        {
            if (selectedTargets == null) selectedTargets = new HashSet<GameObject>();
            if (previewedSlots == null) previewedSlots = new HashSet<int>();
            if (previewObjectStates == null) previewObjectStates = new Dictionary<GameObject, bool>();
            if (previewShapeStates == null) previewShapeStates = new Dictionary<SkinnedMeshRenderer, Dictionary<int, float>>();
            if (previewMaterialStates == null) previewMaterialStates = new Dictionary<Renderer, Material[]>();
            if (drafts == null) drafts = new List<ClothingDraft>();
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EnsureSlots();
        }

        private void OnDisable()
        {
            hierarchyRefresh?.Pause();
            hierarchyRefresh = null;
            ClearPreview();
            reloadSavedMenuOnOpen = loadedMenuRoot != null && !HasUnsavedConfiguration();
            SaveDraft();
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            ObjectChangeEvents.changesPublished -= OnObjectChanges;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnHierarchyChanged()
        {
            hierarchyEvents++;
            WardrobeDiagnostics.Record("分件", "层级变化", "count=" + hierarchyEvents);
            QueueSceneCheck();
        }

        private void OnFocus() { QueueSceneCheck(); }
        private void OnLostFocus()
        {
            // UI Toolkit 可能保留上次 TextField 的内部焦点；切到 Inspector 时结束该输入焦点。
            rootVisualElement.focusController?.focusedElement?.Blur();
            QueueSceneCheck();
        }

        private void OnObjectChanges(ref ObjectChangeEventStream stream)
        {
            if (loadedMenuRoot == null) return;
            for (int i = 0; i < stream.length; i++)
            {
                if (stream.GetEventType(i) != ObjectChangeKind.ChangeGameObjectOrComponentProperties) continue;
                stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var data);
                var changed = EditorUtility.InstanceIDToObject(data.instanceId);
                var node = changed as GameObject ?? (changed as Component)?.gameObject;
                if (node == null || (node != loadedMenuRoot && !node.transform.IsChildOf(loadedMenuRoot.transform))) continue;
                WardrobeDiagnostics.Record("分件", "菜单组件变化", changed.GetType().Name);
                QueueSceneCheck();
                break;
            }
        }

        private void QueueSceneCheck()
        {
            if (hierarchyRefresh != null || rootVisualElement == null) return;
            hierarchyRefresh = rootVisualElement.schedule.Execute(ProcessHierarchyRefresh).StartingIn(250).Every(250);
        }

        private void ProcessHierarchyRefresh()
        {
            // TextField 的焦点实际位于内部 TextInput；包含祖先检查以保护中文输入法组合文字。
            var focused = rootVisualElement.focusController?.focusedElement as VisualElement;
            if (focused != null &&
                (focused is TextField || focused.GetFirstAncestorOfType<TextField>() != null)) return;
            hierarchyRefresh?.Pause();
            hierarchyRefresh = null;
            hierarchyPasses++;
            bool changed = false;
            if (loadedMenuRoot != null && previewedSlots.Count == 0)
            {
                string scene = SceneMenuSignature();
                if (scene != loadedSceneSignature &&
                    (scene != lastSyncAttemptScene || PreviewSignature() != lastSyncAttemptDraft))
                    changed = SynchronizeSceneConfiguration();
            }
            string meshes = MeshHierarchySignature();
            if (changed)
            {
                clothingMenuNameField?.SetValueWithoutNotify(clothingMenuName);
                RefreshAll();
            }
            else if (meshes != meshHierarchySignature)
            {
                meshHierarchySignature = meshes;
                rootNameLabel.text = clothingRoot == null ? "未选择衣服" : clothingRoot.name;
                RefreshMeshProgress();
            }
            WardrobeDiagnostics.Record("分件", "层级检查", "pass=" + hierarchyPasses + ", rebuilt=" + changed);
        }

        public void CreateGUI()
        {
            EnsureSlots();
            if (clothingRoot != null && (reloadSavedMenuOnOpen || loadedMenuRoot == null && !slots.Any(IsUsed)))
                TryRestoreMenu(clothingRoot, false);
            reloadSavedMenuOnOpen = false;
            BuildUI();
        }

        private void BuildUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1;
            root.style.backgroundColor = WindowColor;
            root.style.paddingLeft = 18;
            root.style.paddingRight = 18;
            root.style.paddingTop = 14;
            root.style.paddingBottom = 14;
            // 使用编辑器主题提供的 TextCore 字体；不能把动态系统 Font 强制注入所有控件。
            root.style.unityFontStyleAndWeight = FontStyle.Normal;

            var title = new Label("衣服分件");
            title.style.fontSize = 20;
            title.style.unityFontStyleAndWeight = FontStyle.Normal;
            title.style.marginBottom = 5;
            root.Add(title);
            WardrobeDiagnostics.Attach(root, "分件", DiagnosticState);

            var intro = new Label("拖入一套衣服，再把分件放进对应卡片。生成的分件开关用于隐藏，可一次添加多个物体。");
            intro.style.whiteSpace = WhiteSpace.Normal;
            intro.style.color = MutedColor;
            intro.style.marginBottom = 12;
            root.Add(intro);

            rootDropZone = new VisualElement();
            rootDropZone.style.minHeight = 146;
            rootDropZone.style.paddingLeft = 16;
            rootDropZone.style.paddingRight = 16;
            rootDropZone.style.paddingTop = 14;
            rootDropZone.style.paddingBottom = 12;
            rootDropZone.style.marginBottom = 11;
            rootDropZone.style.backgroundColor = CardColor;
            SetRoundedBorder(rootDropZone, BorderColor);
            var rootCaption = new Label("拖入整套衣服");
            rootCaption.style.fontSize = 13;
            rootCaption.style.color = MutedColor;
            rootDropZone.Add(rootCaption);
            rootNameLabel = new Label();
            rootNameLabel.style.fontSize = 18;
            rootNameLabel.style.unityFontStyleAndWeight = FontStyle.Normal;
            rootNameLabel.style.marginTop = 7;
            rootDropZone.Add(rootNameLabel);
            var menuNameCaption = new Label("菜单名称（选填）");
            menuNameCaption.tooltip = "留空使用 -衣服名。修改生成菜单的层级名称，轮盘里的名称也会同步改变。";
            menuNameCaption.style.marginTop = 10;
            menuNameCaption.style.color = MutedColor;
            rootDropZone.Add(menuNameCaption);
            clothingMenuNameField = new TextField { value = clothingMenuName ?? "" };
            clothingMenuNameField.style.maxWidth = 420;
            clothingMenuNameField.style.marginTop = 3;
            clothingMenuNameField.RegisterValueChangedCallback(evt => { Undo.RecordObject(this, "修改菜单名称"); clothingMenuName = evt.newValue; });
            rootDropZone.Add(clothingMenuNameField);
            var reserveToggle = new Toggle("添加换衣按钮") { value = reserveOutfitSwitch };
            reserveToggle.tooltip = "为衣服切换工具保留一个按钮。未配置换衣前，它不会控制衣服；只做分件菜单时可以关闭。";
            reserveToggle.style.marginTop = 7;
            reserveToggle.RegisterValueChangedCallback(evt =>
            {
                reserveOutfitSwitch = evt.newValue;
                RefreshAll();
            });
            rootDropZone.Add(reserveToggle);
            var outfitIcons = new VisualElement { name = "outfit-icon-row" };
            outfitIcons.style.flexDirection = FlexDirection.Row; outfitIcons.style.marginTop = 7;
            outfitIconField = new ObjectField("衣服图标") { objectType = typeof(Texture2D), allowSceneObjects = false, name = "outfit-icon-field" };
            outfitIconField.style.flexGrow = 1;
            outfitIconField.RegisterValueChangedCallback(evt => SetOutfitIcon(evt.newValue as Texture2D));
            outfitIcons.Add(outfitIconField);
            outfitIconButton = new Button(CaptureOutfitIcon) { text = "生成／更新", name = "outfit-icon-capture" };
            outfitIconButton.style.height = 28; outfitIconButton.style.marginLeft = 8;
            outfitIconButton.tooltip = "拍摄整套衣服，使用标准视角和透明兼容。更新自动图标前备份上一版，手动图标请先清除引用。";
            outfitIcons.Add(outfitIconButton); rootDropZone.Add(outfitIcons);
            rootDropZone.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                DragAndDrop.visualMode = DragAndDrop.objectReferences.OfType<GameObject>().Any()
                    ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                evt.StopPropagation();
            });
            rootDropZone.RegisterCallback<DragPerformEvent>(evt =>
            {
                var candidate = DragAndDrop.objectReferences.OfType<GameObject>().FirstOrDefault();
                if (candidate != null)
                {
                    DragAndDrop.AcceptDrag();
                    SetClothingRoot(candidate);
                }
                evt.StopPropagation();
            });
            root.Add(rootDropZone);

            var selectionButton = new Button(() =>
            {
                if (Selection.activeGameObject == null)
                    Notify("没有选中衣服", "请先在 层级面板中选中这套衣服的根物体。", "确定");
                else
                    SetClothingRoot(Selection.activeGameObject);
            }) { text = "使用选中衣服" };
            selectionButton.style.height = 28;
            selectionButton.style.marginBottom = 10;
            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            selectionButton.style.flexGrow = 1;
            selectionButton.style.flexBasis = 0;
            actions.Add(selectionButton);

            var reloadButton = new Button(() =>
            {
                if (clothingRoot == null) return;
                if (HasUnsavedConfiguration() && !EditorUtility.DisplayDialog("重新读取菜单",
                        "这会替换窗口中尚未保存的分类设置。继续吗？", "重新读取", "取消")) return;
                ClearPreview();
                if (TryRestoreMenu(clothingRoot, true)) BuildUI();
            }) { text = "重新读取菜单" };
            reloadButton.style.height = 28;
            reloadButton.style.marginBottom = 10;
            reloadButton.style.flexGrow = 1;
            reloadButton.style.flexBasis = 0;
            reloadButton.style.marginLeft = 8;
            actions.Add(reloadButton);
            root.Add(actions);
            feedback = new HelpBox("", HelpBoxMessageType.Info);
            feedback.style.display = DisplayStyle.None;
            feedback.style.marginBottom = 10;
            root.Add(feedback);

            statusLabel = new Label();
            statusLabel.style.fontSize = 12;
            statusLabel.style.color = MutedColor;
            statusLabel.style.marginBottom = 10;
            root.Add(statusLabel);

            meshStatusLabel = new Label();
            meshStatusLabel.style.fontSize = 13;
            meshStatusLabel.style.marginBottom = 5;
            root.Add(meshStatusLabel);
            unassignedMeshesFoldout = new Foldout
            {
                text = "查看未分配的模型",
                value = showUnassignedMeshes
            };
            unassignedMeshesFoldout.style.marginBottom = 10;
            unassignedMeshesFoldout.RegisterValueChangedCallback(evt => showUnassignedMeshes = evt.newValue);
            unassignedMeshesScroll = new ScrollView(ScrollViewMode.Vertical) { name = "unassigned-meshes-scroll" };
            unassignedMeshesScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            unassignedMeshesScroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            unassignedMeshesScroll.style.flexShrink = 0;
            unassignedMeshesScroll.style.maxHeight = 156;
            unassignedMeshesContent = unassignedMeshesScroll.contentContainer;
            unassignedMeshesContent.style.width = Length.Percent(100);
            unassignedMeshesFoldout.Add(unassignedMeshesScroll);
            root.Add(unassignedMeshesFoldout);

            var editToggle = new Toggle("编辑分类") { value = editMode };
            editToggle.tooltip = "开启后可跨卡片转移物体，或选中物体后移除分类。不会删除场景里的物体。";
            editToggle.style.marginBottom = 10;
            editToggle.RegisterValueChangedCallback(evt =>
            {
                editMode = evt.newValue;
                selectedTargets.Clear();
                RefreshAll();
            });
            root.Add(editToggle);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.marginBottom = 12;
            root.Add(scroll);

            iconSettings = new VisualElement { name = "part-icon-settings-area" };
            iconSettings.style.marginBottom = 10;
            scroll.Add(iconSettings);

            var headingRow = new VisualElement();
            headingRow.style.flexDirection = FlexDirection.Row;
            headingRow.style.alignItems = Align.Center;
            var mainHeading = new Label("分件分类");
            StyleHeading(mainHeading);
            mainHeading.style.flexGrow = 1;
            headingRow.Add(mainHeading);
            var missingIcons = new Button(GenerateMissingIcons) { text = "补齐图标", name = "part-icons-missing" };
            missingIcons.tooltip = "只为已有网格、没有图标的分类生成截图，不覆盖现有图片。";
            missingIcons.style.marginRight = 6;
            headingRow.Add(missingIcons);
            var updateIcons = new Button(UpdateGeneratedIcons) { text = "更新图标", name = "part-icons-update" };
            updateIcons.tooltip = "原位覆盖自动图标，先备份上一版；保留手动图片。已有菜单沿用原引用，无需重新绑定。";
            headingRow.Add(updateIcons);
            closeAllButton = new Button(CloseAllPreview) { text = "全部隐藏" };
            closeAllButton.tooltip = "一次隐藏所有已填写的分件";
            closeAllButton.style.height = 25;
            closeAllButton.style.marginRight = 6;
            closeAllButton.style.marginBottom = 9;
            closeAllButton.style.borderTopLeftRadius = 6;
            closeAllButton.style.borderTopRightRadius = 6;
            closeAllButton.style.borderBottomLeftRadius = 6;
            closeAllButton.style.borderBottomRightRadius = 6;
            headingRow.Add(closeAllButton);
            openAllButton = new Button(OpenAllPreview) { text = "全部显示" };
            openAllButton.tooltip = "结束隐藏预览，恢复所有分件的原有显示状态";
            openAllButton.style.height = 25;
            openAllButton.style.marginBottom = 9;
            openAllButton.style.borderTopLeftRadius = 6;
            openAllButton.style.borderTopRightRadius = 6;
            openAllButton.style.borderBottomLeftRadius = 6;
            openAllButton.style.borderBottomRightRadius = 6;
            headingRow.Add(openAllButton);
            headingRow.style.marginBottom = 9;
            mainHeading.style.marginBottom = 0;
            foreach (var button in headingRow.Children().OfType<Button>())
            {
                button.style.height = 28; button.style.minHeight = 28;
                button.style.marginTop = button.style.marginBottom = button.style.marginLeft = 0;
                button.style.marginRight = 6;
            }
            openAllButton.style.marginRight = 0;
            scroll.Add(headingRow);
            var previewHint = new Label("场景预览：勾选右上角方框＝隐藏分件；取消勾选＝恢复原有显示状态。")
                { name = "part-preview-hint" };
            previewHint.style.color = MutedColor;
            previewHint.style.fontSize = 12;
            previewHint.style.whiteSpace = WhiteSpace.Normal;
            previewHint.style.marginBottom = 8;
            scroll.Add(previewHint);
            mainGrid = CreateGrid();
            scroll.Add(mainGrid);

            var backupFoldout = new Foldout { text = "备用分类 · 9 项", value = showBackups };
            backupFoldout.style.marginTop = 16;
            backupFoldout.style.marginBottom = 10;
            backupFoldout.RegisterValueChangedCallback(evt => showBackups = evt.newValue);
            backupGrid = CreateGrid();
            backupFoldout.Add(backupGrid);
            scroll.Add(backupFoldout);

            batchSettings = new VisualElement();
            scroll.Add(batchSettings);
            RefreshBatchSettings();

            details = new VisualElement();
            details.style.marginTop = 17;
            details.style.paddingTop = 12;
            details.style.paddingBottom = 8;
            details.style.borderTopWidth = 1;
            details.style.borderTopColor = BorderColor;
            scroll.Add(details);

            generateButton = new Button(Generate);
            generateButton.name = "wardrobe-primary-action";
            generateButton.style.height = 42;
            generateButton.style.fontSize = 14;
            generateButton.style.unityFontStyleAndWeight = FontStyle.Normal;
            generateButton.style.backgroundColor = AccentColor;
            generateButton.style.color = Color.white;
            SetRoundedBorder(generateButton, AccentColor);
            root.Add(generateButton);

            RefreshAll();
            ApplyTextStyle();
        }

        private void ApplyTextStyle()
        {
            WardrobeUiStyle.Apply(rootVisualElement);
        }

        private static VisualElement CreateGrid()
        {
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            return grid;
        }

        private static List<WardrobePartPreset> ClonePresets(List<WardrobePartPreset> source)
        {
            return (source ?? new List<WardrobePartPreset>()).Where(p => p != null).Select(p =>
                new WardrobePartPreset { Name = p.Name,
                    VisibleKeys = new List<string>(p.VisibleKeys ?? new List<string>()) }).ToList();
        }

        private void RefreshBatchSettings()
        {
            if (batchSettings == null) return;
            if (partPresets == null) partPresets = new List<WardrobePartPreset>();
            batchSettings.Clear();
            batchSettings.style.marginTop = 16;
            var heading = new VisualElement();
            heading.style.flexDirection = FlexDirection.Row;
            heading.style.alignItems = Align.Center;
            var title = new Label("批量控制");
            StyleHeading(title);
            title.style.marginBottom = 0;
            title.style.flexGrow = 1;
            heading.Add(title);
            var toggle = new Toggle("加入游戏菜单") { value = enableBatchControl };
            toggle.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "设置批量控制");
                enableBatchControl = evt.newValue;
                RefreshBatchSettings();
            });
            heading.Add(toggle);
            batchSettings.Add(heading);
            if (!enableBatchControl) { RefreshMenuPreview(); return; }
            var fixedActions = CreateGrid();
            fixedActions.name = "batch-default-actions";
            fixedActions.style.marginTop = 10;
            foreach (var action in new[] { ("全部显示", "关闭所有分件隐藏开关"), ("全部隐藏", "打开所有分件隐藏开关") })
            {
                var tile = BatchPanel();
                tile.tooltip = "固定生成的游戏菜单操作。窗口中的场景预览请使用上方的全部显示 / 全部隐藏按钮。";
                tile.style.flexGrow = 1;
                tile.style.flexBasis = 220;
                tile.style.marginRight = 6;
                var line = new VisualElement(); line.style.flexDirection = FlexDirection.Row;
                var label = new Label(action.Item1); label.style.flexGrow = 1; line.Add(label);
                var badge = new Label("默认添加"); badge.style.color = MutedColor; badge.style.fontSize = 10; line.Add(badge);
                tile.Add(line);
                var detail = new Label(action.Item2); detail.style.color = MutedColor; detail.style.fontSize = 11; detail.style.marginTop = 5;
                tile.Add(detail); fixedActions.Add(tile);
            }
            batchSettings.Add(fixedActions);
            var foldout = new Foldout { text = "自定义模板 · " + partPresets.Count, value = showPartPresets };
            foldout.style.marginTop = 10;
            foldout.RegisterValueChangedCallback(evt => showPartPresets = evt.newValue);
            batchSettings.Add(foldout);
            var hint = new Label("模板设置：选中＝显示分件，未选中＝隐藏分件。仅编辑模板，不影响场景预览；更新菜单后生效。\n名称来自分件菜单，空分类不参与。")
                { name = "batch-template-hint" };
            hint.style.color = MutedColor; hint.style.fontSize = 11; hint.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(hint);
            foreach (var preset in partPresets)
            {
                var box = BatchPanel();
                var name = new TextField { value = preset.Name, name = "batch-preset-name" };
                name.style.minWidth = 0;
                name.style.height = 28;
                name.style.minHeight = 28;
                name.style.overflow = Overflow.Hidden;
                name.tooltip = "此名称会显示在游戏中的批量控制菜单里。";
                name.RegisterValueChangedCallback(evt => { Undo.RecordObject(this, "修改模板名称"); preset.Name = evt.newValue; });
                box.Add(name);
                var remove = new Button(() => { Undo.RecordObject(this, "删除模板"); partPresets.Remove(preset); RefreshBatchSettings(); })
                    { text = "删除", name = "batch-delete-preset", tooltip = "删除此模板；更新菜单后应用到生成结果。" };
                remove.style.width = 60; remove.style.minWidth = 60; remove.style.maxWidth = 60;
                remove.style.flexShrink = 0;
                remove.style.height = 26; remove.style.marginTop = 8;
                remove.style.alignSelf = Align.FlexEnd;
                remove.style.backgroundColor = Color.clear;
                remove.style.color = MutedColor;
                remove.style.fontSize = 11;
                remove.style.borderTopWidth = remove.style.borderBottomWidth = 0;
                remove.style.borderLeftWidth = remove.style.borderRightWidth = 0;
                var summary = new Label("显示 " + slots.Count(s => IsUsed(s) && preset.VisibleKeys.Contains(s.Key)) + " / " + slots.Count(IsUsed) + " 个分件菜单");
                summary.style.color = MutedColor; summary.style.fontSize = 11; summary.style.marginTop = 6;
                box.Add(summary);
                var choices = CreateGrid();
                choices.style.marginTop = 8;
                foreach (var slot in slots.Where(IsUsed))
                {
                    bool visible = preset.VisibleKeys.Contains(slot.Key);
                    var choice = new Button(() =>
                    {
                        Undo.RecordObject(this, "设置模板分件");
                        preset.VisibleKeys.RemoveAll(key => key == slot.Key);
                        if (!visible) preset.VisibleKeys.Add(slot.Key);
                        RefreshBatchSettings();
                    });
                    choice.name = "batch-choice-" + slot.Key;
                    choice.AddToClassList("wardrobe-state-button");
                    choice.style.width = 210; choice.style.minWidth = 180; choice.style.height = 82;
                    choice.style.alignItems = Align.Stretch; choice.style.justifyContent = Justify.Center;
                    choice.style.marginRight = 6; choice.style.marginBottom = 6;
                    choice.style.paddingLeft = choice.style.paddingRight = 10;
                    choice.style.backgroundColor = visible ? SelectedColor : WindowColor;
                    SetRoundedBorder(choice, visible ? AccentColor : BorderColor);
                    var first = new VisualElement(); first.style.flexDirection = FlexDirection.Row;
                    var menuLabel = new Label(slot.MenuName) { name = "batch-choice-menu-name" };
                    menuLabel.style.flexGrow = 1; menuLabel.style.flexShrink = 1;
                    menuLabel.style.overflow = Overflow.Hidden; menuLabel.style.textOverflow = TextOverflow.Ellipsis;
                    first.Add(menuLabel);
                    var state = new Label(visible ? "显示" : "隐藏");
                    state.style.color = visible ? WardrobeUiStyle.Link : MutedColor; state.style.marginLeft = 8;
                    state.style.fontSize = 11; first.Add(state); choice.Add(first);
                    var category = new Label(PartDisplayName(slot.Category) + (slot.Backup ? " · 备用" : ""));
                    category.style.color = MutedColor; category.style.fontSize = 11; category.style.marginTop = 5;
                    choice.Add(category);
                    var targetNames = string.Join("、", slot.Targets.Where(t => t != null).Select(t => t.name));
                    if (string.IsNullOrEmpty(targetNames)) targetNames = "形态键 / 材质效果";
                    var targets = new Label(targetNames); targets.style.fontSize = 11; targets.style.color = MutedColor;
                    targets.style.overflow = Overflow.Hidden; targets.style.textOverflow = TextOverflow.Ellipsis;
                    choice.Add(targets);
                    choice.tooltip = "菜单：" + slot.MenuName + "\n分类：" + category.text + "\n对应：" + targetNames +
                        "\n显示 = 关闭此分件隐藏开关；隐藏 = 打开此开关。";
                    choices.Add(choice);
                }
                if (!slots.Any(IsUsed)) choices.Add(new Label("先将分件拖入上方分类卡片，再设置模板。"));
                box.Add(choices);
                // 删除属于这一个模板框，独立放在框内右下角，不与名称输入框争抢宽度。
                box.Add(remove);
                foldout.Add(box);
            }
            foldout.Add(new Button(() =>
            {
                Undo.RecordObject(this, "添加分件模板");
                int number = 1;
                while (partPresets.Any(p => p.Name == "模板 " + number)) number++;
                partPresets.Add(new WardrobePartPreset { Name = "模板 " + number,
                    VisibleKeys = slots.Where(IsUsed).Select(s => s.Key).ToList() });
                showPartPresets = true;
                RefreshBatchSettings();
            }) { text = "＋ 添加模板" });
            RefreshMenuPreview();
            WardrobeUiStyle.Apply(batchSettings);
        }

        private static VisualElement BatchPanel()
        {
            var box = new VisualElement();
            box.style.backgroundColor = CardColor;
            box.style.paddingLeft = box.style.paddingRight = 12;
            box.style.paddingTop = box.style.paddingBottom = 10;
            box.style.marginTop = 6;
            SetRoundedBorder(box, BorderColor);
            return box;
        }

        private void RefreshMenuPreview()
        {
            var foldout = new Foldout { text = "现有菜单 · 点击名称定位", value = showMenuPreview, name = "batch-menu-preview" };
            foldout.RegisterValueChangedCallback(evt => showMenuPreview = evt.newValue);
            foldout.style.marginTop = 8;
            batchSettings.Add(foldout);
            if (loadedMenuRoot == null)
            {
                foldout.Add(new Label("生成菜单后，可在这里查看实际菜单名称和层级。"));
                return;
            }
            var note = new Label(HasUnsavedConfiguration() ? "窗口有未保存修改，下方仍是场景中的现有菜单。" : "已读取场景菜单，修改层级名称后可自动同步。");
            note.style.color = MutedColor; note.style.fontSize = 11; note.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(note);
            AddMenuPreviewNode(foldout, loadedMenuRoot, 0);
        }

        private static void AddMenuPreviewNode(VisualElement parent, GameObject node, int depth)
        {
            var item = node.GetComponent<ModularAvatarMenuItem>();
            if (item == null) return;
            var name = string.IsNullOrEmpty(item.label) ? node.name : item.label;
            var button = new Button(() => { Selection.activeGameObject = node; EditorGUIUtility.PingObject(node); })
                { text = name, name = "existing-menu-row" };
            button.style.marginLeft = depth * 16; button.style.height = 28;
            button.style.marginTop = 3;
            button.style.backgroundColor = CardColor;
            button.style.unityTextAlign = TextAnchor.MiddleLeft;
            SetRoundedBorder(button, BorderColor);
            button.tooltip = "定位场景中的菜单项：" + node.name;
            parent.Add(button);
            if (depth < 3 && item.Control?.type == VRCExpressionsMenu.Control.ControlType.SubMenu &&
                item.MenuSource == SubmenuSource.Children)
                foreach (Transform child in node.transform) AddMenuPreviewNode(parent, child.gameObject, depth + 1);
        }

        private bool HasUnsavedConfiguration()
        {
            return loadedMenuRoot == null ? slots.Any(IsUsed) || partPresets.Count > 0 :
                string.IsNullOrEmpty(loadedConfigurationSignature) || PreviewSignature() != loadedConfigurationSignature;
        }

        private string MenuNamingSignature()
        {
            if (loadedMenuRoot == null) return "";
            return string.Join("|", loadedMenuRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true)
                .Select(item => item.GetInstanceID() + ":" + item.gameObject.name + ":" + item.label));
        }

        private string SceneMenuSignature()
        {
            if (loadedMenuRoot == null) return "";
            var text = new StringBuilder();
            // 只跟踪受管理菜单，额外菜单不参与配置合并，但在诊断报告里展示。
            // 签名还必须容忍损坏的关联，随后由读取器报告错误，不能在检查之前抛异常。
            var marker = loadedMenuRoot.GetComponent<WardrobeGeneratedMenu>();
            var owned = marker == null
                ? loadedMenuRoot.transform.Cast<Transform>().Select(t => t.gameObject)
                : (marker.Slots ?? new List<WardrobeSlotLink>()).Where(s => s != null).Select(s => s.MenuNode)
                    .Concat(new[] { marker.OutfitSwitchNode, marker.BatchControlNode });
            foreach (var node in new[] { loadedMenuRoot }.Concat(owned).Distinct())
            {
                if (node == null) continue;
                foreach (var child in node.GetComponentsInChildren<Transform>(true))
                {
                    // 根节点只记录自身；其受管理子节点由下一轮记录。
                    if (node == loadedMenuRoot && child != node.transform) continue;
                    text.Append(child.gameObject.GetInstanceID()).Append(':').Append(child.name)
                        .Append(':').Append(child.gameObject.activeSelf);
                    foreach (var component in child.GetComponents<Component>())
                        if (component != null && !(component is Transform))
                            text.Append(component.GetType().FullName).Append(EditorJsonUtility.ToJson(component));
                }
            }
            return text.ToString();
        }

        private string MeshHierarchySignature()
        {
            if (clothingRoot == null) return "";
            return clothingRoot.name + "|" + string.Join("|", clothingRoot.GetComponentsInChildren<Transform>(true)
                .Select(t => t.GetInstanceID() + ":" + t.name + ":" + t.parent?.GetInstanceID() + ":" + HasMesh(t.gameObject)));
        }

        private ConfigurationState CurrentConfiguration() => new ConfigurationState
        {
            Root = clothingRoot, MenuName = clothingMenuName, ReserveSwitch = reserveOutfitSwitch,
            Slots = slots, BatchControl = enableBatchControl, Presets = partPresets
        };

        private static ConfigurationState CopyConfiguration(ConfigurationState value) =>
            value == null ? null : JsonUtility.FromJson<ConfigurationState>(JsonUtility.ToJson(value));

        private static PartSlot CopyPart(PartSlot value) => JsonUtility.FromJson<PartSlot>(JsonUtility.ToJson(value));

        private void CaptureSceneBaseline()
        {
            loadedSceneSnapshot = CopyConfiguration(CurrentConfiguration());
            loadedSceneSignature = SceneMenuSignature();
        }

        private ConfigurationState ReadSceneConfiguration(List<PartSlot> restored)
        {
            var marker = loadedMenuRoot.GetComponent<WardrobeGeneratedMenu>();
            var presets = ClonePresets(marker?.PartPresets);
            if (marker?.BatchControlNode != null)
                for (int i = 0; i < presets.Count; i++)
                    presets[i].Name = ReadMenuName(marker.BatchControlNode.transform.GetChild(i + 2).gameObject);
            return new ConfigurationState { Root = clothingRoot, MenuName = ReadMenuName(loadedMenuRoot),
                ReserveSwitch = marker?.OutfitSwitchNode != null, Slots = restored,
                BatchControl = marker?.BatchControlNode != null || string.IsNullOrEmpty(marker?.PartParameterPrefix),
                Presets = presets };
        }

        // 三方比较：基线 / 窗口草稿 / 当前场景。不同字段可合并，同字段冲突不猜测。
        private bool SynchronizeSceneConfiguration()
        {
            if (loadedMenuRoot == null || SceneMenuSignature() == loadedSceneSignature) return false;
            lastSyncAttemptScene = SceneMenuSignature();
            lastSyncAttemptDraft = PreviewSignature();
            synchronizationAttempts++;
            var skipped = new Dictionary<string, string>();
            if (!TryReadMenuCore(loadedMenuRoot, clothingRoot, out var restored, out var reason, skipped))
            {
                Notify("场景配置未同步", reason + " 原组件已保留，请检查后重新读取。");
                WardrobeDiagnostics.Record("分件", "同步停止", reason);
                return false;
            }
            var scene = ReadSceneConfiguration(restored);
            if (loadedSceneSnapshot == null)
            {
                if (HasUnsavedConfiguration())
                {
                    Notify("请先重新读取", "旧窗口没有配置对照记录，无法判断外部改动；未覆盖场景或草稿。");
                    return false;
                }
                return TryRestoreMenu(clothingRoot, false);
            }
            var merged = CopyConfiguration(CurrentConfiguration());
            var baseline = loadedSceneSnapshot;
            var conflicts = new List<string>();
            void Merge(string oldValue, string draftValue, string sceneValue, Action accept, string label)
            {
                if (oldValue == sceneValue) return;
                if (draftValue == oldValue || draftValue == sceneValue) accept();
                else conflicts.Add(label);
            }
            Merge(baseline.MenuName, merged.MenuName, scene.MenuName, () => merged.MenuName = scene.MenuName, "衣服菜单名称");
            Merge(baseline.ReserveSwitch.ToString(), merged.ReserveSwitch.ToString(), scene.ReserveSwitch.ToString(),
                () => merged.ReserveSwitch = scene.ReserveSwitch, "换衣按钮");
            Merge(baseline.BatchControl.ToString(), merged.BatchControl.ToString(), scene.BatchControl.ToString(),
                () => merged.BatchControl = scene.BatchControl, "批量控制");
            string Presets(ConfigurationState data) => JsonUtility.ToJson(new ConfigurationState { Presets = data.Presets });
            Merge(Presets(baseline), Presets(merged), Presets(scene), () => merged.Presets = scene.Presets, "模板");
            for (int i = 0; i < merged.Slots.Count; i++)
            {
                var old = baseline.Slots[i]; var draft = merged.Slots[i]; var current = scene.Slots[i];
                draft.ExternalReadOnlyReason = current.ExternalReadOnlyReason;
                if (skipped.ContainsKey(draft.Key))
                {
                    // 不读取、不覆盖这一个分件，但继续合并其他分件。保留此前基线和草稿。
                    scene.Slots[i] = CopyPart(old);
                    continue;
                }
                string Name(PartSlot s) => s.MenuName;
                string Targets(PartSlot s) => string.Join(",", s.Targets.Select(o => o == null ? 0 : o.GetInstanceID()));
                string Shapes(PartSlot s) => JsonUtility.ToJson(new PartSlot { UseBlendshapes = s.UseBlendshapes, BlendshapesInverted = s.BlendshapesInverted, Blendshapes = s.Blendshapes });
                string Materials(PartSlot s) => JsonUtility.ToJson(new PartSlot { UseMaterials = s.UseMaterials, MaterialsInverted = s.MaterialsInverted, Materials = s.Materials });
                Merge(Name(old), Name(draft), Name(current), () => draft.MenuName = current.MenuName, draft.Key + " 名称");
                string Icon(PartSlot s) => (s.Icon == null ? 0 : s.Icon.GetInstanceID()) + ":" + s.IconGenerated;
                Merge(Icon(old), Icon(draft), Icon(current), () => { draft.Icon = current.Icon; draft.IconConfigured = true; draft.IconGenerated = current.IconGenerated; draft.IconSourceKnown = true; }, draft.Key + " 图标");
                Merge(old.IconTransparentCompatibility.ToString(), draft.IconTransparentCompatibility.ToString(), current.IconTransparentCompatibility.ToString(),
                    () => draft.IconTransparentCompatibility = current.IconTransparentCompatibility, draft.Key + " 截图兼容");
                Merge(Targets(old), Targets(draft), Targets(current), () => draft.Targets = current.Targets, draft.Key + " 物体");
                Merge(Shapes(old), Shapes(draft), Shapes(current), () => { draft.UseBlendshapes = current.UseBlendshapes; draft.BlendshapesInverted = current.BlendshapesInverted; draft.Blendshapes = current.Blendshapes; draft.AdvancedExpanded = true; }, draft.Key + " 形态键");
                Merge(Materials(old), Materials(draft), Materials(current), () => { draft.UseMaterials = current.UseMaterials; draft.MaterialsInverted = current.MaterialsInverted; draft.Materials = current.Materials; draft.AdvancedExpanded = true; }, draft.Key + " 材质");
            }
            if (conflicts.Count > 0)
            {
                Notify("窗口与场景存在冲突", string.Join("、", conflicts) + " 被两边同时修改，未覆盖。可导出诊断记录；确认场景设置后点击重新读取。");
                WardrobeDiagnostics.Record("分件", "合并冲突", string.Join(",", conflicts));
                return false;
            }
            Undo.RecordObject(this, "同步场景 MA 配置");
            clothingMenuName = merged.MenuName; slots = merged.Slots; reserveOutfitSwitch = merged.ReserveSwitch;
            enableBatchControl = merged.BatchControl; partPresets = merged.Presets;
            loadedSceneSnapshot = CopyConfiguration(scene);
            loadedConfigurationSignature = ConfigurationSignature(scene);
            loadedNamingSignature = MenuNamingSignature();
            loadedSceneSignature = SceneMenuSignature();
            if (skipped.Count > 0)
                Notify("部分配置只读保留", string.Join("；", skipped.Values) + " 其他可读取分件已同步；生成更新仍需先处理这些配置。");
            else if (feedback != null && (feedback.text.StartsWith("场景配置未同步") ||
                     feedback.text.StartsWith("部分配置只读保留"))) feedback.style.display = DisplayStyle.None;
            WardrobeDiagnostics.Record("分件", "合并场景配置", "成功，保留窗口未冲突的编辑");
            return true;
        }

        private string DiagnosticState()
        {
            var text = new StringBuilder();
            text.AppendLine("hierarchyEvents=" + hierarchyEvents + ", hierarchyPasses=" + hierarchyPasses + ", fullRefreshes=" + fullRefreshes);
            text.AppendLine("pendingRefresh=" + (hierarchyRefresh != null) + ", unsaved=" + HasUnsavedConfiguration());
            text.AppendLine("synchronizationAttempts=" + synchronizationAttempts + ", windowFocused=" + (focusedWindow == this));
            text.AppendLine("focused=" + (rootVisualElement.focusController?.focusedElement as VisualElement)?.name);
            text.AppendLine("previewSlots=" + string.Join(",", previewedSlots));
            text.AppendLine("反馈=" + feedback?.text);
            text.AppendLine("窗口配置=" + JsonUtility.ToJson(CurrentConfiguration(), true));
            text.AppendLine("读取基线=" + JsonUtility.ToJson(loadedSceneSnapshot, true));
            WardrobeDiagnostics.DescribeTree(text, loadedMenuRoot);
            if (clothingRoot != null)
                foreach (var t in clothingRoot.GetComponentsInChildren<Transform>(true))
                    text.AppendLine("衣服层级 " + RelativePath(clothingRoot.transform, t) + ", activeSelf=" + t.gameObject.activeSelf + ", activeInHierarchy=" + t.gameObject.activeInHierarchy);
            return text.ToString();
        }

        private static bool ValidateBatchNode(WardrobeGeneratedMenu marker)
        {
            var batch = marker.BatchControlNode;
            var menu = batch.GetComponent<ModularAvatarMenuItem>();
            if (!batch.activeSelf || menu == null || !menu.enabled ||
                menu.Control?.type != VRCExpressionsMenu.Control.ControlType.SubMenu ||
                menu.MenuSource != SubmenuSource.Children || menu.menuSource_otherObjectChildren != null ||
                string.IsNullOrEmpty(marker.PartParameterPrefix) || marker.PartPresets == null ||
                batch.transform.childCount != 2 + marker.PartPresets.Count) return false;
            var nodes = new[] { batch }.Concat(batch.transform.Cast<Transform>().Select(t => t.gameObject));
            if (nodes.Any(n => n.GetComponents<ModularAvatarMenuItem>().Length != 1 ||
                n.GetComponents<Component>().Any(c => !(c is Transform) && !(c is ModularAvatarMenuItem)))) return false;
            for (int i = 0; i < batch.transform.childCount; i++)
            {
                var node = batch.transform.GetChild(i).gameObject;
                var item = node.GetComponent<ModularAvatarMenuItem>();
                if (!node.activeSelf || node.transform.childCount != 0 || !item.enabled || item.Control == null ||
                    item.Control.type != VRCExpressionsMenu.Control.ControlType.Button ||
                    item.Control.parameter?.name != marker.PartParameterPrefix + "Batch" ||
                    item.Control.value != i + 1 || item.isSaved || item.isSynced || item.isDefault || item.automaticValue)
                    return false;
            }
            return true;
        }

        private static void CreateBatchMenu(GameObject root, WardrobeGeneratedMenu marker, GameObject previousBatch)
        {
            var batch = CreateChild(root.transform, previousBatch == null ? "批量控制" : ReadMenuName(previousBatch));
            marker.BatchControlNode = batch;
            var menu = Undo.AddComponent<ModularAvatarMenuItem>(batch);
            menu.label = "";
            menu.MenuSource = SubmenuSource.Children;
            menu.Control = new VRCExpressionsMenu.Control { type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                icon = previousBatch?.GetComponent<ModularAvatarMenuItem>()?.Control?.icon ?? WardrobeBatchIcons.Load("BatchControl") };
            var names = new[] {
                previousBatch == null ? "全部显示" : ReadMenuName(previousBatch.transform.GetChild(0).gameObject),
                previousBatch == null ? "全部隐藏" : ReadMenuName(previousBatch.transform.GetChild(1).gameObject)
            }.Concat(marker.PartPresets.Select(p => p.Name)).ToArray();
            for (int i = 0; i < names.Length; i++)
            {
                var previousButton = previousBatch == null ? null : i < 2
                    ? (i < previousBatch.transform.childCount ? previousBatch.transform.GetChild(i) : null)
                    : previousBatch.transform.Cast<Transform>().Skip(2).FirstOrDefault(t => ReadMenuName(t.gameObject) == names[i]);
                var button = Undo.AddComponent<ModularAvatarMenuItem>(CreateChild(batch.transform, names[i]));
                button.label = "";
                button.Control = new VRCExpressionsMenu.Control { type = VRCExpressionsMenu.Control.ControlType.Button,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = marker.PartParameterPrefix + "Batch" }, value = i + 1,
                    icon = previousButton?.GetComponent<ModularAvatarMenuItem>()?.Control?.icon
                        ?? WardrobeBatchIcons.Load(i == 0 ? "ShowAll" : i == 1 ? "HideAll" : "Preset") };
                button.automaticValue = false;
                button.isSaved = false; button.isSynced = false; button.isDefault = false;
                EditorUtility.SetDirty(button);
            }
            EditorUtility.SetDirty(menu);
        }

        private static string ReadMenuName(GameObject node)
        {
            var label = node.GetComponent<ModularAvatarMenuItem>()?.label;
            return string.IsNullOrEmpty(label) ? node.name : label;
        }

        private static void StyleHeading(Label label)
        {
            label.style.fontSize = 15;
            label.style.unityFontStyleAndWeight = FontStyle.Normal;
            label.style.marginBottom = 10;
        }

        private void Notify(string title, string message, string ignored = null)
        {
            WardrobeDiagnostics.Record("分件", title, message);
            if (feedback == null) { ShowNotification(new GUIContent(message)); return; }
            feedback.text = title + " · " + message;
            feedback.messageType = HelpBoxMessageType.Info;
            feedback.style.display = DisplayStyle.Flex;
        }

        private static void SetRoundedBorder(VisualElement element, Color color)
        {
            element.style.borderTopWidth = 1;
            element.style.borderBottomWidth = 1;
            element.style.borderLeftWidth = 1;
            element.style.borderRightWidth = 1;
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
            element.style.borderTopLeftRadius = 3;
            element.style.borderTopRightRadius = 3;
            element.style.borderBottomLeftRadius = 3;
            element.style.borderBottomRightRadius = 3;
        }

        private void RefreshAll()
        {
            if (mainGrid == null) return;
            fullRefreshes++;
            WardrobeDiagnostics.Record("分件", "刷新界面", "count=" + fullRefreshes);
            meshHierarchySignature = MeshHierarchySignature();
            rootNameLabel.text = clothingRoot == null ? "未选择衣服" : clothingRoot.name;
            SetRoundedBorder(rootDropZone, clothingRoot == null ? BorderColor : AccentColor);
            int used = slots.Count(IsUsed);
            statusLabel.text = "已使用 " + used + " 个分类 · 空分类不会生成菜单" +
                               (loadedMenuRoot == null ? "" : " · 正在编辑已有菜单");
            generateButton.text = loadedMenuRoot == null
                ? "生成菜单"
                : "更新菜单";
            generateButton.SetEnabled(clothingRoot != null && (used > 0 || reserveOutfitSwitch));
            closeAllButton.SetEnabled(clothingRoot != null && used > 0);
            openAllButton.SetEnabled(clothingRoot != null);
            RefreshMeshProgress();
            RefreshOutfitIcon();
            RefreshBatchSettings();

            mainGrid.Clear();
            backupGrid.Clear();
            for (int i = 0; i < slots.Count; i++)
                (i < MainCount ? mainGrid : backupGrid).Add(CreateCard(i));
            RefreshDetails();
            ApplyTextStyle();
        }

        private void RefreshMeshProgress()
        {
            unassignedMeshesContent.Clear();
            if (clothingRoot == null)
            {
                meshStatusLabel.text = "拖入衣服后显示模型分配进度";
                meshStatusLabel.style.color = MutedColor;
                unassignedMeshesFoldout.style.display = DisplayStyle.None;
                return;
            }
            var all = clothingRoot.GetComponentsInChildren<Transform>(true)
                .Where(transform => transform != clothingRoot.transform && HasMesh(transform.gameObject))
                .Select(transform => transform.gameObject).ToArray();
            var assignedRoots = slots.SelectMany(slot => slot.Targets)
                .Where(target => target != null).Distinct().ToArray();
            var unassigned = all.Where(meshNode => !assignedRoots.Any(target =>
                meshNode == target || meshNode.transform.IsChildOf(target.transform))).ToArray();
            int classified = all.Length - unassigned.Length;
            meshStatusLabel.text = "模型分配：" + classified + " / " + all.Length +
                                   " · 还剩 " + unassigned.Length + " 个";
            meshStatusLabel.style.color = unassigned.Length == 0 ? WardrobeUiStyle.Link : MutedColor;
            unassignedMeshesFoldout.style.display = unassigned.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            unassignedMeshesFoldout.text = "查看未分配模型（" + unassigned.Length + "）";
            unassignedMeshesScroll.style.height = Mathf.Min(unassigned.Length, 6) * 26;
            foreach (var meshNode in unassigned)
            {
                var captured = meshNode;
                var button = new Button(() =>
                {
                    Selection.activeGameObject = captured;
                    EditorGUIUtility.PingObject(captured);
                }) { text = RelativePath(clothingRoot.transform, meshNode.transform) };
                button.tooltip = button.text;
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
                button.style.height = 24;
                button.style.flexShrink = 0;
                button.style.marginTop = 0;
                button.style.marginBottom = 2;
                button.style.whiteSpace = WhiteSpace.NoWrap;
                button.style.overflow = Overflow.Hidden;
                button.style.textOverflow = TextOverflow.Ellipsis;
                unassignedMeshesContent.Add(button);
            }
        }

        private static bool HasMesh(GameObject obj)
        {
            var skin = obj.GetComponent<SkinnedMeshRenderer>();
            if (skin != null && skin.sharedMesh != null) return true;
            var filter = obj.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null;
        }

        private static string RelativePath(Transform root, Transform target)
        {
            var names = new List<string>();
            for (var current = target; current != null && current != root; current = current.parent)
                names.Add(current.name);
            names.Reverse();
            return string.Join(" / ", names);
        }

        private VisualElement CreateCard(int index)
        {
            var slot = slots[index];
            int count = slot.Targets.Count(target => target != null);
            bool advancedOnly = count == 0 && (slot.UseBlendshapes || slot.UseMaterials);
            var card = new VisualElement { name = "part-card-" + slot.Key };
            card.style.width = CardSize;
            card.style.height = CardSize;
            card.style.flexShrink = 0;
            card.style.marginRight = 9;
            card.style.marginBottom = 9;
            card.style.paddingLeft = 11;
            card.style.paddingRight = 11;
            card.style.paddingTop = 10;
            card.style.paddingBottom = 9;
            card.style.backgroundColor = selectedIndex == index ? SelectedColor : CardColor;
            SetRoundedBorder(card, selectedIndex == index ? AccentColor : BorderColor);

            var cardHeading = new VisualElement();
            cardHeading.style.flexDirection = FlexDirection.Row;
            cardHeading.style.alignItems = Align.FlexStart;
            cardHeading.style.minHeight = 38;
            cardHeading.style.flexShrink = 0;
            var thumbnail = new Image { image = slot.Icon, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            thumbnail.style.width = 24; thumbnail.style.height = 24; thumbnail.style.flexShrink = 0;
            thumbnail.style.marginRight = 6;
            cardHeading.Add(thumbnail);
            var headingText = new VisualElement();
            headingText.style.flexGrow = 1;
            headingText.style.flexShrink = 1;
            headingText.style.minWidth = 0;
            var category = new Label(PartDisplayName(slot.Category)) { tooltip = PartDisplayName(slot.Category), name = "part-category-" + slot.Key };
            category.style.fontSize = 15;
            category.style.unityFontStyleAndWeight = FontStyle.Normal;
            category.style.whiteSpace = WhiteSpace.NoWrap;
            category.style.overflow = Overflow.Hidden;
            category.style.textOverflow = TextOverflow.Ellipsis;
            category.style.minWidth = 0;
            headingText.Add(category);
            if (slot.Backup)
            {
                var badge = new Label("备用") { name = "part-backup-badge" };
                badge.style.fontSize = 10;
                badge.style.color = MutedColor;
                badge.style.alignSelf = Align.FlexStart;
                badge.style.marginTop = 2;
                badge.style.paddingLeft = badge.style.paddingRight = 4;
                badge.style.backgroundColor = new Color(.5f,.5f,.5f,.15f);
                badge.style.borderTopLeftRadius = badge.style.borderTopRightRadius = 3;
                badge.style.borderBottomLeftRadius = badge.style.borderBottomRightRadius = 3;
                headingText.Add(badge);
            }
            cardHeading.Add(headingText);
            var previewButton = new Button(() => TogglePreview(index));
            previewButton.name = "part-preview-" + slot.Key;
            previewButton.AddToClassList("wardrobe-state-button");
            previewButton.text = previewedSlots.Contains(index) ? "✓" : "";
            previewButton.style.color = Color.white;
            previewButton.tooltip = previewedSlots.Contains(index) ? "恢复此分件原有状态" : "临时隐藏此分件；完成后自动恢复，不修改 MA 反向条件";
            previewButton.style.width = 20;
            previewButton.style.height = 20;
            previewButton.style.minWidth = 20;
            previewButton.style.flexShrink = 0;
            previewButton.style.marginRight = 0;
            previewButton.style.marginLeft = 3;
            previewButton.style.paddingLeft = 0;
            previewButton.style.paddingRight = 0;
            previewButton.style.backgroundColor = previewedSlots.Contains(index) ? AccentColor : Color.clear;
            previewButton.style.borderTopLeftRadius = 6;
            previewButton.style.borderTopRightRadius = 6;
            previewButton.style.borderBottomLeftRadius = 6;
            previewButton.style.borderBottomRightRadius = 6;
            previewButton.style.borderTopColor = AccentColor;
            previewButton.style.borderBottomColor = AccentColor;
            previewButton.style.borderLeftColor = AccentColor;
            previewButton.style.borderRightColor = AccentColor;
            previewButton.SetEnabled(clothingRoot != null && string.IsNullOrEmpty(slot.ExternalReadOnlyReason) && (count > 0 || advancedOnly));
            previewButton.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            cardHeading.Add(previewButton);
            card.Add(cardHeading);

            var menuNameCaption = new Label("菜单名称");
            menuNameCaption.style.fontSize = 11;
            menuNameCaption.style.color = MutedColor;
            menuNameCaption.style.marginTop = 5;
            card.Add(menuNameCaption);
            var menuNameField = new TextField { value = slot.MenuName };
            menuNameField.name = "part-menu-name-" + slot.Key;
            menuNameField.tooltip = "直接修改此分件在菜单中的名称，生成或更新菜单后生效。";
            menuNameField.style.marginTop = 2;
            menuNameField.style.marginLeft = menuNameField.style.marginRight = 0;
            menuNameField.style.flexShrink = 0;
            menuNameField.style.minWidth = 0;
            menuNameField.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "修改分件菜单名称");
                slot.MenuName = evt.newValue;
            });
            string lastCommittedName=slot.MenuName;
            menuNameField.RegisterCallback<FocusOutEvent>(evt =>
            {
                if(menuNameField.value==lastCommittedName)return;
                lastCommittedName=menuNameField.value;
                RememberVocabulary(slot.Key,lastCommittedName);
            });
            // 输入框点击不能触发整张卡片重建，否则输入焦点会丢失。
            menuNameField.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            card.Add(menuNameField);
            var vocabulary = new Button(() =>
            {
                selectedIndex = index;
                RefreshAll();
                OpenSelectedVocabulary();
            }) { text = "词库", name = "part-vocabulary-" + slot.Key, tooltip = "从词库选择菜单名称" };
            vocabulary.style.width = 54;
            vocabulary.style.alignSelf = Align.FlexStart;
            vocabulary.style.flexShrink = 0;
            vocabulary.style.marginLeft = 0;
            vocabulary.style.marginTop = 4;
            vocabulary.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            card.Add(vocabulary);

            var counter = new Label(count == 0 ? (advancedOnly ? "仅高级效果" : "拖入分件") : count + " 个物体");
            if (!string.IsNullOrEmpty(slot.ExternalReadOnlyReason))
            {
                counter.text = "外部配置 · 只读保留";
                counter.tooltip = slot.ExternalReadOnlyReason;
            }
            counter.style.marginTop = 5;
            counter.style.whiteSpace = WhiteSpace.NoWrap;
            counter.style.overflow = Overflow.Hidden;
            counter.style.textOverflow = TextOverflow.Ellipsis;
            counter.style.flexShrink = 0;
            counter.style.color = count == 0 && !advancedOnly ? MutedColor : WardrobeUiStyle.Link;
            card.Add(counter);

            var preview = new Label(count == 0
                ? (advancedOnly ? "已开启形态键或材质替换" : "")
                : string.Join("\n", slot.Targets.Where(target => target != null).Take(2).Select(target => target.name)) +
                  (count > 2 ? "\n…" : ""));
            preview.style.marginTop = 5;
            preview.style.whiteSpace = WhiteSpace.Normal;
            preview.style.color = MutedColor;
            preview.style.fontSize = 11;
            preview.style.maxHeight = 28;
            preview.style.overflow = Overflow.Hidden;
            preview.tooltip = string.Join("\n", slot.Targets.Where(target => target != null).Select(target => target.name));
            card.Add(preview);

            card.RegisterCallback<ClickEvent>(evt =>
            {
                if (selectedIndex != index) selectedTargets.Clear();
                selectedIndex = index;
                RefreshAll();
                evt.StopPropagation();
            });
            card.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                DragAndDrop.visualMode = clothingRoot != null && DragAndDrop.objectReferences.OfType<GameObject>().Any()
                    ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                evt.StopPropagation();
            });
            card.RegisterCallback<DragPerformEvent>(evt =>
            {
                if (clothingRoot == null)
                {
                    Notify("先选择衣服", "请先把衣服根对象拖进上方大框。", "确定");
                    return;
                }
                var candidates = DragAndDrop.objectReferences.OfType<GameObject>().ToArray();
                DragAndDrop.AcceptDrag();
                int added = AddTargets(slot, candidates);
                if (selectedIndex != index) selectedTargets.Clear();
                selectedIndex = index;
                if (added == 0 && !lastAddBlockedTransfer)
                    ShowNotification(new GUIContent("未添加：请拖入这套衣服下尚未分配的物体"));
                RefreshAll();
                evt.StopPropagation();
            });
            return card;
        }

        private void RefreshDetails()
        {
            FollowVocabulary();
            details.Clear();
            iconSettings.Clear();
            if (selectedIndex < 0 || selectedIndex >= slots.Count)
            {
                var iconHint = new Foldout { text = "菜单图标", value = false };
                iconHint.Add(new Label("点击分件卡片，设置该分件的菜单图标。"));
                iconSettings.Add(iconHint);
                var hint = new Label("名称可直接在卡片内修改。点击卡片，查看分件或设置高级效果。");
                hint.style.color = MutedColor;
                details.Add(hint);
                return;
            }

            var slot = slots[selectedIndex];
            var heading = new Label(PartDisplayName(slot.Category) + (slot.Backup ? " · 备用" : ""));
            heading.tooltip = "打开这个分件开关时，会隐藏此分类中的全部物体。";
            StyleHeading(heading);
            details.Add(heading);

            if (!string.IsNullOrEmpty(slot.ExternalReadOnlyReason))
            {
                details.Add(new HelpBox(slot.ExternalReadOnlyReason + "\n此分件暂不转换；其他分件仍可同步。请在 Inspector 处理该组件后重新读取。", HelpBoxMessageType.Warning));
                return;
            }


            var targetGrid = CreateGrid();
            DrawPartIcon(iconSettings, slot);
            foreach (var target in slot.Targets.Where(target => target != null).ToArray())
            {
                var captured = target;
                var tile = new VisualElement();
                tile.style.width = 150;
                tile.style.minHeight = 76;
                tile.style.paddingLeft = 8;
                tile.style.paddingRight = 8;
                tile.style.paddingTop = 8;
                tile.style.paddingBottom = 6;
                tile.style.marginRight = 8;
                tile.style.marginBottom = 8;
                tile.style.backgroundColor = selectedTargets.Contains(target) ? SelectedColor : CardColor;
                SetRoundedBorder(tile, selectedTargets.Contains(target) ? AccentColor : BorderColor);
                var targetName = new Label(target.name);
                targetName.style.whiteSpace = WhiteSpace.Normal;
                targetName.style.flexGrow = 1;
                tile.Add(targetName);
                tile.RegisterCallback<ClickEvent>(evt =>
                {
                    if (editMode)
                    {
                        if (!selectedTargets.Add(captured)) selectedTargets.Remove(captured);
                        RefreshDetails();
                    }
                    else
                    {
                        Selection.activeGameObject = captured;
                        EditorGUIUtility.PingObject(captured);
                    }
                    evt.StopPropagation();
                });
                targetGrid.Add(tile);
            }
            details.Add(targetGrid);

            if (editMode)
            {
                var removeSelected = new Button(() => RemoveSelectedTargets(slot))
                    { text = "移除所选（" + selectedTargets.Count + "）" };
                removeSelected.tooltip = "仅移除分类记录，不删除场景物体。";
                removeSelected.SetEnabled(selectedTargets.Count > 0);
                removeSelected.style.height = 30;
                removeSelected.style.marginBottom = 8;
                details.Add(removeSelected);
            }

            var addSelected = new Button(() =>
            {
                if (clothingRoot == null)
                {
                    Notify("先选择衣服", "请先把衣服根对象拖进上方大框。", "确定");
                    return;
                }
                int added = AddTargets(slot, Selection.gameObjects);
                if (added == 0 && !lastAddBlockedTransfer)
                    ShowNotification(new GUIContent("没有可加入的当前衣服子物体"));
                RefreshAll();
            }) { text = "添加选中分件" };
            addSelected.style.width = 200;
            addSelected.style.height = 28;
            details.Add(addSelected);

            var advanced = new Foldout
            {
                text = "高级设置",
                value = slot.AdvancedExpanded
            };
            advanced.style.marginTop = 16;
            advanced.RegisterValueChangedCallback(evt => slot.AdvancedExpanded = evt.newValue);
            details.Add(advanced);

            var shapeToggle = new Toggle("调整形态键") { value = slot.UseBlendshapes };
            shapeToggle.RegisterValueChangedCallback(evt =>
            {
                slot.UseBlendshapes = evt.newValue;
                if (evt.newValue && slot.Blendshapes.Count == 0)
                    slot.Blendshapes.Add(new BlendshapeRule());
                RefreshAll();
            });
            advanced.Add(shapeToggle);
            if (slot.UseBlendshapes) DrawBlendshapes(advanced, slot);

            var materialToggle = new Toggle("启用材质替换") { value = slot.UseMaterials };
            materialToggle.style.marginTop = 12;
            materialToggle.RegisterValueChangedCallback(evt =>
            {
                slot.UseMaterials = evt.newValue;
                if (evt.newValue && slot.Materials.Count == 0)
                    slot.Materials.Add(new MaterialRule());
                RefreshAll();
            });
            advanced.Add(materialToggle);
            if (slot.UseMaterials) DrawMaterials(advanced, slot);
            WardrobeUiStyle.Apply(details);
        }

        internal void OpenSelectedVocabulary()
        {
            if (selectedIndex < 0 || selectedIndex >= slots.Count) return;
            var slot = slots[selectedIndex];
            WardrobeVocabularyWindow.Open(this, clothingRoot, slot.Key, slot.Category);
        }

        internal void FollowVocabulary()
        {
            var slot = selectedIndex < 0 || selectedIndex >= slots.Count ? null : slots[selectedIndex];
            WardrobeVocabularyWindow.Follow(this, clothingRoot, slot?.Key, slot?.Category);
        }

        internal bool ApplyVocabulary(GameObject expectedRoot, string key, string word, out string name)
        {
            name = null;
            if (clothingRoot != expectedRoot) return false;
            var slot = slots.FirstOrDefault(s=>s.Key==key);
            if (slot==null) return false;
            word = WardrobeNameVocabulary.Clean(word);
            name = word +
                (slot.MenuName != null && slot.MenuName.EndsWith("（备用）", StringComparison.Ordinal) ? "（备用）" : "");
            if (name == slot.MenuName) return false;
            Undo.RecordObject(this, "从词库填写菜单名称");
            slot.MenuName = name;
            rootVisualElement.Q<TextField>("part-menu-name-"+key)?.SetValueWithoutNotify(name);
            RefreshDetails();
            Repaint();
            return true;
        }

        internal void RememberVocabulary(string key,string menuName)
        {
            var slot=slots.FirstOrDefault(s=>s.Key==key);
            if(slot==null||string.IsNullOrWhiteSpace(menuName))return;
            string word=menuName.Trim();
            if(word.StartsWith("隐藏",StringComparison.Ordinal))word=word.Substring(2).Trim();
            if(word.EndsWith("（备用）",StringComparison.Ordinal))word=word.Substring(0,word.Length-4).Trim();
            if(string.IsNullOrWhiteSpace(word))return;
            try
            {
                word=WardrobeNameVocabulary.Clean(word);
                var vocabulary=WardrobeNameVocabulary.Shared;
                var existing=vocabulary.Query(slot.Category,word).FirstOrDefault(t=>t.Word==word);
                if(existing==null||existing.Custom)vocabulary.Add(word,slot.Category);
                vocabulary.Record(word,slot.Category);
            }
            catch(Exception e){WardrobeDiagnostics.Record("分件","名称收录失败",e.Message);}
        }

        private void DrawPartIcon(VisualElement parent, PartSlot slot)
        {
            var section = new Foldout { text = "菜单图标 · " + slot.MenuName, value = partIconsExpanded, name = "part-icon-settings" };
            section.RegisterValueChangedCallback(evt =>
            {
                if (evt.target == section) partIconsExpanded = evt.newValue;
            });
            section.style.marginBottom = 12;
            parent.Add(section);
            var image = new Image { image = slot.Icon, scaleMode = ScaleMode.ScaleToFit };
            image.style.width = 72; image.style.height = 72;
            image.style.backgroundColor = CardColor;
            SetRoundedBorder(image, BorderColor);
            section.Add(image);
            var field = new ObjectField("拖入图片") { objectType = typeof(Texture2D), allowSceneObjects = false, value = slot.Icon, name = "part-icon-field" };
            field.RegisterValueChangedCallback(evt => SetPartIcon(slot, evt.newValue as Texture2D));
            section.Add(field);
            var summary = new Label();
            void UpdateSummary() { summary.text = "拍摄：X " + slot.IconPitch.ToString("0.#") + "° · Y " + slot.IconYaw.ToString("0.#") + "°"; }
            UpdateSummary(); section.Add(summary);
            var settings = new Foldout {text = "拍摄设置", value = false};
            section.Add(settings);
            var pitch = new Slider("俯仰 X", -80, 80) {value = slot.IconPitch, showInputField = true, name = "part-icon-pitch"};
            var yaw = new Slider("旋转 Y", 0, 360) {value = slot.IconYaw, showInputField = true, name = "part-icon-yaw"};
            var padding = new Slider("边缘留白", .02f, .4f) {value = slot.IconPadding, showInputField = true};
            pitch.tooltip = "正数为从上方拍摄；调整后点击自动截图才应用，不会覆盖现有图标。";
            pitch.RegisterValueChangedCallback(evt => { slot.IconPitch = evt.newValue; slot.IconViewVersion = 3; slot.IconAnglesInitialized = true; UpdateSummary(); });
            yaw.RegisterValueChangedCallback(evt => { slot.IconYaw = evt.newValue; slot.IconViewVersion = 3; slot.IconAnglesInitialized = true; UpdateSummary(); });
            padding.RegisterValueChangedCallback(evt => { slot.IconPadding = evt.newValue; slot.IconAnglesInitialized = true; });
            var presets = new VisualElement(); presets.style.flexDirection = FlexDirection.Row; presets.style.flexWrap = Wrap.Wrap;
            void SetView(float x, float y)
            {
                Undo.RecordObject(this, "调整图标拍摄角度"); slot.IconPitch = x; slot.IconYaw = y; slot.IconAnglesInitialized = true;
                slot.IconViewVersion = 3;
                pitch.SetValueWithoutNotify(x); yaw.SetValueWithoutNotify(y); UpdateSummary();
            }
            presets.Add(new Button(() => SetView(22,335)) {text = "标准", name = "part-icon-oblique"});
            presets.Add(new Button(() => SetView(45,45)) {text = "大斜角"});
            presets.Add(new Button(() => SetView(0,0)) {text = "正面"});
            presets.Add(new Button(() => SetView(0,90)) {text = "侧面"});
            settings.Add(presets); settings.Add(pitch); settings.Add(yaw); settings.Add(padding);
            var compatibility = new Toggle("透明截图兼容") { value = slot.IconTransparentCompatibility, name = "part-icon-transparent-compatibility" };
            compatibility.tooltip = "仅截图时使用临时材质副本，先画背面再画正面，原材质不变。不支持的材质保持原样；复杂多层透明仍可能存在排序差异。调整后点击自动截图或更新图标。生成／更新菜单后保存此选项。";
            compatibility.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(this, "修改透明截图兼容"); slot.IconTransparentCompatibility = evt.newValue;
            });
            settings.Add(compatibility);
            var actions = new VisualElement(); actions.style.flexDirection = FlexDirection.Row; actions.style.flexWrap = Wrap.Wrap;
            var capture = new Button(() => CapturePartIconWithMa(slot)) { text = "自动截图", name = "part-icon-capture" };
            capture.SetEnabled(slot.Targets.Any(t => t != null));
            actions.Add(capture);
            actions.Add(new Button(() => SetPartIcon(slot, null)) { text = "清除图标" });
            section.Add(actions);
            foreach (var button in presets.Children().Concat(actions.Children()).OfType<Button>())
            {
                button.style.height = 28; button.style.minWidth = 80;
                button.style.marginTop = button.style.marginLeft = 0;
                button.style.marginRight = 6; button.style.marginBottom = 4;
            }
            var note = new Label("自动图标重拍时原位覆盖，先备份上一版；手动图片不覆盖。首次绑定仍需更新菜单。") { tooltip = "备份在 Library/Zincat/IconBackups，每张图只保留上一版；图片内容覆盖不能用普通场景撤销恢复。" };
            note.style.whiteSpace = WhiteSpace.Normal; note.style.color = MutedColor;
            section.Add(note);
            image.RegisterCallback<DragUpdatedEvent>(evt => { DragAndDrop.visualMode = DragAndDrop.objectReferences.OfType<Texture2D>().Any() ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected; evt.StopPropagation(); });
            image.RegisterCallback<DragPerformEvent>(evt => { var texture = DragAndDrop.objectReferences.OfType<Texture2D>().FirstOrDefault(); if (texture != null) { DragAndDrop.AcceptDrag(); SetPartIcon(slot, texture); } evt.StopPropagation(); });
        }

        private void SetPartIcon(PartSlot slot, Texture2D icon)
        {
            if (icon != null && !EditorUtility.IsPersistent(icon)) { Notify("图片未保存", "请拖入 Project 中的图片资源。"); return; }
            Undo.RecordObject(this, "修改分件图标");
            slot.Icon = icon; slot.IconConfigured = true; slot.IconGenerated = false; slot.IconSourceKnown = true;
            RefreshAll();
        }

        private bool CapturePartIcon(PartSlot slot)
        {
            if (slot.Icon != null && !slot.IconGenerated)
            { Notify("手动图片已保留", "如需改用自动截图，请先清除该图标；原图片文件不会删除。"); return false; }
            try
            {
                WardrobeMaIconPreview.Active?.SelectTargets(slot.Targets);
                var icon = WardrobePartIconRenderer.Capture(slot.Targets, clothingRoot.transform.parent.rotation, slot.IconYaw, slot.IconPitch, slot.IconPadding, slot.IconGenerated ? slot.Icon : null, slot.IconTransparentCompatibility, WardrobeIconStorage.Folder(clothingRoot));
                Undo.RecordObject(this, "生成分件图标");
                slot.Icon = icon; slot.IconConfigured = true; slot.IconGenerated = true; slot.IconSourceKnown = true;
                return true;
            }
            catch (Exception error) { Notify("未生成图标", error.Message); return false; }
        }

        private async void CapturePartIconWithMa(PartSlot slot)
        {
            if (iconCaptureBusy || clothingRoot == null) return;
            if (slot.Icon != null && !slot.IconGenerated) { CapturePartIcon(slot); return; }
            iconCaptureBusy = true; var expected = clothingRoot; rootVisualElement.SetEnabled(false);
            try
            {
                using (await WardrobeMaIconPreview.PrepareAsync(slot.Targets))
                    if (this != null && clothingRoot == expected && CapturePartIcon(slot)) RefreshAll();
            }
            catch (Exception e) { if (this != null) Notify("未生成图标", e.GetBaseException().Message); }
            finally { iconCaptureBusy = false; if (this != null) rootVisualElement.SetEnabled(true); }
        }

        [Serializable] private sealed class OutfitIconDraft
        {
            public GameObject Root;
            public Texture2D Icon;
            public bool Generated;
        }
        private OutfitIconDraft OutfitDraft()
        {
            if (outfitIconDrafts == null) outfitIconDrafts = new List<OutfitIconDraft>();
            outfitIconDrafts.RemoveAll(d => d == null || d.Root == null);
            return outfitIconDrafts.FirstOrDefault(d => d.Root == clothingRoot);
        }
        private Texture2D OutfitIcon() => loadedMenuRoot == null ? OutfitDraft()?.Icon : loadedMenuRoot.GetComponent<ModularAvatarMenuItem>()?.Control?.icon;
        private void RefreshOutfitIcon()
        {
            outfitIconField?.SetValueWithoutNotify(clothingRoot == null ? null : OutfitIcon());
            outfitIconField?.SetEnabled(clothingRoot != null); outfitIconButton?.SetEnabled(clothingRoot != null);
        }
        private void SetOutfitIcon(Texture2D icon)
        {
            if (clothingRoot == null) return;
            if (icon != null && !EditorUtility.IsPersistent(icon)) return;
            Undo.RecordObject(this, "修改衣服图标");
            var draft = OutfitDraft(); if (draft == null) { draft = new OutfitIconDraft { Root = clothingRoot }; outfitIconDrafts.Add(draft); }
            draft.Icon = icon; draft.Generated = false;
            if (loadedMenuRoot != null)
            {
                WardrobeMenuIconGenerator.Assign(loadedMenuRoot.GetComponent<ModularAvatarMenuItem>(), icon);
                var marker = loadedMenuRoot.GetComponent<WardrobeGeneratedMenu>();
                if (marker != null) { Undo.RecordObject(marker, "记录手动衣服图标"); marker.GeneratedOutfitIcon = null; EditorUtility.SetDirty(marker); }
                WardrobeMenuIconGenerator.SyncCollectionsFor(marker);
            }
            RefreshOutfitIcon();
        }
        private async void CaptureOutfitIcon()
        {
            if (clothingRoot == null || iconCaptureBusy) return;
            var marker = loadedMenuRoot == null ? null : loadedMenuRoot.GetComponent<WardrobeGeneratedMenu>();
            var previous = OutfitIcon(); var draft = OutfitDraft();
            bool automatic = marker != null ? previous != null && previous == marker.GeneratedOutfitIcon : draft != null && draft.Generated;
            if (previous != null && !automatic) { Notify("手动图片已保留", "请先清除衣服图标的引用，再使用自动截图；图片文件不会删除。"); return; }
            iconCaptureBusy = true; var expected = clothingRoot; rootVisualElement.SetEnabled(false);
            try
            {
                var icon = await WardrobeMenuIconGenerator.CaptureOutfit(expected, automatic ? previous : null);
                if (this == null || clothingRoot != expected) return;
                Undo.RecordObject(this, "生成衣服图标");
                draft = OutfitDraft(); if (draft == null) { draft = new OutfitIconDraft { Root = expected }; outfitIconDrafts.Add(draft); }
                draft.Icon = icon; draft.Generated = true;
                if (marker != null) WardrobeMenuIconGenerator.ApplyOutfit(marker, icon);
                RefreshOutfitIcon();
            }
            catch (Exception e) { if (this != null) Notify("未生成衣服图标", e.GetBaseException().Message); }
            finally { iconCaptureBusy = false; if (this != null) rootVisualElement.SetEnabled(true); }
        }

        private void GenerateMissingIcons()
        {
            GeneratePartIcons(false);
        }

        private void UpdateGeneratedIcons()
        {
            GeneratePartIcons(true);
        }

        private async void GeneratePartIcons(bool updateExisting)
        {
            if (clothingRoot == null || iconCaptureBusy) return;
            iconCaptureBusy = true; var expected = clothingRoot; rootVisualElement.SetEnabled(false);
            EnsureSlots();
            int generated = 0, failed = 0;
            var updatedPaths = new HashSet<string>();
            try
            {
                var missing = slots.Where(s => (updateExisting ? s.Icon != null && s.IconGenerated : s.Icon == null) && s.Targets.Any(t => t != null) && string.IsNullOrEmpty(s.ExternalReadOnlyReason)).ToArray();
                using (missing.Length == 0 ? null : await WardrobeMaIconPreview.PrepareAsync(new[] { clothingRoot }.Concat(missing.SelectMany(s => s.Targets))))
                for (int i = 0; i < missing.Length; i++)
                {
                    if (updateExisting && !updatedPaths.Add(AssetDatabase.GetAssetPath(missing[i].Icon))) continue;
                    if (EditorUtility.DisplayCancelableProgressBar("生成分件图标", missing[i].MenuName, (float)i / missing.Length)) break;
                    if (this == null || clothingRoot != expected) break;
                    if (CapturePartIcon(missing[i])) generated++; else failed++;
                }
            }
            catch (Exception e) { failed++; if (this != null) Notify("图标处理失败", e.GetBaseException().Message); }
            finally { iconCaptureBusy = false; EditorUtility.ClearProgressBar(); if (this != null) { rootVisualElement.SetEnabled(true); RefreshAll(); } }
            if (this == null) return;
            Notify("图标处理完成", (updateExisting ? "重拍 " : "新增 ") + generated + " 张；失败 " + failed + " 个。" +
                (updateExisting ? "已覆盖自动图标并备份上一版，手动图片已保留；原菜单引用同步更新。" : "已有图标未修改。点击生成／更新菜单后应用。"));
        }

        private void DrawBlendshapes(VisualElement parent, PartSlot slot)
        {
            var inverted = new Toggle("反向条件（开关关闭时生效）") { value = slot.BlendshapesInverted, name = "blendshapes-inverted" };
            inverted.tooltip = "保留 MA 形态键组件自己的反向条件，不改变物体隐藏开关。场景隐藏预览不执行反向设置，实际效果由 MA 处理。";
            inverted.RegisterValueChangedCallback(evt => { Undo.RecordObject(this, "修改形态键反向条件"); slot.BlendshapesInverted = evt.newValue; RefreshDetails(); });
            parent.Add(inverted);
            var note = new Label(slot.BlendshapesInverted
                ? "已保留反向条件：分件开关关闭时，应用这些形态键设置。隐藏预览不执行此反向效果。"
                : "隐藏分件时，将所选形态键调整到指定值。可以选择衣服或角色身体的模型。");
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.color = MutedColor;
            note.style.marginBottom = 8;
            parent.Add(note);
            var grid = CreateGrid();
            parent.Add(grid);
            foreach (var rule in slot.Blendshapes.ToArray())
            {
                var card = CreateRuleCard();
                grid.Add(card);
                card.Add(new Label("形态键设置"));
                var renderer = new ObjectField("目标网格")
                {
                    objectType = typeof(SkinnedMeshRenderer), allowSceneObjects = true, value = rule.Renderer
                };
                renderer.RegisterValueChangedCallback(evt =>
                {
                    rule.Renderer = evt.newValue as SkinnedMeshRenderer;
                    rule.ShapeName = GetShapeNames(rule.Renderer).FirstOrDefault() ?? "";
                    RefreshDetails();
                });
                card.Add(renderer);

                var names = GetShapeNames(rule.Renderer);
                if (!string.IsNullOrEmpty(rule.ShapeName) && !names.Contains(rule.ShapeName))
                    names.Insert(0, rule.ShapeName);
                if (names.Count == 0)
                {
                    var missing = new Label("请选择带形态键的蒙皮网格（Skinned Mesh Renderer）。");
                    missing.style.whiteSpace = WhiteSpace.Normal;
                    missing.style.color = MutedColor;
                    card.Add(missing);
                }
                else
                {
                    var picker = new PopupField<string>("形态键", names,
                        Mathf.Max(0, names.IndexOf(rule.ShapeName)));
                    picker.RegisterValueChangedCallback(evt => rule.ShapeName = evt.newValue);
                    card.Add(picker);
                }
                var value = new Slider("目标值", 0, 100) { value = rule.Value, showInputField = true };
                value.RegisterValueChangedCallback(evt => rule.Value = evt.newValue);
                card.Add(value);
                var remove = new Button(() =>
                {
                    slot.Blendshapes.Remove(rule);
                    RefreshAll();
                }) { text = "移除此设置" };
                remove.style.marginTop = 8;
                card.Add(remove);
            }
            var add = new Button(() =>
            {
                slot.Blendshapes.Add(new BlendshapeRule());
                RefreshAll();
            }) { text = "＋ 添加形态键" };
            add.style.width = 180;
            parent.Add(add);
        }

        private void DrawMaterials(VisualElement parent, PartSlot slot)
        {
            var inverted = new Toggle("反向条件（开关关闭时生效）") { value = slot.MaterialsInverted, name = "materials-inverted" };
            inverted.tooltip = "仅控制 MA 材质组件自己的反向条件。场景隐藏预览不执行反向设置，实际效果由 MA 处理。";
            inverted.RegisterValueChangedCallback(evt => { Undo.RecordObject(this, "修改材质反向条件"); slot.MaterialsInverted = evt.newValue; RefreshDetails(); });
            parent.Add(inverted);
            var note = new Label(slot.MaterialsInverted
                ? "已保留反向条件：分件开关关闭时应用材质替换。隐藏预览不执行此反向效果。"
                : "隐藏分件时，将指定材质槽替换为所选材质。");
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.color = MutedColor;
            note.style.marginTop = 8;
            note.style.marginBottom = 8;
            parent.Add(note);
            var grid = CreateGrid();
            parent.Add(grid);
            foreach (var rule in slot.Materials.ToArray())
            {
                var card = CreateRuleCard();
                grid.Add(card);
                card.Add(new Label("材质替换设置"));
                var renderer = new ObjectField("目标网格")
                {
                    objectType = typeof(Renderer), allowSceneObjects = true, value = rule.Renderer
                };
                renderer.RegisterValueChangedCallback(evt =>
                {
                    rule.Renderer = evt.newValue as Renderer;
                    rule.MaterialIndex = 0;
                    RefreshDetails();
                });
                card.Add(renderer);

                var choices = GetMaterialChoices(rule.Renderer);
                if (choices.Count == 0)
                {
                    var missing = new Label("请选择带材质的模型渲染器（Renderer）。");
                    missing.style.whiteSpace = WhiteSpace.Normal;
                    missing.style.color = MutedColor;
                    card.Add(missing);
                }
                else
                {
                    var picker = new PopupField<string>("材质槽", choices,
                        Mathf.Clamp(rule.MaterialIndex, 0, choices.Count - 1));
                    picker.RegisterValueChangedCallback(evt => rule.MaterialIndex = choices.IndexOf(evt.newValue));
                    card.Add(picker);
                }
                var material = new ObjectField("替换为")
                {
                    objectType = typeof(Material), allowSceneObjects = false, value = rule.Replacement
                };
                material.RegisterValueChangedCallback(evt => rule.Replacement = evt.newValue as Material);
                card.Add(material);
                var remove = new Button(() =>
                {
                    slot.Materials.Remove(rule);
                    RefreshAll();
                }) { text = "移除此设置" };
                remove.style.marginTop = 8;
                card.Add(remove);
            }
            var add = new Button(() =>
            {
                slot.Materials.Add(new MaterialRule());
                RefreshAll();
            }) { text = "＋ 添加材质替换" };
            add.style.width = 180;
            parent.Add(add);
        }

        private static VisualElement CreateRuleCard()
        {
            var card = new VisualElement();
            card.style.width = 270;
            card.style.height = 220;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.paddingTop = 10;
            card.style.paddingBottom = 10;
            card.style.marginRight = 9;
            card.style.marginBottom = 9;
            card.style.backgroundColor = CardColor;
            SetRoundedBorder(card, BorderColor);
            return card;
        }

        private static List<string> GetShapeNames(SkinnedMeshRenderer renderer)
        {
            var names = new List<string>();
            if (renderer == null || renderer.sharedMesh == null) return names;
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                names.Add(renderer.sharedMesh.GetBlendShapeName(i));
            return names;
        }

        private static List<string> GetMaterialChoices(Renderer renderer)
        {
            var choices = new List<string>();
            if (renderer == null) return choices;
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
                choices.Add("第 " + (i + 1) + " 格 · " + (materials[i] == null ? "空" : materials[i].name));
            return choices;
        }

        private int AddTargets(PartSlot slot, IEnumerable<GameObject> candidates)
        {
            Undo.RecordObject(this, "分类衣服分件");
            int added = 0;
            int blockedTransfer = 0;
            lastAddBlockedTransfer = false;
            foreach (var target in candidates)
            {
                if (target == null || EditorUtility.IsPersistent(target) || !target.scene.IsValid() ||
                    target == clothingRoot || !target.transform.IsChildOf(clothingRoot.transform) ||
                    slot.Targets.Contains(target))
                    continue;
                // 父子控制冲突始终拦截；跨框转移仅在编辑模式允许。
                if (slots.Any(other => other != slot && other.Targets.Any(existing => existing != null &&
                    existing != target && (existing.transform.IsChildOf(target.transform) ||
                                           target.transform.IsChildOf(existing.transform)))))
                    continue;
                var previousSlots = slots.Where(other => other != slot && other.Targets.Contains(target)).ToArray();
                if (previousSlots.Length > 0 && !editMode)
                {
                    blockedTransfer++;
                    continue;
                }
                foreach (var other in previousSlots)
                    other.Targets.RemoveAll(existing => existing == target);
                slot.Targets.Add(target);
                added++;
            }
            if (blockedTransfer > 0)
            {
                lastAddBlockedTransfer = true;
                ShowNotification(new GUIContent("物体已在其他分类中。开启“编辑分类”后，可拖入新卡片转移。"));
            }
            if (added > 0) SyncPreviewObjects();
            return added;
        }

        private void RemoveSelectedTargets(PartSlot slot)
        {
            Undo.RecordObject(this, "移除分件分类");
            slot.Targets.RemoveAll(target => selectedTargets.Contains(target));
            selectedTargets.Clear();
            SyncPreviewObjects();
            RefreshAll();
        }

        private static bool IsUsed(PartSlot slot)
        {
            return slot.Targets.Any(target => target != null) || slot.UseBlendshapes || slot.UseMaterials || !string.IsNullOrEmpty(slot.ExternalReadOnlyReason);
        }

        private bool ValidateAdvanced(PartSlot slot, VRCAvatarDescriptor avatar)
        {
            if (slot.UseBlendshapes)
            {
                if (slot.Blendshapes.Count == 0)
                {
                    Notify("形态键未填写", "「" + slot.Category + "」已启用形态键，但没有设置内容。", "确定");
                    return false;
                }
                foreach (var rule in slot.Blendshapes)
                {
                    if (rule.Renderer == null || !rule.Renderer.transform.IsChildOf(avatar.transform) ||
                        rule.Renderer.sharedMesh == null || string.IsNullOrEmpty(rule.ShapeName) ||
                        !GetShapeNames(rule.Renderer).Contains(rule.ShapeName))
                    {
                        Notify("形态键设置不完整", "请检查「" + slot.Category + "」的目标网格与形态键；目标必须在此 Avatar 下。", "确定");
                        return false;
                    }
                    if (rule.Value < 0 || rule.Value > 100)
                    {
                        Notify("形态键数值错误", "「" + slot.Category + "」的形态键值需要在 0 到 100 之间。", "确定");
                        return false;
                    }
                }
            }
            if (slot.UseMaterials)
            {
                if (slot.Materials.Count == 0)
                {
                    Notify("材质替换未填写", "「" + slot.Category + "」已启用材质替换，但没有设置内容。", "确定");
                    return false;
                }
                foreach (var rule in slot.Materials)
                {
                    if (rule.Renderer == null || !rule.Renderer.transform.IsChildOf(avatar.transform) ||
                        rule.MaterialIndex < 0 || rule.MaterialIndex >= rule.Renderer.sharedMaterials.Length ||
                        rule.Replacement == null)
                    {
                        Notify("材质替换设置不完整", "请检查「" + slot.Category + "」的目标网格、材质槽与替换材质；目标必须在此 Avatar 下。", "确定");
                        return false;
                    }
                }
            }
            return true;
        }

        private static List<GameObject> FindMarkedMenus(GameObject outfit)
        {
            var avatar = outfit == null ? null : outfit.GetComponentInParent<VRCAvatarDescriptor>();
            var found = new List<GameObject>();
            if (avatar == null) return found;
            foreach (var marker in avatar.GetComponentsInChildren<WardrobeGeneratedMenu>(true))
            {
                if (marker != null && marker.ClothingRoot == outfit)
                    found.Add(marker.gameObject);
            }
            return found;
        }

        private static GameObject FindMenuCandidate(GameObject outfit)
        {
            var marked = FindMarkedMenus(outfit);
            if (marked.Count == 1) return marked[0];
            if (marked.Count > 1) return null;
            var avatar = outfit == null ? null : outfit.GetComponentInParent<VRCAvatarDescriptor>();
            return avatar == null ? null : avatar.transform.Find(GeneratedPrefix + outfit.name)?.gameObject;
        }

        internal static bool TryFindPartMenu(GameObject outfit, out GameObject menu, out string reason)
        {
            menu = null;
            reason = null;
            var marked = FindMarkedMenus(outfit);
            if (marked.Count > 1)
            {
                reason = "同一套衣服有多个分件菜单关联";
                return false;
            }
            menu = FindMenuCandidate(outfit);
            if (menu == null) return true;
            if (TryReadGeneratedMenu(menu, outfit, out _, out reason)) return true;
            return false;
        }

        private bool TryRestoreMenu(GameObject outfit, bool notify)
        {
            var marked = FindMarkedMenus(outfit);
            if (marked.Count > 1)
            {
                if (notify) Notify("菜单关联重复", "找到多个声明属于这套衣服的菜单，无法自动选择。", "确定");
                return false;
            }
            var candidate = FindMenuCandidate(outfit);
            if (candidate == null)
            {
                if (notify) Notify("未找到可读取的菜单",
                    "没有找到明确属于这套衣服的衣柜菜单。现有窗口设置未改变。", "确定");
                return false;
            }
            var skipped = new Dictionary<string, string>();
            if (!TryReadMenuCore(candidate, outfit, out var restored, out var reason, skipped))
            {
                if (notify) Notify("无法读取菜单", reason, "确定");
                else ShowNotification(new GUIContent("找到菜单，但结构不符合本工具格式，未自动读取"));
                return false;
            }
            if (loadedMenuRoot == candidate) CopyIconViews(slots, restored);
            slots = restored;
            var mainMenu = candidate.GetComponent<ModularAvatarMenuItem>();
            clothingMenuName = !string.IsNullOrEmpty(mainMenu.label) ? mainMenu.label :
                candidate.name;
            loadedMenuRoot = candidate;
            reserveOutfitSwitch = candidate.GetComponent<WardrobeGeneratedMenu>()?.OutfitSwitchNode != null;
            var batchMarker = candidate.GetComponent<WardrobeGeneratedMenu>();
            // 未配置过此功能的旧菜单也默认开启；已明确关闭并保存的菜单保留关闭设置。
            enableBatchControl = batchMarker?.BatchControlNode != null ||
                string.IsNullOrEmpty(batchMarker?.PartParameterPrefix);
            partPresets = ClonePresets(batchMarker?.PartPresets);
            if (batchMarker?.BatchControlNode != null)
                for (int i = 0; i < partPresets.Count; i++)
                    partPresets[i].Name = ReadMenuName(batchMarker.BatchControlNode.transform.GetChild(i + 2).gameObject);
            selectedIndex = -1;
            selectedTargets.Clear();
            showBackups = slots.Skip(MainCount).Any(IsUsed);
            loadedConfigurationSignature = PreviewSignature();
            loadedNamingSignature = MenuNamingSignature();
            CaptureSceneBaseline();
            if (skipped.Count > 0)
                Notify("部分配置只读保留", string.Join("；", skipped.Values) + " 其他可读取分件已恢复；生成更新仍需先处理这些配置。");
            else if (notify) ShowNotification(new GUIContent("已从 MA 菜单恢复 " + slots.Count(IsUsed) + " 个部件位"));
            return true;
        }

        private static bool TryReadGeneratedMenu(GameObject menuRoot, GameObject outfit,
            out List<PartSlot> restored, out string reason)
        {
            return TryReadMenuCore(menuRoot, outfit, out restored, out reason, null);
        }

        private static bool TryReadMenuCore(GameObject menuRoot, GameObject outfit,
            out List<PartSlot> restored, out string reason, Dictionary<string, string> skipped)
        {
            restored = CreateDefaultSlots();
            reason = "菜单不是此工具可安全读取的格式。";
            if (menuRoot == null || outfit == null)
                return false;
            var avatar = outfit.GetComponentInParent<VRCAvatarDescriptor>();
            if (avatar == null || outfit.transform.parent != avatar.transform) return false;
            var parent = menuRoot.transform.parent;
            var collection = parent == null ? null : parent.GetComponent<WardrobeGeneratedSwitch>();
            bool grouped = collection != null && collection.IsSeparateCollection &&
                           collection.AvatarRoot == avatar.gameObject && parent.parent == avatar.transform &&
                           collection.Entries != null && collection.Entries.Any(entry =>
                               entry != null && entry.OutfitRoot == outfit && entry.PartMenuRoot == menuRoot);
            if (parent != avatar.transform && !grouped) return false;
            if (!menuRoot.activeSelf) return false;
            var submenu = menuRoot.GetComponent<ModularAvatarMenuItem>();
            var installer = menuRoot.GetComponent<ModularAvatarMenuInstaller>();
            if (submenu == null || (grouped ? installer != null : installer == null) ||
                !submenu.enabled || submenu.Control == null ||
                submenu.Control.type != VRCExpressionsMenu.Control.ControlType.SubMenu ||
                submenu.MenuSource != SubmenuSource.Children ||
                submenu.menuSource_otherObjectChildren != null)
                return false;
            if (menuRoot.GetComponents<ModularAvatarMenuItem>().Length != 1 ||
                menuRoot.GetComponents<ModularAvatarMenuInstaller>().Length != (grouped ? 0 : 1) ||
                menuRoot.GetComponents<WardrobeGeneratedMenu>().Length > 1)
                return false;

            var marker = menuRoot.GetComponent<WardrobeGeneratedMenu>();
            if (marker != null && marker.ClothingRoot != outfit)
            {
                reason = "菜单的衣服关联与当前选择不一致。";
                return false;
            }
            if (menuRoot.GetComponents<Component>().Any(component => component == null ||
                (!(component is Transform) && !(component is ModularAvatarMenuItem) &&
                !(component is ModularAvatarMenuInstaller) && !(component is WardrobeGeneratedMenu))))
            {
                reason = "菜单根节点含有本工具不认识的组件，不能安全覆盖。";
                return false;
            }

            var children = menuRoot.transform.Cast<Transform>().Select(child => child.gameObject).ToList();
            if (marker != null)
            {
                // 有关联记录时，只检查本工具拥有的节点；额外的 MA 菜单及引用不算分件。
                if (marker.Slots == null || marker.Slots.Count > TotalCount || marker.Slots.Any(link =>
                        link == null || link.MenuNode == null || link.MenuNode.transform.parent != menuRoot.transform))
                {
                    reason = "分件关联缺失或不在当前菜单下，未覆盖。";
                    return false;
                }
                var owned = new HashSet<GameObject>();
                foreach (var link in marker.Slots)
                    if (!owned.Add(link.MenuNode))
                    { reason = "分件关联重复，未覆盖。"; return false; }
                foreach (var special in new[] { marker.OutfitSwitchNode, marker.BatchControlNode })
                    if (special != null && (special.transform.parent != menuRoot.transform || !owned.Add(special)))
                    { reason = "换衣或批量控制关联无效，未覆盖。"; return false; }
                children = children.Where(owned.Contains).ToList();
            }
            bool hasSwitch = marker != null && marker.OutfitSwitchNode != null;
            if (!hasSwitch && marker != null && !string.IsNullOrEmpty(marker.OutfitSwitchGroupParameter))
                return false;
            if (children.Count == 0 || children.Count > TotalCount + (hasSwitch ? 1 : 0) + (marker?.BatchControlNode != null ? 1 : 0))
                return false;
            if (hasSwitch)
            {
                var switchNode = marker.OutfitSwitchNode;
                var switchItem = switchNode.GetComponent<ModularAvatarMenuItem>();
                var switcher = switchNode.GetComponent<ModularAvatarObjectToggle>();
                var mergedMotion = switchNode.GetComponent<ModularAvatarMergeBlendTree>();
                if (children[0] != switchNode || !switchNode.activeSelf ||
                    switchNode.transform.childCount != 0 || switchItem == null || !switchItem.enabled ||
                    switchItem.Control == null || switchItem.Control.type != VRCExpressionsMenu.Control.ControlType.Toggle ||
                    switchItem.automaticValue || switchItem.Control.value < 0 || switchItem.Control.value > 255 ||
                    !Mathf.Approximately(switchItem.Control.value, Mathf.Round(switchItem.Control.value)) ||
                    switchNode.GetComponents<ModularAvatarMenuItem>().Length != 1 ||
                    switchNode.GetComponents<ModularAvatarObjectToggle>().Length > 1 ||
                    switchNode.GetComponents<ModularAvatarMergeBlendTree>().Length > 1 ||
                    switchNode.GetComponents<Component>().Any(component => component == null ||
                        (!(component is Transform) && !(component is ModularAvatarMenuItem) &&
                         !(component is ModularAvatarObjectToggle) &&
                         !(component is ModularAvatarMergeBlendTree))) ||
                    switcher != null && (switcher.Inverted || !switcher.enabled || switcher.Objects == null ||
                        switcher.Objects.Any(entry => entry.Object?.Get(switcher) == null)) ||
                    mergedMotion != null && (!mergedMotion.enabled || mergedMotion.Motion == null ||
                        mergedMotion.PathMode != MergeAnimatorPathMode.Absolute))
                    return false;
                children.RemoveAt(0);
            }
            if (marker != null && marker.BatchControlNode != null)
            {
                var batch = marker.BatchControlNode;
                if (children.Count == 0 || children[0] != batch || !ValidateBatchNode(marker))
                {
                    reason = "批量控制结构已被手动修改，未覆盖。";
                    return false;
                }
                children.RemoveAt(0);
            }
            if (marker != null && (marker.Slots == null || marker.Slots.Count != children.Count))
            {
                reason = "菜单关联记录与实际子节点不一致。";
                return false;
            }
            var assigned = new HashSet<int>();
            foreach (var node in children)
            {
                var item = node.GetComponent<ModularAvatarMenuItem>();
                int index = marker == null ? LegacySlotIndex(node, item) : SlotIndexFromMarker(marker, node);
                if (index < 0 || !assigned.Add(index))
                {
                    reason = "无法确定某个旧菜单项对应哪个分件框；未猜测分类。";
                    return false;
                }
                var slot = restored[index];
                if (TryReadPartNode(node, outfit, marker, slot, out var partReason)) continue;
                if (skipped == null) { reason = partReason; return false; }
                // 只读占位，不将部分解析结果当成可编辑的完整设置。
                slot = CreateDefaultSlots()[index];
                slot.MenuName = ReadMenuName(node);
                slot.ExternalReadOnlyReason = node.name + "：" + partReason;
                restored[index] = slot;
                skipped[slot.Key] = slot.ExternalReadOnlyReason;
            }
            return true;
        }

        private static bool TryReadPartNode(GameObject node, GameObject outfit, WardrobeGeneratedMenu marker,
            PartSlot slot, out string reason)
        {
                reason = node.name + " 的菜单或控制组件不符合可编辑格式；原组件保留。";
                var item = node.GetComponent<ModularAvatarMenuItem>();
                if (!node.activeSelf || item == null || !item.enabled || item.Control == null ||
                    item.Control.type != VRCExpressionsMenu.Control.ControlType.Toggle || item.isDefault)
                    return false;
                if (node.GetComponents<ModularAvatarMenuItem>().Length != 1) return false;
                if (node.transform.childCount != 0)
                {
                    reason = "分件菜单节点含有额外子物体，不能安全覆盖。";
                    return false;
                }
                if (marker?.BatchControlNode != null &&
                    item.Control.parameter?.name != marker.PartParameterPrefix + slot.Key)
                {
                    reason = "分件参数与批量控制关联不一致，未覆盖。";
                    return false;
                }
                slot.MenuName = !string.IsNullOrEmpty(item.label) ? item.label :
                    node.name;
                slot.Icon = item.Control.icon;
                slot.IconConfigured = true;
                var iconLink = marker?.Slots.FirstOrDefault(link => link.MenuNode == node);
                slot.IconTransparentCompatibility = iconLink?.IconTransparentCompatibility ?? true;
                if (iconLink != null && iconLink.IconViewVersion > 0)
                { slot.IconYaw = iconLink.IconYaw; slot.IconPitch = iconLink.IconPitch; slot.IconPadding = iconLink.IconPadding; slot.IconViewVersion = 3; slot.IconAnglesInitialized = true; }
                slot.IconGenerated = iconLink != null && iconLink.IconSourceVersion > 0
                    ? slot.Icon != null && iconLink.GeneratedIcon == slot.Icon
                    : WardrobePartIconRenderer.IsLegacyGeneratedIcon(slot.Icon);
                slot.IconSourceKnown = true;
                var reactions = node.GetComponents<ReactiveComponent>();
                if (reactions.Length == 0 || reactions.Any(reaction => !reaction.enabled ||
                    !(reaction is ModularAvatarObjectToggle) &&
                    !(reaction is ModularAvatarShapeChanger) &&
                    !(reaction is ModularAvatarMaterialSetter)))
                    return false;
                if (node.GetComponents<ModularAvatarObjectToggle>().Length > 1 ||
                    node.GetComponents<ModularAvatarShapeChanger>().Length > 1 ||
                    node.GetComponents<ModularAvatarMaterialSetter>().Length > 1)
                    return false;
                if (node.GetComponents<Component>().Any(component => component == null ||
                    (!(component is Transform) && !(component is ModularAvatarMenuItem) &&
                    !(component is ModularAvatarObjectToggle) &&
                    !(component is ModularAvatarShapeChanger) &&
                    !(component is ModularAvatarMaterialSetter))))
                {
                    reason = "分件菜单节点含有本工具不认识的组件，不能安全覆盖。";
                    return false;
                }

                var objects = node.GetComponent<ModularAvatarObjectToggle>();
                if (objects != null)
                {
                    if (objects.Inverted || objects.Objects == null || objects.Objects.Count == 0) return false;
                    foreach (var entry in objects.Objects)
                    {
                        var target = entry.Object?.Get(objects);
                        if (entry.Active || target == null || target == outfit ||
                            !target.transform.IsChildOf(outfit.transform)) return false;
                        if (!slot.Targets.Contains(target)) slot.Targets.Add(target);
                    }
                }

                var shapes = node.GetComponent<ModularAvatarShapeChanger>();
                if (shapes != null)
                {
                    if (shapes.Shapes == null || shapes.Shapes.Count == 0)
                    {
                        reason = "形态键组件没有规则，未转换；原组件保留。";
                        return false;
                    }
                    slot.UseBlendshapes = true;
                    slot.BlendshapesInverted = shapes.Inverted;
                    slot.AdvancedExpanded = true;
                    foreach (var entry in shapes.Shapes)
                    {
                        var target = entry?.Object?.Get(shapes);
                        var renderer = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                        if (entry == null || entry.ChangeType != ShapeChangeType.Set || renderer == null)
                        {
                            reason = node.name + "：当前只支持有效的 Set（设置数值）形态键规则，不支持 Delete（删除）；原组件未修改。";
                            return false;
                        }
                        slot.Blendshapes.Add(new BlendshapeRule
                        {
                            Renderer = renderer, ShapeName = entry.ShapeName, Value = entry.Value
                        });
                    }
                }

                var materials = node.GetComponent<ModularAvatarMaterialSetter>();
                if (materials != null)
                {
                    if (materials.Objects == null || materials.Objects.Count == 0)
                    { reason = "材质替换组件没有规则，未转换；原组件保留。"; return false; }
                    slot.UseMaterials = true;
                    slot.MaterialsInverted = materials.Inverted;
                    slot.AdvancedExpanded = true;
                    foreach (var entry in materials.Objects)
                    {
                        var target = entry?.Object?.Get(materials);
                        var renderer = target == null ? null : target.GetComponent<Renderer>();
                        if (entry == null || renderer == null || entry.Material == null)
                            return false;
                        slot.Materials.Add(new MaterialRule
                        {
                            Renderer = renderer, MaterialIndex = entry.MaterialIndex,
                            Replacement = entry.Material
                        });
                    }
                }
            return true;
        }

        private static int SlotIndexFromMarker(WardrobeGeneratedMenu marker, GameObject node)
        {
            var links = marker.Slots.Where(link => link != null && link.MenuNode == node).ToList();
            if (links.Count != 1) return -1;
            var templates = CreateDefaultSlots();
            return templates.FindIndex(slot => slot.Key == links[0].Key);
        }

        private static int LegacySlotIndex(GameObject node, ModularAvatarMenuItem item)
        {
            var templates = CreateDefaultSlots();
            var parameter = item.Control.parameter?.name ?? "";
            for (int i = 0; i < templates.Count; i++)
                if (parameter.EndsWith("_" + templates[i].Key, StringComparison.Ordinal)) return i;
            for (int i = 0; i < templates.Count; i++)
            {
                var slot = templates[i];
                if (node.name == DefaultMenuName(slot.Category, slot.Backup) || node.name == "隐藏" + DefaultMenuName(slot.Category, slot.Backup)) return i;
                if (node.name == "关闭" + slot.Category + (slot.Backup ? "备用" : "")) return i;
                if (node.name == slot.Category + (slot.Backup ? " 备用" : "")) return i;
            }
            return -1;
        }

        private void SetClothingRoot(GameObject candidate)
        {
            if (candidate == null || EditorUtility.IsPersistent(candidate) || !candidate.scene.IsValid())
            {
                Notify("无法选择", "请拖入 层级面板中的衣服根物体，不要拖入 项目面板中的预制体。", "确定");
                return;
            }
            var avatar = candidate.GetComponentInParent<VRCAvatarDescriptor>();
            if (avatar == null || candidate.transform.parent != avatar.transform)
            {
                Notify("层级不符合", "衣服根对象需要是 角色下面的一级物体（直接放在 Avatar 下）。", "确定");
                return;
            }
            if (candidate == clothingRoot) return;
            SaveDraft();
            ClearPreview();
            clothingRoot = candidate;
            clothingMenuName = "";
            ResetSlots();
            reserveOutfitSwitch = true;
            enableBatchControl = true;
            partPresets = new List<WardrobePartPreset>();
            loadedMenuRoot = null;
            loadedConfigurationSignature = null;
            loadedNamingSignature = null;
            loadedSceneSnapshot = null;
            loadedSceneSignature = null;
            selectedIndex = -1;
            selectedTargets.Clear();
            var draft = drafts.FirstOrDefault(entry => entry.Root == candidate);
            if (draft != null && (draft.Unsaved || string.IsNullOrEmpty(draft.ConfigurationSignature) || !TryRestoreMenu(candidate, false)))
            {
                slots = JsonUtility.FromJson<ClothingDraft>(JsonUtility.ToJson(draft)).Slots;
                clothingMenuName = draft.MenuName;
                reserveOutfitSwitch = draft.ReserveSwitch;
                enableBatchControl = draft.BatchControl;
                partPresets = ClonePresets(draft.Presets);
                loadedMenuRoot = FindMenuCandidate(candidate);
                loadedConfigurationSignature = draft.ConfigurationSignature;
                loadedNamingSignature = draft.NamingSignature;
                loadedSceneSnapshot = CopyConfiguration(draft.SceneSnapshot);
                loadedSceneSignature = draft.SceneSignature;
                EnsureSlots();
            }
            else if (draft == null) TryRestoreMenu(candidate, false);
            if (draft != null) CopyIconViews(draft.Slots, slots);
            BuildUI();
        }

        private static void CopyIconViews(List<PartSlot> source, List<PartSlot> destination)
        {
            if (source == null || destination == null) return;
            foreach (var target in destination)
            {
                var previous = source.FirstOrDefault(s => s.Key == target.Key);
                if (previous == null || !previous.IconAnglesInitialized) continue;
                target.IconYaw = previous.IconYaw; target.IconPitch = previous.IconPitch;
                target.IconPadding = previous.IconPadding; target.IconAnglesInitialized = true;
                target.IconViewVersion = previous.IconViewVersion;
            }
        }

        private void SaveDraft()
        {
            if (clothingRoot == null) return;
            if (drafts == null) drafts = new List<ClothingDraft>();
            drafts.RemoveAll(entry => entry.Root == null || entry.Root == clothingRoot);
            var draft = new ClothingDraft { Root = clothingRoot, MenuName = clothingMenuName,
                ReserveSwitch = reserveOutfitSwitch, Slots = slots,
                BatchControl = enableBatchControl, Presets = partPresets,
                Unsaved = HasUnsavedConfiguration(), ConfigurationSignature = loadedConfigurationSignature,
                NamingSignature = loadedNamingSignature, SceneSnapshot = loadedSceneSnapshot, SceneSignature = loadedSceneSignature };
            drafts.Add(JsonUtility.FromJson<ClothingDraft>(JsonUtility.ToJson(draft)));
        }

        [Serializable]
        private class ConfigurationState
        {
            public GameObject Root;
            public string MenuName;
            public bool ReserveSwitch;
            public List<PartSlot> Slots;
            public bool BatchControl;
            public List<WardrobePartPreset> Presets;
        }

        [Serializable]
        private sealed class ClothingDraft : ConfigurationState
        {
            public bool Unsaved;
            public string ConfigurationSignature;
            public string NamingSignature;
            public ConfigurationState SceneSnapshot;
            public string SceneSignature;
        }

        // 预览仅修改 Unity 场景状态。原值先缓存，完成、保存或退出时恢复，
        // 不写入 MA 的 Inverted，也不把临时状态放入 Undo 或生成菜单的默认状态。
        private static void RefreshScenePreview()
        {
            // 直接 SetActive / 修改渲染器不会走 Undo 的属性通知，MA 的分析缓存
            // 可能仍按修改前的状态绘制代理模型。显式失效缓存，不改组件或预览开关。
            PropCacheDebug.InvalidateAllCaches();
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        private void RestorePreviewStates(bool refresh = true)
        {
            bool hadPreview = previewObjectStates.Count > 0 || previewShapeStates.Count > 0 || previewMaterialStates.Count > 0;
            foreach (var entry in previewObjectStates)
                if (entry.Key != null) entry.Key.SetActive(entry.Value);
            foreach (var entry in previewShapeStates)
                if (entry.Key != null && entry.Key.sharedMesh != null)
                    foreach (var shape in entry.Value)
                        if (shape.Key < entry.Key.sharedMesh.blendShapeCount)
                            entry.Key.SetBlendShapeWeight(shape.Key, shape.Value);
            foreach (var entry in previewMaterialStates)
                if (entry.Key != null) entry.Key.sharedMaterials = entry.Value;
            previewObjectStates.Clear();
            previewShapeStates.Clear();
            previewMaterialStates.Clear();
            if (refresh && hadPreview) RefreshScenePreview();
        }

        // 分类编辑只增量处理受影响对象，不清空勾选，也不让其他隐藏对象闪现。
        private void SyncPreviewObjects(bool refresh = true)
        {
            bool changed = false;
            previewedSlots.RemoveWhere(index => index < 0 || index >= slots.Count || !IsUsed(slots[index]));
            var hiddenTargets = new HashSet<GameObject>();
            if (clothingRoot != null)
                foreach (int index in previewedSlots)
                    foreach (var target in slots[index].Targets)
                        if (target != null && target != clothingRoot && !EditorUtility.IsPersistent(target) &&
                            target.transform.IsChildOf(clothingRoot.transform)) hiddenTargets.Add(target);

            foreach (var target in previewObjectStates.Keys.ToArray())
            {
                if (target != null && hiddenTargets.Contains(target)) continue;
                if (target != null && target.activeSelf != previewObjectStates[target])
                {
                    target.SetActive(previewObjectStates[target]);
                    changed = true;
                }
                previewObjectStates.Remove(target);
            }
            foreach (var target in hiddenTargets)
            {
                if (!previewObjectStates.ContainsKey(target)) previewObjectStates.Add(target, target.activeSelf);
                if (target.activeSelf)
                {
                    target.SetActive(false);
                    changed = true;
                }
            }
            if (refresh && changed) RefreshScenePreview();
        }

        private void ApplyScenePreview()
        {
            // 同一操作完成全部临时修改后再刷新，避免重建中间状态的预览。
            RestorePreviewStates(false);
            SyncPreviewObjects(false);
            if (clothingRoot == null) { RefreshScenePreview(); return; }
            var avatar = clothingRoot.GetComponentInParent<VRCAvatarDescriptor>();
            foreach (int index in previewedSlots.OrderBy(value => value))
            {
                if (index < 0 || index >= slots.Count) continue;
                var slot = slots[index];
                // 高级预览也直接使用 Unity 原值，仍保留形态键和材质预览能力。
                if (slot.UseBlendshapes && !slot.BlendshapesInverted && avatar != null)
                    foreach (var rule in slot.Blendshapes)
                    {
                        var renderer = rule.Renderer;
                        if (renderer == null || renderer.sharedMesh == null ||
                            !renderer.transform.IsChildOf(avatar.transform)) continue;
                        int shape = renderer.sharedMesh.GetBlendShapeIndex(rule.ShapeName ?? "");
                        if (shape < 0) continue;
                        if (!previewShapeStates.TryGetValue(renderer, out var original))
                            previewShapeStates.Add(renderer, original = new Dictionary<int, float>());
                        if (!original.ContainsKey(shape)) original.Add(shape, renderer.GetBlendShapeWeight(shape));
                        renderer.SetBlendShapeWeight(shape, rule.Value);
                    }
                if (slot.UseMaterials && !slot.MaterialsInverted && avatar != null)
                    foreach (var rule in slot.Materials)
                    {
                        var renderer = rule.Renderer;
                        if (renderer == null || !renderer.transform.IsChildOf(avatar.transform) || rule.Replacement == null) continue;
                        var materials = renderer.sharedMaterials;
                        if (rule.MaterialIndex < 0 || rule.MaterialIndex >= materials.Length) continue;
                        if (!previewMaterialStates.ContainsKey(renderer)) previewMaterialStates.Add(renderer, (Material[])materials.Clone());
                        materials[rule.MaterialIndex] = rule.Replacement;
                        renderer.sharedMaterials = materials;
                    }
            }
            RefreshScenePreview();
        }

        private void ClearPreview()
        {
            RestorePreviewStates();
            previewedSlots?.Clear();
            if (mainGrid != null) RefreshAll();
        }

        private void OnUndoRedo() { ClearPreview(); RefreshAll(); }

        private void OnSceneSaving(Scene scene, string path)
        {
            if (clothingRoot != null && clothingRoot.scene == scene) ClearPreview();
        }

        private void OnSceneClosing(Scene scene, bool removingScene)
        {
            if (clothingRoot != null && clothingRoot.scene == scene) ClearPreview();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) ClearPreview();
        }

        private string PreviewSignature()
        {
            return ConfigurationSignature(CurrentConfiguration());
        }

        private static string ConfigurationSignature(ConfigurationState data)
        {
            var text = new StringBuilder();
            text.Append(data.Root == null ? 0 : data.Root.GetInstanceID()).Append('|')
                .Append(data.MenuName).Append('|').Append(data.ReserveSwitch).Append('|').Append(data.BatchControl);
            foreach (var preset in data.Presets)
                text.Append('|').Append(preset.Name).Append(':').Append(string.Join(",", preset.VisibleKeys));
            foreach (var slot in data.Slots)
            {
                text.Append(slot.Key).Append(':').Append(slot.MenuName).Append(':');
                text.Append(slot.Icon == null ? 0 : slot.Icon.GetInstanceID()).Append(':').Append(slot.IconGenerated).Append(':');
                text.Append(slot.IconTransparentCompatibility).Append(':');
                foreach (var target in slot.Targets)
                    text.Append(target == null ? 0 : target.GetInstanceID()).Append(',');
                text.Append(':').Append(slot.UseBlendshapes).Append(':').Append(slot.BlendshapesInverted).Append(':');
                foreach (var rule in slot.Blendshapes)
                    text.Append(rule.Renderer == null ? 0 : rule.Renderer.GetInstanceID())
                        .Append(',').Append(rule.ShapeName).Append(',').Append(rule.Value).Append(';');
                text.Append(':').Append(slot.UseMaterials).Append(':').Append(slot.MaterialsInverted).Append(':');
                foreach (var rule in slot.Materials)
                    text.Append(rule.Renderer == null ? 0 : rule.Renderer.GetInstanceID())
                        .Append(',').Append(rule.MaterialIndex).Append(',')
                        .Append(rule.Replacement == null ? 0 : rule.Replacement.GetInstanceID()).Append(';');
                text.Append('|');
            }
            return text.ToString();
        }

        private void TogglePreview(int index)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || clothingRoot == null ||
                index < 0 || index >= slots.Count) return;
            if (previewedSlots.Contains(index))
            {
                previewedSlots.Remove(index);
            }
            else
            {
                if (!IsUsed(slots[index])) return;
                previewedSlots.Add(index);
            }
            ApplyScenePreview();
            RefreshAll();
        }

        private void CloseAllPreview()
        {
            if (clothingRoot == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            foreach (int index in Enumerable.Range(0, slots.Count).Where(index => IsUsed(slots[index])))
                previewedSlots.Add(index);
            ApplyScenePreview();
            RefreshAll();
        }

        private void OpenAllPreview()
        {
            if (clothingRoot == null) return;
            ClearPreview();
        }

        private void Generate()
        {
            GenerateCore(false);
        }

        private bool GenerateCore(bool previewConfirmed, bool suppressSuccess = false)
        {
            WardrobeDiagnostics.Record("分件", "生成或更新", "开始");
            ClearPreview();
            if (clothingRoot == null || EditorUtility.IsPersistent(clothingRoot) || !clothingRoot.scene.IsValid())
            {
                Notify("无法生成", "请先拖入场景层级面板里的衣服根物体。", "确定");
                return false;
            }
            var avatar = clothingRoot.GetComponentInParent<VRCAvatarDescriptor>();
            if (avatar == null || clothingRoot.transform.parent != avatar.transform)
            {
                Notify("无法生成", "衣服根对象必须是 角色下面的一级物体（直接放在 Avatar 下）。", "确定");
                return false;
            }

            // 在校验 used 列表之前同步，避免先缓存旧分件再合并配置。
            if (loadedMenuRoot != null && SceneMenuSignature() != loadedSceneSignature &&
                !SynchronizeSceneConfiguration()) return false;

            clothingMenuName = clothingMenuName == null ? "" : clothingMenuName.Trim();
            if (string.IsNullOrEmpty(clothingMenuName))
            {
                clothingMenuName = "-" + clothingRoot.name;
            }

            var used = slots.Where(IsUsed).ToList();
            if (used.Count == 0 && !reserveOutfitSwitch) return false;
            if (enableBatchControl)
            {
                var presetNames = new HashSet<string> { "全部显示", "全部隐藏", "显示全部分件", "隐藏全部分件" };
                if (partPresets.Count > 253 || partPresets.Any(p => p == null ||
                    string.IsNullOrWhiteSpace(p.Name) || !presetNames.Add(p.Name.Trim())))
                {
                    Notify("模板名称有问题", "模板名称不能留空或重复，最多支持 253 个模板。");
                    return false;
                }
                foreach (var preset in partPresets) preset.Name = preset.Name.Trim();
            }
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var allTargets = new Dictionary<GameObject, PartSlot>();
            foreach (var slot in used)
            {
                slot.MenuName = slot.MenuName == null ? "" : slot.MenuName.Trim();
                if (string.IsNullOrEmpty(slot.MenuName) || !seenNames.Add(slot.MenuName))
                {
                    Notify("菜单名称有问题", "已使用的部件位不能空名或重名：" + slot.MenuName, "确定");
                    return false;
                }
                foreach (var target in slot.Targets.Where(target => target != null))
                {
                    if (EditorUtility.IsPersistent(target) || !target.scene.IsValid() || target.scene != clothingRoot.scene ||
                        target == clothingRoot || !target.transform.IsChildOf(clothingRoot.transform))
                    {
                        Notify("物体不属于这套衣服", target.name + " 不在当前衣服根对象下面。", "确定");
                        return false;
                    }
                    if (allTargets.TryGetValue(target, out var previous))
                    {
                        if (previous == slot) continue;
                        Notify("重复分配", target.name + " 同时属于「" + previous.MenuName + "」和「" + slot.MenuName + "」。", "确定");
                        return false;
                    }
                    allTargets.Add(target, slot);
                }
                if (!ValidateAdvanced(slot, avatar)) return false;
            }
            var targets = allTargets.Keys.ToArray();
            for (int i = 0; i < targets.Length; i++)
                for (int j = i + 1; j < targets.Length; j++)
                    if (allTargets[targets[i]] != allTargets[targets[j]] &&
                        (targets[i].transform.IsChildOf(targets[j].transform) ||
                         targets[j].transform.IsChildOf(targets[i].transform)))
                    {
                        Notify("父子物体冲突",
                            targets[i].name + " 与 " + targets[j].name + " 是父子层级，不能独立作为两个开关目标。", "确定");
                        return false;
                    }

            if (FindMarkedMenus(clothingRoot).Count > 1)
            {
                Notify("菜单关联重复", "找到多个声明属于这套衣服的菜单，请先在 层级面板中确认，工具不会猜测要更新哪一个。", "确定");
                return false;
            }
            var existingMenu = FindMenuCandidate(clothingRoot);
            var installTargetLinks = existingMenu == null
                ? new List<(WardrobeGeneratedSwitch Group, WardrobeSwitchLink Link)>()
                : avatar.transform.Cast<Transform>()
                    .Select(child => child.GetComponent<WardrobeGeneratedSwitch>())
                    .Where(group => group != null && group.IsSeparateCollection && group.UsesInstallTargets &&
                                    group.Entries != null)
                    .SelectMany(group => group.Entries.Where(link => link != null &&
                        link.PartMenuRoot == existingMenu).Select(link => (Group: group, Link: link)))
                    .ToList();
            if (installTargetLinks.Count > 1 || installTargetLinks.Count == 1 &&
                !IsInstallTargetFor(installTargetLinks[0].Link.PartsNode,
                    existingMenu.GetComponent<ModularAvatarMenuInstaller>()))
            {
                Notify("总菜单引用已损坏", "现有分件菜单被总菜单引用，但关联不完整；为避免破坏换衣菜单，本次不自动更新。", "确定");
                return false;
            }
            var menuParent = existingMenu == null ? avatar.transform : existingMenu.transform.parent;
            var nameCollision = menuParent.Cast<Transform>().FirstOrDefault(child =>
                child.name == clothingMenuName && child.gameObject != existingMenu);
            if (nameCollision != null)
            {
                clothingMenuName = GameObjectUtility.GetUniqueNameForSibling(menuParent, clothingMenuName);
            }
            if (existingMenu != null)
            {
                if (PrefabUtility.IsPartOfPrefabInstance(existingMenu) &&
                    !PrefabUtility.IsAddedGameObjectOverride(existingMenu))
                {
                    Notify("不更新 Prefab 菜单", "已有菜单是 预制体原有对象，自动替换可能影响 Prefab 结构，请先手动处理。", "确定");
                    return false;
                }
                if (!TryReadGeneratedMenu(existingMenu, clothingRoot, out _, out var readReason))
                {
                    Notify("无法安全更新", readReason + " 未覆盖现有组件，请检查后重新读取。", "确定");
                    return false;
                }
                var previousSwitchMarker = existingMenu.GetComponent<WardrobeGeneratedMenu>();
                if (!reserveOutfitSwitch && previousSwitchMarker != null &&
                    !string.IsNullOrEmpty(previousSwitchMarker.OutfitSwitchGroupParameter))
                {
                    Notify("换衣选项仍在使用",
                        "这套衣服仍属于互斥换衣组。请先用换衣辅助工具移出该衣服，再关闭预留选项。", "确定");
                    return false;
                }
            }

            var previousSubmenu = existingMenu == null ? null : existingMenu.GetComponent<ModularAvatarMenuItem>();
            var previousInstaller = existingMenu == null ? null : existingMenu.GetComponent<ModularAvatarMenuInstaller>();
            var collection = menuParent == avatar.transform ? null :
                menuParent.GetComponent<WardrobeGeneratedSwitch>();
            var previousMarkerForSwitch = existingMenu == null ? null : existingMenu.GetComponent<WardrobeGeneratedMenu>();
            var previousSwitchNode = previousMarkerForSwitch?.OutfitSwitchNode;
            var previousSwitchItem = previousSwitchNode == null ? null :
                previousSwitchNode.GetComponent<ModularAvatarMenuItem>();
            var previousSwitchToggle = previousSwitchNode == null ? null :
                previousSwitchNode.GetComponent<ModularAvatarObjectToggle>();
            var previousSwitchMotion = previousSwitchNode == null ? null :
                previousSwitchNode.GetComponent<ModularAvatarMergeBlendTree>();
            var previousItems = new Dictionary<string, ModularAvatarMenuItem>();
            if (existingMenu != null)
            {
                var previousMarker = existingMenu.GetComponent<WardrobeGeneratedMenu>();
                var templates = CreateDefaultSlots();
                foreach (Transform child in existingMenu.transform)
                {
                    var item = child.GetComponent<ModularAvatarMenuItem>();
                    int index = previousMarker == null ? LegacySlotIndex(child.gameObject, item) :
                        SlotIndexFromMarker(previousMarker, child.gameObject);
                    if (index >= 0) previousItems[templates[index].Key] = item;
                }
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("生成 MA 衣柜分件菜单");
            try
            {
                var root = CreateChild(menuParent,
                    existingMenu == null ? clothingMenuName : clothingMenuName + "_更新中");
                Undo.RecordObject(root.transform, "排列衣柜菜单");
                root.transform.SetSiblingIndex(existingMenu == null ?
                    clothingRoot.transform.GetSiblingIndex() : existingMenu.transform.GetSiblingIndex());
                var marker = Undo.AddComponent<WardrobeGeneratedMenu>(root);
                Undo.RecordObject(marker, "记录衣柜菜单关联");
                marker.ClothingRoot = clothingRoot;
                marker.PartParameterPrefix = previousMarkerForSwitch != null &&
                    !string.IsNullOrEmpty(previousMarkerForSwitch.PartParameterPrefix)
                    ? previousMarkerForSwitch.PartParameterPrefix : "ZincatParts_" + Guid.NewGuid().ToString("N") + "_";
                marker.PartPresets = ClonePresets(partPresets);
                var submenu = Undo.AddComponent<ModularAvatarMenuItem>(root);
                Undo.RecordObject(submenu, "配置衣柜主菜单");
                submenu.MenuSource = SubmenuSource.Children;
                submenu.Control = new VRCExpressionsMenu.Control
                {
                    name = clothingMenuName,
                    type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                    icon = previousSubmenu == null ? OutfitDraft()?.Icon : previousSubmenu.Control?.icon
                };
                marker.GeneratedOutfitIcon = previousSubmenu == null ? OutfitDraft()?.Generated == true ? submenu.Control.icon : null : previousMarkerForSwitch?.GeneratedOutfitIcon;
                marker.GeneratedWearIcon = previousMarkerForSwitch?.GeneratedWearIcon;
                // label 留空即 MA Inspector 的“使用对象名称”；层级改名会同步到菜单名。
                submenu.label = "";
                if (collection == null)
                {
                    var installer = Undo.AddComponent<ModularAvatarMenuInstaller>(root);
                    if (previousInstaller != null)
                    {
                        Undo.RecordObject(installer, "保留菜单安装位置");
                        installer.installTargetMenu = previousInstaller.installTargetMenu;
                        installer.menuToAppend = previousInstaller.menuToAppend;
                        installer.enabled = previousInstaller.enabled;
                        EditorUtility.SetDirty(installer);
                    }
                }
                EditorUtility.SetDirty(submenu);

                if (reserveOutfitSwitch)
                {
                    var switchNode = CreateChild(root.transform,
                        previousSwitchNode == null ? "换衣" : previousSwitchNode.name);
                    marker.OutfitSwitchNode = switchNode;
                    marker.OutfitSwitchGroupParameter = previousMarkerForSwitch?.OutfitSwitchGroupParameter;
                    var switchItem = Undo.AddComponent<ModularAvatarMenuItem>(switchNode);
                    Undo.RecordObject(switchItem, "预留 Int 换衣开关");
                    switchItem.Control = new VRCExpressionsMenu.Control
                    {
                        name = switchNode.name,
                        type = VRCExpressionsMenu.Control.ControlType.Toggle,
                        parameter = new VRCExpressionsMenu.Control.Parameter
                        { name = previousSwitchItem?.Control?.parameter?.name ?? "" },
                        // MA 根据数值推断参数类型；单独的占位项用 2 明确推断为 Int。
                        value = previousSwitchItem == null ? 2 :
                            string.IsNullOrEmpty(previousMarkerForSwitch?.OutfitSwitchGroupParameter)
                                ? Mathf.Max(2, previousSwitchItem.Control.value)
                                : previousSwitchItem.Control.value,
                        icon = previousSwitchItem?.Control?.icon
                    };
                    if (previousSwitchItem == null && marker.GeneratedOutfitIcon != null)
                    { switchItem.Control.icon = marker.GeneratedOutfitIcon; marker.GeneratedWearIcon = marker.GeneratedOutfitIcon; }
                    switchItem.label = "";
                    switchItem.automaticValue = false;
                    switchItem.isDefault = false;
                    switchItem.isSaved = previousSwitchItem == null || previousSwitchItem.isSaved;
                    switchItem.isSynced = previousSwitchItem == null || previousSwitchItem.isSynced;
                    EditorUtility.SetDirty(switchItem);
                    if (previousSwitchToggle != null)
                    {
                        var switcher = Undo.AddComponent<ModularAvatarObjectToggle>(switchNode);
                        Undo.RecordObject(switcher, "保留已有换衣逻辑");
                        switcher.Inverted = false;
                        foreach (var entry in previousSwitchToggle.Objects)
                            switcher.Objects.Add(new ToggledObject
                            {
                                Object = new AvatarObjectReference(entry.Object.Get(previousSwitchToggle)),
                                Active = entry.Active
                            });
                        EditorUtility.SetDirty(switcher);
                    }
                    if (previousSwitchMotion != null)
                    {
                        var mergedMotion = Undo.AddComponent<ModularAvatarMergeBlendTree>(switchNode);
                        Undo.RecordObject(mergedMotion, "保留已有换衣动画");
                        mergedMotion.Motion = previousSwitchMotion.Motion;
                        mergedMotion.PathMode = previousSwitchMotion.PathMode;
                        EditorUtility.SetDirty(mergedMotion);
                    }
                }

                if (enableBatchControl && used.Count > 0)
                    CreateBatchMenu(root, marker, previousMarkerForSwitch?.BatchControlNode);

                foreach (var slot in used)
                {
                    var node = CreateChild(root.transform, slot.MenuName);
                    var iconRecord = new WardrobeSlotLink { Key = slot.Key, MenuNode = node, IconSourceVersion = 1, IconTransparentCompatibility = slot.IconTransparentCompatibility,
                        IconYaw = slot.IconYaw, IconPitch = slot.IconPitch, IconPadding = slot.IconPadding, IconViewVersion = 3 };
                    marker.Slots.Add(iconRecord);
                    var menu = Undo.AddComponent<ModularAvatarMenuItem>(node);
                    Undo.RecordObject(menu, "配置分件菜单");
                    previousItems.TryGetValue(slot.Key, out var previousItem);
                    menu.Control = new VRCExpressionsMenu.Control
                    {
                        name = slot.MenuName,
                        type = VRCExpressionsMenu.Control.ControlType.Toggle,
                        parameter = new VRCExpressionsMenu.Control.Parameter
                        { name = enableBatchControl ? marker.PartParameterPrefix + slot.Key : "" },
                        value = 1,
                        icon = slot.IconConfigured ? slot.Icon : previousItem?.Control?.icon
                    };
                    var oldIconRecord = previousMarkerForSwitch?.Slots.FirstOrDefault(s => s.Key == slot.Key);
                    bool generatedIcon = slot.IconConfigured ? slot.IconGenerated : oldIconRecord != null && oldIconRecord.IconSourceVersion > 0
                        ? oldIconRecord.GeneratedIcon == menu.Control.icon : WardrobePartIconRenderer.IsLegacyGeneratedIcon(menu.Control.icon);
                    iconRecord.GeneratedIcon = generatedIcon ? menu.Control.icon : null;
                    menu.label = "";
                    menu.automaticValue = false;
                    menu.isDefault = false;
                    menu.isSaved = previousItem == null || previousItem.isSaved;
                    menu.isSynced = previousItem == null || previousItem.isSynced;
                    EditorUtility.SetDirty(menu);

                    // 唯一动作：菜单开时隐藏；菜单关时让 MA 恢复物体原有状态。
                    // 反转条件始终关闭；编辑器预览直接切换 Unity 状态，生成前恢复。
                    if (slot.Targets.Any(target => target != null))
                    {
                        var hide = Undo.AddComponent<ModularAvatarObjectToggle>(node);
                        Undo.RecordObject(hide, "配置分件隐藏对象");
                        hide.Inverted = false;
                        foreach (var target in slot.Targets.Where(target => target != null).Distinct())
                            hide.Objects.Add(new ToggledObject
                            {
                                Object = new AvatarObjectReference(target),
                                Active = false
                            });
                        EditorUtility.SetDirty(hide);
                    }
                    if (slot.UseBlendshapes)
                    {
                        var shapes = Undo.AddComponent<ModularAvatarShapeChanger>(node);
                        Undo.RecordObject(shapes, "配置形态键");
                        shapes.Inverted = slot.BlendshapesInverted;
                        foreach (var rule in slot.Blendshapes)
                            shapes.Shapes.Add(new ChangedShape
                            {
                                Object = new AvatarObjectReference(rule.Renderer.gameObject),
                                ShapeName = rule.ShapeName,
                                ChangeType = ShapeChangeType.Set,
                                Value = rule.Value
                            });
                        EditorUtility.SetDirty(shapes);
                    }
                    if (slot.UseMaterials)
                    {
                        var materials = Undo.AddComponent<ModularAvatarMaterialSetter>(node);
                        Undo.RecordObject(materials, "配置材质替换");
                        materials.Inverted = slot.MaterialsInverted;
                        foreach (var rule in slot.Materials)
                            materials.Objects.Add(new MaterialSwitchObject
                            {
                                Object = new AvatarObjectReference(rule.Renderer.gameObject),
                                MaterialIndex = rule.MaterialIndex,
                                Material = rule.Replacement
                            });
                        EditorUtility.SetDirty(materials);
                    }
                }

                EditorUtility.SetDirty(marker);
                if (existingMenu != null)
                {
                    foreach (var group in installTargetLinks.Select(link => link.Group)
                                 .Concat(collection == null ? Array.Empty<WardrobeGeneratedSwitch>() : new[] { collection }).Distinct())
                        Undo.RegisterCompleteObjectUndo(group, "保留换衣菜单关联");
                    root = CommitMenuUpdate(existingMenu, root);
                    marker = root.GetComponent<WardrobeGeneratedMenu>();
                    if (installTargetLinks.Count == 1)
                    {
                        var linked = installTargetLinks[0];
                        var newInstaller = root.GetComponent<ModularAvatarMenuInstaller>();
                        if (!TryRetargetInstallTarget(linked.Link.PartsNode, newInstaller))
                            throw new InvalidOperationException("无法更新总菜单对分件菜单的引用。");
                        Undo.RecordObject(linked.Group, "更新总菜单对分件菜单的关联");
                        linked.Link.PartMenuRoot = root;
                        linked.Link.WearNode = marker.OutfitSwitchNode;
                        linked.Link.PartInstallerWasEnabled = newInstaller.enabled;
                        linked.Link.PartInstallTargetMenu = newInstaller.installTargetMenu;
                        linked.Link.PartMenuToAppend = newInstaller.menuToAppend;
                        EditorUtility.SetDirty(linked.Group);
                    }
                    if (collection != null)
                    {
                        Undo.RecordObject(collection, "更新总菜单里的衣服关联");
                        foreach (var entry in collection.Entries.Where(entry =>
                                     entry != null && entry.PartMenuRoot == existingMenu))
                        {
                            entry.PartMenuRoot = root;
                            entry.WearNode = marker.OutfitSwitchNode;
                        }
                        EditorUtility.SetDirty(collection);
                    }
                    Undo.RecordObject(root, "更新衣柜菜单名称");
                    root.name = clothingMenuName;
                }

                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                loadedMenuRoot = root;
                foreach (var link in root.GetComponent<WardrobeGeneratedMenu>().Slots)
                {
                    var saved = slots.First(s => s.Key == link.Key);
                    saved.Icon = link.MenuNode.GetComponent<ModularAvatarMenuItem>().Control.icon;
                    saved.IconConfigured = true;
                    saved.IconGenerated = saved.Icon != null && link.GeneratedIcon == saved.Icon;
                    saved.IconSourceKnown = true;
                }
                loadedConfigurationSignature = PreviewSignature();
                loadedNamingSignature = MenuNamingSignature();
                CaptureSceneBaseline();
                clothingMenuNameField?.SetValueWithoutNotify(clothingMenuName);
                Selection.activeGameObject = root;
                EditorGUIUtility.PingObject(root);
                Undo.CollapseUndoOperations(undoGroup);
                RefreshAll();
                WardrobeDiagnostics.Record("分件", "生成或更新", "成功");
                if (!previewConfirmed && !suppressSuccess)
                    Notify("完成", "已生成 " + used.Count + " 个 MA 分件菜单项，跳过 " +
                        (TotalCount - used.Count) + " 个空位。原衣服层级和动画未修改。", "确定");
                return true;
            }
            catch (Exception error)
            {
                WardrobeDiagnostics.Record("分件", "生成异常", error.ToString());
                Undo.RevertAllDownToGroup(undoGroup);
                Debug.LogException(error);
                Notify("生成失败", error is InvalidOperationException
                    ? "已撤销本次更新。" + error.Message
                    : "已撤销本次部分生成；请查看控制台（Console）。", "确定");
                return false;
            }
        }

        private static Dictionary<string, GameObject> OwnedMenuNodes(GameObject root)
        {
            var result = new Dictionary<string, GameObject>();
            var marker = root.GetComponent<WardrobeGeneratedMenu>();
            if (marker != null)
            {
                foreach (var slot in marker.Slots) result.Add("part:" + slot.Key, slot.MenuNode);
                if (marker.OutfitSwitchNode != null) result.Add("switch", marker.OutfitSwitchNode);
                if (marker.BatchControlNode != null) result.Add("batch", marker.BatchControlNode);
            }
            else
            {
                // 无标记的旧菜单仍走原来的严格读取，成功后所有子项都是已确认的分件。
                foreach (Transform child in root.transform)
                {
                    int index = LegacySlotIndex(child.gameObject, child.GetComponent<ModularAvatarMenuItem>());
                    result.Add("part:" + CreateDefaultSlots()[index].Key, child.gameObject);
                }
            }
            return result;
        }

        private static void MapUpdatedNodes(GameObject oldNode, GameObject newNode,
            Dictionary<UnityEngine.Object, UnityEngine.Object> replacements)
        {
            replacements[oldNode] = newNode;
            foreach (var component in oldNode.GetComponents<Component>())
                if (component != null && newNode.GetComponent(component.GetType()) is Component replacement)
                    replacements[component] = replacement;
            // 批量控制的固定按钮按数值匹配，模板按名字匹配，避免删除模板后错指到另一个模板。
            foreach (Transform oldChild in oldNode.transform)
            {
                var oldItem = oldChild.GetComponent<ModularAvatarMenuItem>();
                var matches = newNode.transform.Cast<Transform>().Where(child =>
                {
                    var item = child.GetComponent<ModularAvatarMenuItem>();
                    return oldItem != null && item != null &&
                        (oldItem.Control.value <= 2 ? item.Control.value == oldItem.Control.value :
                            ReadMenuName(child.gameObject) == ReadMenuName(oldChild.gameObject));
                }).ToArray();
                if (matches.Length == 1) MapUpdatedNodes(oldChild.gameObject, matches[0].gameObject, replacements);
            }
        }

        private static GameObject CommitMenuUpdate(GameObject existing, GameObject staged)
        {
            var oldOwned = OwnedMenuNodes(existing);
            var newOwned = OwnedMenuNodes(staged);
            var originalOrder = existing.transform.Cast<Transform>().Select(t => t.gameObject).ToArray();
            var oldSet = new HashSet<GameObject>(oldOwned.Values);
            var extras = originalOrder.Where(node => !oldSet.Contains(node)).ToArray();
            var marker = existing.GetComponent<WardrobeGeneratedMenu>() ?? Undo.AddComponent<WardrobeGeneratedMenu>(existing);
            // 必须在删除旧子项之前记录旧关联，才能完整撤销更新。
            Undo.RegisterCompleteObjectUndo(marker, "更新衣柜菜单关联");
            var replacements = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
            var removedObjects = new HashSet<UnityEngine.Object>();
            foreach (var pair in oldOwned)
            {
                foreach (var component in pair.Value.GetComponentsInChildren<Component>(true))
                    if (component != null) { removedObjects.Add(component); removedObjects.Add(component.gameObject); }
                if (newOwned.TryGetValue(pair.Key, out var replacement))
                    MapUpdatedNodes(pair.Value, replacement, replacements);
            }
            // 外加节点之间及指向其他菜单的引用不动；若引用本工具重建的节点，则重定向到对应项。
            foreach (var extra in extras)
                foreach (var component in extra.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    var serialized = new SerializedObject(component);
                    var property = serialized.GetIterator();
                    bool changed = false;
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var reference = property.objectReferenceValue;
                        if (reference == null || !removedObjects.Contains(reference)) continue;
                        if (!replacements.TryGetValue(reference, out var replacement))
                            throw new InvalidOperationException("外加节点「" + extra.name +
                                "」引用了将被移除的菜单项，请先调整该引用。");
                        if (!changed) Undo.RecordObject(component, "保留外加菜单引用");
                        property.objectReferenceValue = replacement;
                        changed = true;
                    }
                    if (changed) { serialized.ApplyModifiedProperties(); EditorUtility.SetDirty(component); }
                }

            var newChildren = staged.transform.Cast<Transform>().Select(t => t.gameObject).ToArray();
            foreach (var node in oldOwned.Values) Undo.DestroyObjectImmediate(node);
            foreach (var node in newChildren)
            {
                Undo.SetTransformParent(node.transform, existing.transform, "更新生成的菜单项");
                Undo.RecordObject(node.transform, "保留菜单项局部位置");
                node.transform.localPosition = Vector3.zero;
                node.transform.localRotation = Quaternion.identity;
                node.transform.localScale = Vector3.one;
            }
            // 用新生成项填回旧位置，外加节点保留相对顺序，数量不变时也保留原来的位置。
            var queue = new Queue<GameObject>(newChildren);
            var order = new List<GameObject>();
            foreach (var node in originalOrder)
                if (!oldSet.Contains(node)) order.Add(node);
                else if (queue.Count > 0) order.Add(queue.Dequeue());
            order.AddRange(queue);
            foreach (var node in order) Undo.RecordObject(node.transform, "保留菜单顺序");
            for (int i = 0; i < order.Count; i++) order[i].transform.SetSiblingIndex(i);

            var sourceMarker = staged.GetComponent<WardrobeGeneratedMenu>();
            marker.ClothingRoot = sourceMarker.ClothingRoot;
            marker.Slots = sourceMarker.Slots;
            marker.OutfitSwitchNode = sourceMarker.OutfitSwitchNode;
            marker.OutfitSwitchGroupParameter = sourceMarker.OutfitSwitchGroupParameter;
            marker.PartParameterPrefix = sourceMarker.PartParameterPrefix;
            marker.BatchControlNode = sourceMarker.BatchControlNode;
            marker.PartPresets = sourceMarker.PartPresets;
            var menu = existing.GetComponent<ModularAvatarMenuItem>();
            var sourceMenu = staged.GetComponent<ModularAvatarMenuItem>();
            Undo.RecordObject(menu, "更新衣柜主菜单");
            menu.Control = sourceMenu.Control;
            menu.MenuSource = sourceMenu.MenuSource;
            menu.label = sourceMenu.label;
            EditorUtility.SetDirty(marker);
            EditorUtility.SetDirty(menu);
            Undo.DestroyObjectImmediate(staged);
            return existing;
        }

        // 菜单引用的维护放在主工具内部，主工具可独立于副工具脚本使用。
        private static bool IsInstallTargetFor(GameObject node, ModularAvatarMenuInstaller installer)
        {
            var type = typeof(ModularAvatarMenuInstaller).Assembly
                .GetType("nadena.dev.modular_avatar.core.ModularAvatarMenuInstallTarget");
            var component = node == null || type == null ? null : node.GetComponent(type);
            return component != null && installer != null &&
                   new SerializedObject(component).FindProperty("installer")?.objectReferenceValue == installer;
        }

        private static bool TryRetargetInstallTarget(GameObject node, ModularAvatarMenuInstaller installer)
        {
            var type = typeof(ModularAvatarMenuInstaller).Assembly
                .GetType("nadena.dev.modular_avatar.core.ModularAvatarMenuInstallTarget");
            var component = node == null || type == null ? null : node.GetComponent(type);
            if (component == null || installer == null) return false;
            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty("installer");
            if (property == null) return false;
            Undo.RecordObject(component, "更新衣服菜单引用");
            property.objectReferenceValue = installer;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
            return true;
        }

        private static GameObject CreateChild(Transform parent, string name)
        {
            var child = new GameObject(name);
            if (child.scene != parent.gameObject.scene)
                SceneManager.MoveGameObjectToScene(child, parent.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(child, "创建 MA 衣柜节点");
            Undo.SetTransformParent(child.transform, parent, "设置 MA 衣柜父级");
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;
            return child;
        }

        private void EnsureSlots()
        {
            if (clothingRoot != null && clothingMenuName == null)
                clothingMenuName = "";
            if (slots == null || slots.Count != TotalCount)
            {
                ResetSlots();
                return;
            }
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot.Targets == null) slot.Targets = new List<GameObject>();
                if (slot.Blendshapes == null) slot.Blendshapes = new List<BlendshapeRule>();
                if (slot.Materials == null) slot.Materials = new List<MaterialRule>();
                if (!slot.IconSourceKnown)
                {
                    var link = loadedMenuRoot == null ? null : loadedMenuRoot.GetComponent<WardrobeGeneratedMenu>()?.Slots?.FirstOrDefault(s => s != null && s.Key == slot.Key);
                    slot.IconGenerated = link != null && link.IconSourceVersion > 0
                        ? slot.Icon != null && link.GeneratedIcon == slot.Icon : WardrobePartIconRenderer.IsLegacyGeneratedIcon(slot.Icon);
                    slot.IconSourceKnown = true;
                }
                if (!slot.IconAnglesInitialized)
                {
                    bool oldDefault = Mathf.Approximately(slot.IconPitch, 45) && Mathf.Approximately(slot.IconYaw, 45);
                    slot.IconPitch = 22;
                    if (Mathf.Approximately(slot.IconYaw, 0) || oldDefault) slot.IconYaw = 335;
                    slot.IconPadding = .1f;
                    slot.IconAnglesInitialized = true;
                }
                if (slot.IconViewVersion < 3)
                {
                    if (Mathf.Approximately(slot.IconPitch, 45) && Mathf.Approximately(slot.IconYaw, 45) ||
                        Mathf.Approximately(slot.IconPitch, 12) && Mathf.Approximately(slot.IconYaw, 15))
                    { slot.IconPitch = 22; slot.IconYaw = 335; }
                    slot.IconViewVersion = 3;
                }
                // 统一旧版自动名称；保留用户填写的其他名称。
                if (slot.MenuName == slot.Category || slot.MenuName == slot.Category + " 备用" ||
                    slot.MenuName == "隐藏" + DefaultMenuName(slot.Category, slot.Backup))
                    slot.MenuName = DefaultMenuName(slot.Category, slot.Backup);
            }
        }

        private void ResetSlots()
        {
            slots = CreateDefaultSlots();
            showBackups = false;
        }

        private static List<PartSlot> CreateDefaultSlots()
        {
            var defaults = new List<PartSlot>();
            for (int i = 0; i < MainCount; i++)
                defaults.Add(new PartSlot
                {
                    Key = "M" + i.ToString("00"), Category = MainNames[i],
                    MenuName = DefaultMenuName(MainNames[i], false), Backup = false
                });
            for (int i = 0; i < 9; i++)
                defaults.Add(new PartSlot
                {
                    Key = "B" + i.ToString("00"), Category = MainNames[i],
                    MenuName = DefaultMenuName(MainNames[i], true), Backup = true
                });
            return defaults;
        }

        private static string DefaultMenuName(string category, bool backup)
        {
            return PartDisplayName(category).Replace(" · ", "") + (backup ? "（备用）" : "");
        }

        // 只调整界面用语，不改已有分类键、对象名和菜单名。
        private static string PartDisplayName(string category)
        {
            switch (category)
            {
                case "裤_裙": return "裤子 · 裙子";
                case "胖次": return "内裤";
                case "袖子_手套": return "袖子 · 手套";
                case "其他1": return "其他 1";
                case "其他2": return "其他 2";
                case "其他3": return "其他 3";
                default: return category;
            }
        }

        [Serializable]
        private sealed class PartSlot
        {
            public string Key;
            public string Category;
            public string MenuName;
            public bool Backup;
            public Texture2D Icon;
            public bool IconConfigured;
            public bool IconGenerated;
            public bool IconSourceKnown;
            public bool IconTransparentCompatibility = true;
            public float IconYaw = 335;
            public float IconPitch = 22;
            public float IconPadding = .1f;
            public bool IconAnglesInitialized;
            public int IconViewVersion;
            public List<GameObject> Targets = new List<GameObject>();
            public bool AdvancedExpanded;
            public bool UseBlendshapes;
            public bool UseMaterials;
            public bool BlendshapesInverted;
            public bool MaterialsInverted;
            public string ExternalReadOnlyReason;
            public List<BlendshapeRule> Blendshapes = new List<BlendshapeRule>();
            public List<MaterialRule> Materials = new List<MaterialRule>();
        }

        [Serializable]
        private sealed class BlendshapeRule
        {
            public SkinnedMeshRenderer Renderer;
            public string ShapeName;
            public float Value;
        }

        [Serializable]
        private sealed class MaterialRule
        {
            public Renderer Renderer;
            public int MaterialIndex;
            public Material Replacement;
        }
    }
}
#endif
