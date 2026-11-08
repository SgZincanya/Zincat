using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Zincat.Switches;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Avatars.Components;

public class IntegrateMovementTool : EditorWindow
{
    [SerializeField] bool intMode;
    [SerializeField] bool expandedDefaultRestored;
    [SerializeField] string activeGroupKey;
    [SerializeField] List<ZincatMaEntry> switches = new List<ZincatMaEntry>();
    [SerializeField] List<ZincatMaGroup> groups = new List<ZincatMaGroup>();
    [SerializeField] Vector2 switchScroll, groupScroll;
    string result;
    bool resultError;

    ScrollView scroll;
    Button generate, addSelection;
    Label status;
    HelpBox feedback;
    Button reloadSource;
    DropdownField groupPicker;
    readonly Dictionary<ZincatMaEntry, Action> cardUpdates = new Dictionary<ZincatMaEntry, Action>();
    readonly Dictionary<ZincatMaGroup, Label> groupSummaries = new Dictionary<ZincatMaGroup, Label>();
    ZincatMaGroup ActiveGroup => groups.FirstOrDefault(g => g.AutoParameter == activeGroupKey) ?? groups.LastOrDefault();
    bool dark, scrollRestorePending;
    IVisualElementScheduledItem scrollRestoreTask;
    readonly Dictionary<ZincatMaEntry, Label> summaries = new Dictionary<ZincatMaEntry, Label>();
    readonly Dictionary<ZincatMaEntry, VisualElement> componentHosts = new Dictionary<ZincatMaEntry, VisualElement>();
    readonly Dictionary<ZincatMaMesh, VisualElement> meshViews = new Dictionary<ZincatMaMesh, VisualElement>();
    readonly Dictionary<object, ZincatRowView> rowUpdates = new Dictionary<object, ZincatRowView>();
    Vector2 offset { get => intMode ? groupScroll : switchScroll; set { if (intMode) groupScroll = value; else switchScroll = value; } }

    [MenuItem("Zincat/MA 分件创建")]
    public static void ShowWindow()
    {
        var window = GetWindow<IntegrateMovementTool>("MA 组件生成器"); window.minSize = new Vector2(440, 440); window.Show();
    }
    void OnEnable()
    {
        titleContent = new GUIContent("MA 组件生成器"); minSize = new Vector2(440, 440); dark = EditorGUIUtility.isProSkin;
        if (!expandedDefaultRestored) { foreach (var entry in switches.Concat(groups.SelectMany(g => g.Values))) entry.Expanded = true; expandedDefaultRestored = true; }
        Undo.undoRedoPerformed += OnUndo; EditorApplication.update += OnUpdate; EditorApplication.hierarchyChanged += UpdateFooter;
    }
    void OnDisable()
    {
        scrollRestoreTask?.Pause(); Undo.undoRedoPerformed -= OnUndo;
        EditorApplication.update -= OnUpdate; EditorApplication.hierarchyChanged -= UpdateFooter;
    }
    void OnUndo() { result = null; BuildUI(); }
    void OnUpdate() { if (dark != EditorGUIUtility.isProSkin) { dark = EditorGUIUtility.isProSkin; BuildUI(); } }
    void OnSelectionChange() { UpdateFooter(); }
    public void CreateGUI() { BuildUI(); }
    void Remember(string action) { Undo.RecordObject(this, action); result = null; }
    void Perform(Action action)
    {
        result = null;
        try { action(); }
        catch (Exception ex) { result = ex.Message; resultError = true; }
        BuildUI();
    }
    void ChangeMode(bool next)
    {
        if (intMode == next) return;
        if (scroll != null && !scrollRestorePending) offset = scroll.scrollOffset;
        scrollRestoreTask?.Pause(); scroll = null; intMode = next; result = null; BuildUI();
    }
    static VisualElement Row()
    {
        var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row; row.style.alignItems = Align.Center; row.style.minWidth = 0; return row;
    }
    static void Elastic(VisualElement element)
    { element.style.flexGrow = 1; element.style.flexShrink = 1; element.style.flexBasis = 0; element.style.minWidth = 0; }
    static void CardStyle(VisualElement box)
    {
        box.style.flexGrow = 1; box.style.flexShrink = 1; box.style.flexBasis = 350; box.style.minWidth = 0;
        box.style.marginRight = 7; box.style.marginBottom = 9; box.style.paddingLeft = box.style.paddingRight = 10;
        box.style.paddingTop = box.style.paddingBottom = 10; box.style.backgroundColor = ZincatQuickMenuStyle.Card;
        ZincatQuickMenuStyle.Border(box, ZincatQuickMenuStyle.Line, 1, 6);
    }
    void BuildUI()
    {
        if (scroll != null && !scrollRestorePending) offset = scroll.scrollOffset;
        scrollRestoreTask?.Pause(); scrollRestorePending = true; summaries.Clear(); componentHosts.Clear(); meshViews.Clear(); rowUpdates.Clear(); cardUpdates.Clear(); groupSummaries.Clear(); groupPicker = null;
        var root = rootVisualElement; root.Clear(); root.style.flexGrow = 1; root.style.backgroundColor = ZincatQuickMenuStyle.Surface;
        root.style.paddingLeft = root.style.paddingRight = 14; root.style.paddingTop = root.style.paddingBottom = 12;
        var titleRow = Row(); var title = new Label("MA 组件生成器"); title.style.fontSize = 20; title.style.marginBottom = 6; Elastic(title); titleRow.Add(title);
        titleRow.Add(new Button(() => ZincatMaPhotoWindow.OpenSettings(PhotoOptions)) { text = "分件截图设置", tooltip = "预览角度、留白与透明兼容，也可一键拍摄全部选项" }); root.Add(titleRow);
        root.Add(ZincatQuickMenuStyle.Hint("手动配置物品、形态键和材质替换；也可留空生成原生 MA 组件", 10));
        var modes = Row(); modes.style.minHeight = 30; modes.style.flexShrink = 0;
        foreach (bool useInt in new[] { false, true })
        {
            var button = new Button(() => ChangeMode(useInt)) { text = useInt ? "多选一（Int）" : "独立开关（Bool）", name = useInt == intMode ? "quick-mode-selected" : "quick-mode" };
            Elastic(button); button.style.marginRight = 4; modes.Add(button);
        }
        root.Add(modes);
        if (intMode) root.Add(ZincatQuickMenuStyle.Hint("同组共用一个参数；新增选项自动使用空闲 Int 值。", 0));
        if (intMode)
        {
            var read = new ObjectField("读取 Int 菜单") { objectType = typeof(GameObject), allowSceneObjects = true, tooltip = "拖入已生成的 Int 子菜单，读取当前 MA 配置并继续添加对象；兼容的旧菜单也可读取。" };
            read.style.marginTop = 5;
            read.RegisterValueChangedCallback(e => { if (e.newValue is GameObject obj) Perform(() => LoadIntMenu(obj)); }); root.Add(read);
            if (groups.Count > 1)
            {
                groupPicker = new DropdownField("添加到参数组", groups.Select((g, i) => (i + 1) + ". " + g.Name).ToList(), groups.IndexOf(ActiveGroup));
                groupPicker.RegisterValueChangedCallback(e => { if (groupPicker.index >= 0 && groupPicker.index < groups.Count) activeGroupKey = groups[groupPicker.index].AutoParameter; UpdateFooter(); }); root.Add(groupPicker);
            }
        }
        var tools = Row(); tools.style.marginTop = 8; tools.style.minHeight = 30; tools.style.flexShrink = 0;
        addSelection = new Button(() => Perform(() => AddObjects(Selection.gameObjects, intMode ? ActiveGroup : null))) { text = "添加选中物件", tooltip = "按名称创建卡片，并绑定物品开关目标；不修改原物件状态", name = "quick-add-selection" }; Elastic(addSelection); tools.Add(addSelection);
        var create = new Button(() => Perform(() =>
        {
            Remember("新建开关");
            if (intMode) { var group = NewGroup(); group.Values.Add(new ZincatMaEntry { Name = "选项 1" }); }
            else switches.Add(new ZincatMaEntry());
        })) { text = intMode ? "＋ 参数组" : "＋ 新建开关" }; create.style.marginLeft = 6; tools.Add(create);
        tools.Add(new Button(() => Perform(() => { result = ZincatMaPhotoWindow.CaptureAll(PhotoOptions()); resultError = false; })) { text = "一键全拍", tooltip = "当前模式全部选项逐项拍摄并回填，空来源跳过" });
        var more = new Button(More) { text = "⋯", tooltip = "从直接子级添加、展开、折叠、合并、清空" }; more.style.width = 28; more.style.marginLeft = 6; tools.Add(more); root.Add(tools);
        scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden, name = "quick-scroll" };
        scroll.style.flexGrow = 1; scroll.style.flexShrink = 1; scroll.style.flexBasis = 0; scroll.style.minHeight = 0; scroll.style.minWidth = 0; scroll.style.marginTop = 10;
        scroll.contentContainer.style.minWidth = 0; scroll.contentContainer.style.width = new Length(100, LengthUnit.Percent); root.Add(scroll);
        var grid = new VisualElement(); grid.style.flexShrink = 0; grid.style.flexDirection = FlexDirection.Row; grid.style.flexWrap = Wrap.Wrap; grid.style.alignItems = Align.FlexStart;
        if (intMode) foreach (var group in groups) grid.Add(GroupUI(group));
        else foreach (var entry in switches) grid.Add(CardUI(entry, null));
        var drop = new VisualElement(); CardStyle(drop); drop.style.minHeight = (intMode ? groups.Count : switches.Count) == 0 ? 120 : 72;
        drop.style.justifyContent = Justify.Center; drop.style.alignItems = Align.Center;
        drop.Add(new Label(intMode ? "＋ 拖入物件，新建参数组" : "＋ 拖入物件，批量创建卡片"));
        drop.Add(ZincatQuickMenuStyle.Hint("绑定物品开关；拖入已有卡片可一起控制", 0));
        RegisterDrop(drop, objects =>
        {
            if (intMode)
            {
                if (objects.Length > 256) throw new InvalidOperationException("一个 Int 参数组最多 256 个值，请分批添加。");
                foreach (var obj in objects) CheckBindable(obj);
                Remember("新建参数组"); AddObjects(objects, NewGroup());
            }
            else AddObjects(objects);
        }); grid.Add(drop); scroll.Add(grid);
        feedback = new HelpBox("", HelpBoxMessageType.Info); feedback.style.marginTop = 6; root.Add(feedback);
        reloadSource = new Button(() => Perform(() => { var group = ActiveGroup; if (group?.SourceMenu != null) LoadIntMenu(group.SourceMenu, true); })) { text = "重新读取当前菜单", tooltip = "用原生 MA 配置替换该组草稿；Ctrl+Z 可恢复草稿" }; root.Add(reloadSource);
        status = ZincatQuickMenuStyle.Hint("", 8); status.style.marginTop = 8; root.Add(status);
        generate = new Button(() => Perform(Generate)) { text = "生成 MA 组件与参数", name = "quick-primary-action" }; generate.style.height = 34; root.Add(generate);
        // Header, toolbar and footer keep their natural heights. Only the scroll viewport
        // consumes remaining space, irrespective of the number of cards in its content.
        foreach (var child in root.Children()) if (child != scroll) child.style.flexShrink = 0;
        UpdateFooter(); ZincatQuickMenuStyle.Apply(root); InstallScrollRestoration(scroll);
    }
    VisualElement GroupUI(ZincatMaGroup group)
    {
        var box = new VisualElement(); CardStyle(box); box.style.flexBasis = 420;
        box.RegisterCallback<PointerDownEvent>(_ => { activeGroupKey = group.AutoParameter; UpdateFooter(); });
        var header = Row(); var name = new TextField { value = group.Name, tooltip = "Int 子菜单名称" }; Elastic(name);
        name.RegisterValueChangedCallback(e => { Remember("修改子菜单名称"); group.Name = e.newValue; UpdateFooter(); }); header.Add(name);
        var remove = new Button(() => Perform(() => { Remember("移除参数组"); groups.Remove(group); })) { text = "×", tooltip = "从草稿移除此组，保留场景中的菜单" }; remove.style.width = 28; header.Add(remove); box.Add(header);
        var summary = ZincatQuickMenuStyle.Hint("", 4); box.Add(summary); groupSummaries[group] = summary;
        var settings = new Foldout { text = "参数与菜单来源", value = group.SettingsExpanded }; settings.style.marginTop = 4;
        settings.RegisterValueChangedCallback(e => { if (e.target == settings) group.SettingsExpanded = e.newValue; }); box.Add(settings);
        var parameter = new TextField("共享参数") { value = group.Parameter, tooltip = "留空使用自动生成的唯一参数名；同组各选项共用此参数" };
        parameter.RegisterValueChangedCallback(e => { Remember("修改 Int 参数名"); group.Parameter = e.newValue; UpdateFooter(); }); settings.Add(parameter);
        if (group.SourceMenu != null)
        {
            var source = new ObjectField("原菜单") { objectType = typeof(GameObject), value = group.SourceMenu, allowSceneObjects = true }; source.SetEnabled(false); settings.Add(source);
            var sourceActions = Row();
            sourceActions.Add(new Button(() => Perform(() => LoadIntMenu(group.SourceMenu, true))) { text = "重新读取", tooltip = "用原生 MA 当前配置替换这一组草稿，Ctrl+Z 可恢复" });
            sourceActions.Add(new Button(() => { Selection.activeGameObject = group.SourceMenu; EditorGUIUtility.PingObject(group.SourceMenu); }) { text = "定位原菜单" }); settings.Add(sourceActions);
        }
        var tools = Row(); tools.style.marginTop = 8;
        var selected = new Button(() => Perform(() => AddObjects(Selection.gameObjects, group))) { text = "＋ 选中物件", tooltip = "添加到本组，保留已有选项和 Int 值" }; Elastic(selected); tools.Add(selected);
        var empty = new Button(() => Perform(() => AddValue(group))) { text = "＋ 空选项", tooltip = "自动使用未占用的 Int 值，组件可自行配置" }; empty.SetEnabled(group.Values.Count < 256); Elastic(empty); tools.Add(empty); box.Add(tools);
        tools.Add(new Button(() => Perform(() => { foreach (var entry in group.Values) entry.Expanded = false; })) { text = "折叠选项", tooltip = "只收起编辑内容，名称、图标和 Int 值仍可查看" });
        foreach (var entry in group.Values) box.Add(CardUI(entry, group));
        RegisterDrop(box, objects => AddObjects(objects, group)); return box;
    }
    VisualElement CardUI(ZincatMaEntry entry, ZincatMaGroup group)
    {
        var box = new VisualElement(); CardStyle(box);
        if (group != null) { box.style.flexBasis = StyleKeyword.Auto; box.style.flexGrow = 0; box.style.marginRight = 0; box.style.marginTop = 8; box.style.width = new Length(100, LengthUnit.Percent); box.style.backgroundColor = ZincatQuickMenuStyle.Surface; }
        var header = Row(); box.Add(header);
        var thumbnail = new Image { image = entry.Icon, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
        thumbnail.style.width = thumbnail.style.height = 40;
        var photo = new Button(() => Perform(() => CaptureIcon(entry))) { name = "quick-icon-button", tooltip = "点击直接拍摄或重拍；可拖入图片自动优化，也可拖入场景物件作为特殊截图来源" };
        thumbnail.style.display = entry.Icon != null ? DisplayStyle.Flex : DisplayStyle.None;
        photo.style.width = photo.style.height = 46; photo.style.flexShrink = 0; photo.style.marginRight = 6;
        photo.Add(thumbnail); header.Add(photo);
        var name = new TextField { value = entry.Name, tooltip = "菜单名称" }; Elastic(name);
        name.RegisterValueChangedCallback(e => { Remember("修改开关名称"); entry.Name = e.newValue; UpdateFooter(); }); header.Add(name);
        if (group != null)
        {
            var value = new IntegerField("值") { value = entry.Value, tooltip = "同组内不能重复，范围 0–255" }; value.style.width = 84; value.style.flexShrink = 0; value.style.marginLeft = 5;
            value.AddToClassList("quick-inline-value");
            value.labelElement.style.minWidth = 14; value.labelElement.style.width = 14;
            value.RegisterValueChangedCallback(e => { Remember("设置 Int 值"); entry.Value = e.newValue; UpdateFooter(); }); header.Add(value);
        }
        var remove = new Button(() => Perform(() => { Remember("移除开关"); if (group == null) switches.Remove(entry); else group.Values.Remove(entry); })) { text = "×", tooltip = entry.SourceObject != null ? "移除此选项，写回时删除原菜单项；可撤销" : "移除这张卡片" }; remove.style.width = 28; remove.style.flexShrink = 0; header.Add(remove);
        var summary = ZincatQuickMenuStyle.Hint("", 0); summary.style.marginTop = 3; box.Add(summary); summaries[entry] = summary;
        cardUpdates[entry] = () => { thumbnail.image = entry.Icon; thumbnail.style.display = entry.Icon != null ? DisplayStyle.Flex : DisplayStyle.None; photo.text = entry.Icon != null ? "" : entry.IconSource == ZincatMaIconSource.Image ? "放图" : ZincatMaPhotoRenderer.CanCapture(IconTargets(entry)) ? "拍照" : ""; };
        if (group != null)
        {
            var issue = ZincatQuickMenuStyle.Hint("", 0); issue.style.color = EditorGUIUtility.isProSkin ? (Color)new Color32(240, 193, 132, 255) : (Color)new Color32(132, 75, 0, 255); box.Add(issue);
            cardUpdates[entry] += () => { string text = ZincatMaDraftRules.IntValueIssue(entry.Value, group.Values.Where(e => e != entry).Select(e => e.Value)); issue.text = text ?? ""; issue.style.display = text == null ? DisplayStyle.None : DisplayStyle.Flex; };
        }
        var details = new Foldout { text = "编辑配置", value = entry.Expanded }; details.style.marginTop = 3;
        details.RegisterValueChangedCallback(e => { if (e.target == details) entry.Expanded = e.newValue; }); box.Add(details);
        if (group == null)
        {
            var parameter = new TextField("参数名") { value = entry.Parameter, tooltip = "留空时生成唯一 Bool 参数名" };
            parameter.RegisterValueChangedCallback(e => { Remember("修改 Bool 参数名"); entry.Parameter = e.newValue; UpdateFooter(); }); details.Add(parameter);
        }
        var components = new VisualElement(); details.Add(components); componentHosts[entry] = components; RenderComponents(entry);
        details.Add(IconSourceUI(entry)); RegisterIconDrop(photo, entry);
        RegisterLocalDrop(box, objects => EditComponent(entry, () => BindObjects(entry, objects))); return box;
    }
    GameObject[] IconTargets(ZincatMaEntry entry) => (entry.IconSource == ZincatMaIconSource.SceneObjects ? (IEnumerable<GameObject>)entry.IconTargets : entry.Targets).Where(t => t != null && Editable(t)).Distinct().ToArray();
    List<ZincatMaPhotoOption> PhotoOptions()
    {
        if (this == null) throw new InvalidOperationException("请重新打开 MA 组件生成器。");
        return Entries().Select(entry => new ZincatMaPhotoOption
        {
            Key = entry.AutoParameter, Name = intMode ? groups.First(g => g.Values.Contains(entry)).Name + " / " + entry.Name : entry.Name, Entry = ExportDraft(entry, true), Targets = IconTargets(entry),
            ManualImage = entry.IconSource == ZincatMaIconSource.Image, SourceImage = entry.IconInput, ImagePath = entry.IconInputPath,
            Apply = image => WritePhotoIcon(entry, image)
        }).ToList();
    }
    void CaptureIcon(ZincatMaEntry entry)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请退出运行模式后截图。");
        if (entry.IconSource == ZincatMaIconSource.Image)
        {
            if (entry.IconInput) ImportIconImage(entry, entry.IconInput);
            else if (ZincatMaPhotoWindow.IsImageFile(entry.IconInputPath) && System.IO.File.Exists(entry.IconInputPath)) ImportIconFile(entry, entry.IconInputPath);
            else if (entry.Icon) ImportIconImage(entry, entry.Icon);
            else ChooseIconFile(entry);
            return;
        }
        var targets = IconTargets(entry);
        if (!ZincatMaPhotoRenderer.CanCapture(targets))
        {
            result = entry.Icon == null ? "没有可截图的物件或网格，图标保持留空。需要时可选择特殊图标来源。" : "没有可截图的网格，现有图标已保留。"; resultError = false; return;
        }
        var option = new ZincatMaPhotoOption { Name = entry.Name, Entry = ExportDraft(entry, true), Targets = targets };
        var image = ZincatMaPhotoWindow.Render(option, ZincatMaPhotoWindow.ReadSettings());
        try { WritePhotoIcon(entry, image); } finally { DestroyImmediate(image); }
    }
    void ImportIconImage(ZincatMaEntry entry, Texture2D source)
    {
        var image = ZincatMaPhotoWindow.ProcessImage(source, entry.Name);
        try { Remember("放入自定义图标"); entry.IconSource = ZincatMaIconSource.Image; entry.IconInput = source; entry.IconInputPath = null; WritePhotoIcon(entry, image); }
        finally { DestroyImmediate(image); }
    }
    void ImportIconFile(ZincatMaEntry entry, string source)
    {
        var image = ZincatMaPhotoWindow.ProcessImageFile(source, entry.Name);
        try { Remember("放入自定义图标"); entry.IconSource = ZincatMaIconSource.Image; entry.IconInput = null; entry.IconInputPath = source; WritePhotoIcon(entry, image); }
        finally { DestroyImmediate(image); }
    }
    void ChooseIconFile(ZincatMaEntry entry)
    {
        var path = EditorUtility.OpenFilePanelWithFilters("选择图标图片", "", new[] { "图片", "png,jpg,jpeg" });
        if (!string.IsNullOrEmpty(path)) ImportIconFile(entry, path);
    }
    VisualElement IconSourceUI(ZincatMaEntry entry)
    {
        var section = new Foldout { text = "特殊图标来源（可选）", value = entry.IconExpanded };
        section.RegisterValueChangedCallback(e => { if (e.target == section) entry.IconExpanded = e.newValue; });
        var names = new List<string> { "关联物件 / 网格（默认）", "单独指定场景物件", "自定义图片" };
        var mode = new DropdownField("图标来源", names, (int)entry.IconSource);
        mode.RegisterValueChangedCallback(e => Perform(() => { Remember("设置图标来源"); entry.IconSource = (ZincatMaIconSource)mode.index; })); section.Add(mode);
        if (entry.IconSource == ZincatMaIconSource.SceneObjects)
        {
            section.Add(ZincatQuickMenuStyle.Hint("仅作为截图来源，不绑定到 MA 动作；包含所放物件的子级网格。", 3));
            var targets = new VisualElement(); section.Add(targets);
            foreach (var target in entry.IconTargets.ToArray())
            {
                int index = entry.IconTargets.IndexOf(target); var row = Row();
                var field = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = true, value = target }; Elastic(field);
                field.RegisterValueChangedCallback(e => Edit(() => { var value = e.newValue as GameObject; if (value != null && !Editable(value)) throw new InvalidOperationException("请选择场景或 Prefab 编辑中的物件。"); Remember("修改截图来源"); entry.IconTargets[index] = value; })); row.Add(field);
                row.Add(new Button(() => Perform(() => { Remember("移除截图来源"); entry.IconTargets.RemoveAt(index); })) { text = "×" }); targets.Add(row);
            }
            var add = new ObjectField("添加场景物件") { objectType = typeof(GameObject), allowSceneObjects = true };
            add.RegisterValueChangedCallback(e => { if (e.newValue is GameObject obj) Perform(() => AddIconTargets(entry, new[] { obj })); }); section.Add(add);
            section.Add(new Button(() => Perform(() => AddIconTargets(entry, Selection.gameObjects))) { text = "＋ 选中层级物件" });
            RegisterLocalDrop(targets, objects => Perform(() => AddIconTargets(entry, objects)));
        }
        if (entry.IconSource == ZincatMaIconSource.Image)
        {
            var source = new ObjectField("放入图片") { objectType = typeof(Texture2D), allowSceneObjects = false, value = entry.IconInput };
            source.RegisterValueChangedCallback(e => { if (e.newValue is Texture2D image) Perform(() => ImportIconImage(entry, image)); else Perform(() => { Remember("移除自定义图片"); entry.IconInput = null; entry.IconInputPath = null; BindIcon(entry, null); }); }); section.Add(source);
            section.Add(new Button(() => Perform(() => ChooseIconFile(entry))) { text = "选择 PNG / JPG 文件…" });
            section.Add(ZincatQuickMenuStyle.Hint("自动处理为 256×256，保持比例与透明边缘，原文件不变。", 3));
        }
        var actions = Row(); actions.Add(new Button(() => Perform(() => CaptureIcon(entry))) { text = entry.IconSource == ZincatMaIconSource.Image ? "更新图片" : "拍摄 / 刷新图标" });
        actions.Add(new Button(() => Perform(() => BindIcon(entry, null))) { text = "清除图标", tooltip = "清除菜单引用，保留来源和图片文件" }); section.Add(actions);
        return section;
    }
    void AddIconTargets(ZincatMaEntry entry, IEnumerable<GameObject> targets)
    {
        var valid = targets.Where(t => t != null && Editable(t)).Distinct().ToArray();
        if (valid.Length == 0) return;
        Remember("添加特殊截图来源"); entry.IconSource = ZincatMaIconSource.SceneObjects;
        foreach (var target in valid) if (!entry.IconTargets.Contains(target)) entry.IconTargets.Add(target);
    }
    void RegisterIconDrop(VisualElement area, ZincatMaEntry entry)
    {
        bool Accepts() => DragAndDrop.objectReferences.OfType<Texture2D>().Any() || DragAndDrop.paths.Any(ZincatMaPhotoWindow.IsImageFile) || DragObjects().Length > 0;
        area.RegisterCallback<DragUpdatedEvent>(e => { if (!Accepts()) return; DragAndDrop.visualMode = DragAndDropVisualMode.Copy; e.StopPropagation(); });
        area.RegisterCallback<DragPerformEvent>(e =>
        {
            if (!Accepts()) return;
            var image = DragAndDrop.objectReferences.OfType<Texture2D>().FirstOrDefault(); var path = DragAndDrop.paths.FirstOrDefault(ZincatMaPhotoWindow.IsImageFile); var objects = DragObjects();
            DragAndDrop.AcceptDrag(); e.StopPropagation();
            Perform(() => { if (image != null) ImportIconImage(entry, image); else if (path != null) ImportIconFile(entry, path); else { AddIconTargets(entry, objects); CaptureIcon(entry); } });
        });
    }
    void WritePhotoIcon(ZincatMaEntry entry, Texture2D image)
    {
        if (this == null || (!switches.Contains(entry) && !groups.Any(g => g.Values.Contains(entry)))) throw new InvalidOperationException("原卡片已移除，请重新读取菜单。");
        const string folder = "Assets/Zincat/Cache/MaMenuIcons";
        var parts = folder.Split('/'); string current = parts[0];
        foreach (var part in parts.Skip(1)) { if (!AssetDatabase.IsValidFolder(current + "/" + part)) AssetDatabase.CreateFolder(current, part); current += "/" + part; }
        var assetPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + ZincatMaPhotoWindow.SafeName(entry.Name) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
        System.IO.File.WriteAllBytes(assetPath, image.EncodeToPNG()); AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) throw new InvalidOperationException("图标导入失败，请重试。");
        importer.textureType = TextureImporterType.Default; importer.alphaIsTransparency = true; importer.sRGBTexture = true;
        importer.mipmapEnabled = false; importer.maxTextureSize = 256; importer.wrapMode = TextureWrapMode.Clamp; importer.filterMode = FilterMode.Bilinear;
        Zincat.Clothing.Editor.WardrobeIconStorage.SetCompression(importer); ZincatMaPhotoWindow.WriteSource(importer, entry); importer.SaveAndReimport();
        var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath); if (!icon) throw new InvalidOperationException("图标导入失败。");
        BindIcon(entry, icon);
    }
    void BindIcon(ZincatMaEntry entry, Texture2D icon)
    {
        ModularAvatarMenuItem item = null; ZincatMaEntry native = null; bool baselineMatches = false;
        if (entry.SourceObject != null)
        {
            item = entry.SourceObject.GetComponent<ModularAvatarMenuItem>(); if (item == null || item.Control == null) throw new InvalidOperationException("原菜单项已失效，请重新读取菜单。");
            native = ReadIntOption(entry.SourceObject); baselineMatches = EntryFingerprint(native) == entry.SourceFingerprint;
        }
        else if (!string.IsNullOrEmpty(entry.SourceFingerprint)) throw new InvalidOperationException("原菜单项已删除，请重新读取菜单。");
        Undo.IncrementCurrentGroup(); int undo = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("更新菜单图标"); Remember("更新菜单图标"); entry.Icon = icon;
        if (item != null)
        {
            Undo.RecordObject(item, "更新菜单图标"); item.Control.icon = icon; DirtyNative(item);
            if (baselineMatches) { native.Icon = icon; entry.SourceFingerprint = EntryFingerprint(native); }
            foreach (var group in groups.Where(g => g.SourceMenu != null && g.KnownOptions.Contains(entry.SourceObject))) SyncIntMenuIcon(group.SourceMenu, group.KnownOptions);
            EditorSceneManager.MarkSceneDirty(item.gameObject.scene);
        }
        EditorUtility.SetDirty(this); Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(undo);
        result = item != null ? "图标已更新，并同步到原菜单。" : "图标已更新，生成菜单时直接使用。"; resultError = false; BuildUI(); Repaint();
    }
    void Edit(Action action)
    {
        result = null;
        try { action(); }
        catch (Exception ex) { result = ex.Message; resultError = true; }
        UpdateFooter();
    }
    void EditComponent(ZincatMaEntry entry, Action action)
    {
        Edit(() =>
        {
            action();
            if (scroll != null && !scrollRestorePending) offset = scroll.scrollOffset;
            scrollRestoreTask?.Pause(); scrollRestorePending = true;
            RenderComponents(entry); if (scroll != null) InstallScrollRestoration(scroll);
        });
    }
    void RenderComponents(ZincatMaEntry entry)
    {
        if (!componentHosts.TryGetValue(entry, out var host)) return;
        foreach (var key in rowUpdates.Where(p => p.Value.Entry == entry).Select(p => p.Key).ToArray()) rowUpdates.Remove(key);
        foreach (var key in meshViews.Where(p => p.Value.userData == entry).Select(p => p.Key).ToArray()) meshViews.Remove(key);
        host.Clear();
        for (int kind = 0; kind < 3; kind++)
        {
            if (!HasComponent(entry, kind)) continue;
            int choice = kind;
            var fold = new Foldout { text = ComponentLabel(kind), value = ComponentExpanded(entry, kind) };
            fold.style.marginTop = 7;
            fold.RegisterValueChangedCallback(e => { if (e.target == fold) SetComponentExpanded(entry, choice, e.newValue); });
            var actions = Row();
            var note = new VisualElement(); Elastic(note); actions.Add(note);
            actions.Add(new Button(() => EditComponent(entry, () => { Remember("移除组件并保留草稿"); SetComponent(entry, choice, false); })) { text = "移除组件", tooltip = "配置会保留，重新添加可以恢复；也可 Ctrl+Z 撤销" }); fold.Add(actions);
            fold.Add(kind == 0 ? ObjectBindingUI(entry) : MeshConfigurationUI(entry, kind == 1)); host.Add(fold);
        }
        var adders = Row(); adders.style.flexWrap = Wrap.Wrap; adders.style.marginTop = 7;
        for (int kind = 0; kind < 3; kind++)
        {
            if (HasComponent(entry, kind)) continue;
            int choice = kind;
            var button = new Button(() => EditComponent(entry, () => { Remember("添加组件"); SetComponent(entry, choice, true); SetComponentExpanded(entry, choice, true); })) { text = "＋ " + ComponentLabel(kind), tooltip = "恢复已有草稿或添加空配置，不自动绑定目标" };
            Elastic(button); button.style.marginRight = 3; adders.Add(button);
        }
        host.Add(adders); if (host.panel != null) ZincatQuickMenuStyle.Apply(host);
    }
    static bool ComponentExpanded(ZincatMaEntry entry, int kind) => kind == 0 ? entry.ObjectsExpanded : kind == 1 ? entry.ShapesExpanded : entry.MaterialsExpanded;
    static void SetComponentExpanded(ZincatMaEntry entry, int kind, bool value)
    { if (kind == 0) entry.ObjectsExpanded = value; else if (kind == 1) entry.ShapesExpanded = value; else entry.MaterialsExpanded = value; }
    void TrackRow(object row, ZincatMaEntry entry, VisualElement editor, Label hint, Func<bool> enabled, Func<string> issue)
    {
        rowUpdates[row] = new ZincatRowView { Entry = entry, Update = () =>
        {
            bool use = enabled(); editor.style.opacity = use ? 1 : .65f;
            string error = use ? issue() : null;
            hint.text = !use ? "未参与生成，配置仍可编辑。" : error ?? "";
            hint.style.display = hint.text.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            hint.style.color = error != null ? (Color)(EditorGUIUtility.isProSkin ? new Color32(240, 193, 132, 255) : new Color32(132, 75, 0, 255)) : ZincatQuickMenuStyle.Muted;
        } };
    }
    VisualElement ObjectBindingUI(ZincatMaEntry entry)
    {
        var box = new VisualElement(); box.style.minWidth = 0;
        foreach (var binding in entry.Bindings)
        {
            var editor = new VisualElement(); editor.style.marginTop = 6; box.Add(editor);
            var line = Row(); editor.Add(line);
            var enabled = new Toggle { value = binding.Enabled, tooltip = "参与生成；取消勾选可保留此项草稿" }; enabled.style.width = 22; line.Add(enabled);
            enabled.RegisterValueChangedCallback(e => Edit(() => { Remember("设置物品项参与生成"); binding.Enabled = e.newValue; }));
            var target = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = true, value = binding.Target, tooltip = "物品开关目标；更换目标会保留打开/关闭设置" }; Elastic(target); line.Add(target);
            target.RegisterValueChangedCallback(e => Edit(() => { Remember("设置物品目标"); binding.Target = e.newValue as GameObject; }));
            var state = new DropdownField(new List<string> { "打开", "关闭" }, binding.Active ? 0 : 1); state.style.width = 68; line.Add(state);
            state.RegisterValueChangedCallback(e => Edit(() => { Remember("设置物品开关状态"); binding.Active = state.index == 0; }));
            var remove = new Button(() => EditComponent(entry, () => { Remember("移除物品项"); entry.Bindings.Remove(binding); })) { text = "×", tooltip = "删除这一项，Ctrl+Z 可撤销" }; remove.style.width = 28; line.Add(remove);
            var hint = ZincatQuickMenuStyle.Hint("", 0); editor.Add(hint);
            TrackRow(binding, entry, editor, hint, () => binding.Enabled, () => BindingIssue(entry, binding));
        }
        var tools = Row(); tools.style.marginTop = 7;
        var picker = new ObjectField("添加物件") { objectType = typeof(GameObject), allowSceneObjects = true }; Elastic(picker); tools.Add(picker);
        picker.RegisterValueChangedCallback(e => { if (e.newValue is GameObject obj) EditComponent(entry, () => BindObjects(entry, new[] { obj })); });
        tools.Add(new Button(() => EditComponent(entry, () => BindObjects(entry, Selection.gameObjects))) { text = "＋选中" });
        tools.Add(new Button(() => EditComponent(entry, () => { Remember("添加空物品项"); entry.Bindings.Add(new ZincatMaBinding { Active = true }); })) { text = "＋一项" }); box.Add(tools);
        if (entry.Bindings.Count == 0) box.Add(ZincatQuickMenuStyle.Hint("留空可生成原生 MA Object Toggle。", 0));
        RegisterLocalDrop(box, objects => EditComponent(entry, () => BindObjects(entry, objects))); return box;
    }
    VisualElement MeshConfigurationUI(ZincatMaEntry entry, bool shapeMode)
    {
        var meshes = shapeMode ? entry.ShapeMeshes : entry.MaterialMeshes;
        var section = new VisualElement(); section.style.minWidth = 0;
        foreach (var mesh in meshes)
        {
            var box = new VisualElement { userData = entry }; box.style.marginTop = 8; box.style.paddingLeft = box.style.paddingRight = 7;
            box.style.paddingTop = box.style.paddingBottom = 7; box.style.backgroundColor = ZincatQuickMenuStyle.Surface;
            ZincatQuickMenuStyle.Border(box, ZincatQuickMenuStyle.Line, 1, 4); section.Add(box); meshViews[mesh] = box;
            var head = Row();
            var target = new ObjectField("目标网格") { objectType = shapeMode ? typeof(SkinnedMeshRenderer) : typeof(Renderer), allowSceneObjects = true, value = mesh.Renderer, tooltip = "更换网格会保留现有配置，不匹配的项目会给出提示" }; Elastic(target); head.Add(target);
            target.RegisterValueChangedCallback(e =>
            {
                var renderer = e.newValue as Renderer;
                var existing = renderer != null ? meshes.FirstOrDefault(m => m != mesh && m.Renderer == renderer) : null;
                if (existing != null) { target.SetValueWithoutNotify(mesh.Renderer); FocusMesh(existing); return; }
                EditComponent(entry, () => { Remember("更换配置网格"); mesh.Renderer = renderer; });
            });
            var remove = new Button(() => EditComponent(entry, () => { Remember("移除网格配置"); meshes.Remove(mesh); })) { text = "×", tooltip = "删除网格及其配置，Ctrl+Z 可撤销" }; remove.style.width = 28; head.Add(remove); box.Add(head);
            var rows = new Foldout { text = "配置项 · " + mesh.Rows.Count, value = mesh.Expanded };
            rows.RegisterValueChangedCallback(e => { if (e.target == rows) mesh.Expanded = e.newValue; }); box.Add(rows);
            foreach (var setting in mesh.Rows) rows.Add(MeshRowUI(entry, mesh, setting, shapeMode));
            if (mesh.Rows.Count == 0) rows.Add(ZincatQuickMenuStyle.Hint("尚未添加项目，可直接生成空组件。", 0));
            var tools = Row(); tools.style.marginTop = 7; tools.style.flexWrap = Wrap.Wrap;
            tools.Add(new Button(() => EditComponent(entry, () => { Remember("添加空配置项"); mesh.Rows.Add(new ZincatMaMeshSetting()); mesh.Expanded = true; })) { text = "＋ 添加一项" });
            Button choose = null;
            choose = new Button(() => OpenMeshChoices(entry, mesh, shapeMode, choose)) { text = "批量添加…", tooltip = "只追加所选的新项目，已有配置不删除" };
            choose.SetEnabled(mesh.Renderer != null && MeshChoices(mesh.Renderer, shapeMode).Length > 0); tools.Add(choose);
            if (shapeMode)
                foreach (float value in new[] { 0f, 100f })
                {
                    float chosen = value;
                    tools.Add(new Button(() => EditComponent(entry, () =>
                    {
                        Remember("批量设置形态键目标值"); foreach (var row in mesh.Rows.Where(r => r.Enabled)) { ZincatMaDraftRules.SetWeightText(row, chosen.ToString("G9", CultureInfo.CurrentCulture)); }
                    })) { text = "全部设为 " + chosen, tooltip = "仅修改勾选参与生成的项目" });
                }
            rows.Add(tools);
        }
        var add = new ObjectField("添加网格") { objectType = shapeMode ? typeof(SkinnedMeshRenderer) : typeof(Renderer), allowSceneObjects = true, tooltip = "手动添加网格；重复添加会定位已有分组" }; add.style.marginTop = 8;
        add.RegisterValueChangedCallback(e =>
        {
            if (!(e.newValue is Renderer renderer)) return;
            var existing = meshes.FirstOrDefault(m => m.Renderer == renderer);
            if (existing != null) { add.SetValueWithoutNotify(null); FocusMesh(existing); return; }
            EditComponent(entry, () => AddMesh(entry, renderer, shapeMode));
        }); section.Add(add);
        if (meshes.Count == 0) section.Add(ZincatQuickMenuStyle.Hint("添加网格后可逐行编辑或批量添加，也可留空生成。", 0));
        RegisterLocalDrop(section, objects =>
        {
            var renderers = objects.SelectMany(o => shapeMode ? o.GetComponents<SkinnedMeshRenderer>().Cast<Renderer>() : o.GetComponents<Renderer>()).Distinct().ToArray();
            if (renderers.Length == 0) { Edit(() => { throw new InvalidOperationException("请拖入带有对应网格组件的物件。"); }); return; }
            if (renderers.Length == 1)
            {
                var existing = meshes.FirstOrDefault(m => m.Renderer == renderers[0]); if (existing != null) { FocusMesh(existing); return; }
            }
            EditComponent(entry, () => { foreach (var renderer in renderers) AddMesh(entry, renderer, shapeMode); });
        }); return section;
    }
    VisualElement MeshRowUI(ZincatMaEntry entry, ZincatMaMesh mesh, ZincatMaMeshSetting setting, bool shapeMode)
    {
        var editor = new VisualElement(); editor.style.marginTop = 6; editor.style.minWidth = 0;
        var line = Row(); editor.Add(line);
        var enabled = new Toggle { value = setting.Enabled, tooltip = "参与生成；取消勾选可保留草稿" }; enabled.style.width = 22; line.Add(enabled);
        enabled.RegisterValueChangedCallback(e => Edit(() => { Remember("设置配置项参与生成"); setting.Enabled = e.newValue; }));
        ObjectField original = null;
        var choices = MeshChoices(mesh.Renderer, shapeMode);
        if (shapeMode)
        {
            var keys = new List<string> { "" }; keys.AddRange(choices);
            if (!string.IsNullOrEmpty(setting.Shape) && !keys.Contains(setting.Shape)) keys.Add(setting.Shape);
            var labels = keys.Select(k => k.Length == 0 ? "选择形态键" : choices.Contains(k) ? k : "失效 · " + k).ToList();
            var shape = new PopupField<string>(labels, Mathf.Max(0, keys.IndexOf(setting.Shape ?? ""))); Elastic(shape); line.Add(shape);
            shape.RegisterValueChangedCallback(e => Edit(() => { Remember("更换形态键"); setting.Shape = keys[shape.index]; }));
            var number = new TextField { value = setting.WeightText ?? (setting.WeightSet ? setting.Weight.ToString("G9", CultureInfo.CurrentCulture) : ""), tooltip = "手动填写目标值，直接输入 0 也有效，可超出 0–100。留空会保留为草稿。" }; number.style.width = 70; number.style.marginLeft = 4; line.Add(number);
            Action<float> set = value => Edit(() => { Remember("设置形态键目标值"); ZincatMaDraftRules.SetWeightText(setting, value.ToString("G9", CultureInfo.CurrentCulture)); number.SetValueWithoutNotify(setting.WeightText); });
            number.RegisterValueChangedCallback(e => Edit(() => { Remember("设置形态键目标值"); ZincatMaDraftRules.SetWeightText(setting, e.newValue); }));
            if (setting.DeleteShape) { number.SetEnabled(false); editor.Add(ZincatQuickMenuStyle.Hint("原生 MA 删除形态键配置，写回时会保留。", 0)); }
            number.AddManipulator(new ContextualMenuManipulator(e => { e.menu.AppendAction("设为 0", _ => set(0)); e.menu.AppendAction("设为 100", _ => set(100)); }));
        }
        else
        {
            var indices = new List<int> { -1 }; indices.AddRange(Enumerable.Range(0, choices.Length));
            if (setting.Slot >= choices.Length && setting.Slot >= 0) indices.Add(setting.Slot);
            var labels = indices.Select(i => i < 0 ? "选择材质槽" : i < choices.Length ? choices[i] : "失效槽位 " + i).ToList();
            var slot = new PopupField<string>(labels, Mathf.Max(0, indices.IndexOf(setting.Slot))); Elastic(slot); line.Add(slot);
            slot.RegisterValueChangedCallback(e => Edit(() =>
            {
                Remember("更换材质槽"); setting.Slot = indices[slot.index];
                if (original != null) original.SetValueWithoutNotify(OriginalMaterial(mesh.Renderer, setting.Slot));
            }));
            original = new ObjectField("原材质") { objectType = typeof(Material), value = OriginalMaterial(mesh.Renderer, setting.Slot), allowSceneObjects = false }; original.SetEnabled(false);
        }
        var remove = new Button(() => EditComponent(entry, () => { Remember("删除配置项"); mesh.Rows.Remove(setting); })) { text = "×", tooltip = "删除这一项，Ctrl+Z 可撤销" }; remove.style.width = 28; line.Add(remove);
        if (!shapeMode)
        {
            editor.Add(original);
            var material = new ObjectField("替换为") { objectType = typeof(Material), allowSceneObjects = false, value = setting.Material };
            material.RegisterValueChangedCallback(e => Edit(() => { Remember("设置替换材质"); setting.Material = e.newValue as Material; setting.AllowNullMaterial = false; })); editor.Add(material);
        }
        var hint = ZincatQuickMenuStyle.Hint("", 0); editor.Add(hint);
        TrackRow(setting, entry, editor, hint, () => setting.Enabled, () => MeshRowIssue(entry, mesh, setting, shapeMode)); return editor;
    }
    static Material OriginalMaterial(Renderer renderer, int slot)
    { var materials = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>(); return slot >= 0 && slot < materials.Length ? materials[slot] : null; }
    void FocusMesh(ZincatMaMesh mesh)
    {
        if (!meshViews.TryGetValue(mesh, out var view) || scroll == null) return;
        mesh.Expanded = true; var fold = view.Q<Foldout>(); fold?.SetValueWithoutNotify(true);
        var current = scroll; current.schedule.Execute(() => { if (scroll == current && view.panel != null) { current.ScrollTo(view); view.Q<ObjectField>()?.Focus(); } });
    }
    static string[] MeshChoices(Renderer renderer, bool shapeMode)
    {
        if (renderer == null) return Array.Empty<string>();
        if (!shapeMode) return renderer.sharedMaterials.Select((m, i) => i + " · " + (m != null ? m.name : "空材质")).ToArray();
        var mesh = (renderer as SkinnedMeshRenderer)?.sharedMesh;
        return mesh == null ? Array.Empty<string>() : Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
    }
    void AddMesh(ZincatMaEntry entry, Renderer renderer, bool shapeMode)
    {
        var meshes = shapeMode ? entry.ShapeMeshes : entry.MaterialMeshes;
        if (meshes.Any(m => m.Renderer == renderer)) return;
        Remember("添加配置网格"); meshes.Add(new ZincatMaMesh { Renderer = renderer });
    }
    void OpenMeshChoices(ZincatMaEntry entry, ZincatMaMesh mesh, bool shapeMode, Button button)
    {
        var choices = MeshChoices(mesh.Renderer, shapeMode); var renderer = mesh.Renderer;
        var existing = Enumerable.Range(0, choices.Length).Where(i => mesh.Rows.Any(r => shapeMode ? r.Shape == choices[i] : r.Slot == i));
        UnityEditor.PopupWindow.Show(button.worldBound, new ZincatMeshSelectionPopup(choices, existing, picked => EditComponent(entry, () =>
        {
            var meshes = shapeMode ? entry.ShapeMeshes : entry.MaterialMeshes;
            if (!Entries().Contains(entry) || !meshes.Contains(mesh) || renderer == null || mesh.Renderer != renderer || !choices.SequenceEqual(MeshChoices(renderer, shapeMode))) throw new InvalidOperationException("网格内容已变化，请重新打开批量添加。");
            Remember("批量追加配置项"); mesh.Expanded = true;
            ZincatMaDraftRules.AppendSelection(mesh.Rows, choices, picked, shapeMode);
        })));
    }
    void RegisterLocalDrop(VisualElement area, Action<GameObject[]> action)
    {
        area.RegisterCallback<DragUpdatedEvent>(e => { if (DragObjects().Length == 0) return; DragAndDrop.visualMode = DragAndDropVisualMode.Copy; e.StopPropagation(); });
        area.RegisterCallback<DragPerformEvent>(e => { var objects = DragObjects(); if (objects.Length == 0) return; DragAndDrop.AcceptDrag(); action(objects); e.StopPropagation(); });
    }

    static bool Editable(GameObject obj)
    {
        if (obj == null || EditorUtility.IsPersistent(obj) || !obj.scene.IsValid() || !obj.scene.isLoaded) return false;
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        return !EditorSceneManager.IsPreviewScene(obj.scene) || (stage != null && stage.scene == obj.scene);
    }
    static void CheckBindable(GameObject obj)
    {
        if (!Editable(obj)) throw new InvalidOperationException("请选择场景或 Prefab 编辑中的物件。");
        if (obj.GetComponent<VRCAvatarDescriptor>() != null) throw new InvalidOperationException("角色根对象不能作为显隐目标，请选择子物件。");
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && obj == stage.prefabContentsRoot) throw new InvalidOperationException("请控制 Prefab 的子级物件。");
    }
    void BindObjects(ZincatMaEntry entry, GameObject[] objects)
    {
        var batch = objects.Where(o => o != null).Distinct().ToArray();
        if (batch.Length == 0) throw new InvalidOperationException("请选择或拖入要控制的物件。");
        Remember("绑定物品开关目标"); entry.Objects = true;
        foreach (var obj in batch) if (!entry.Bindings.Any(b => b.Target == obj)) entry.Bindings.Add(new ZincatMaBinding { Target = obj, Active = !obj.activeSelf });
    }
    static void CheckTargetSet(IEnumerable<GameObject> objects)
    {
        var targets = objects.Where(o => o != null).Distinct().ToArray();
        if (targets.Select(o => o.scene.handle).Distinct().Count() > 1 || targets.Select(Owner).Distinct().Count() > 1)
            throw new InvalidOperationException("同一个开关的目标须属于同一场景、同一角色。");
    }
    static List<ZincatMaMesh> MergeMeshes(IEnumerable<ZincatMaMesh> meshes, bool shapeMode)
    {
        return meshes.GroupBy(m => m.Renderer).Select(g => new ZincatMaMesh
        {
            Renderer = g.Key,
            Rows = g.SelectMany(m => m.Rows).GroupBy(r => new { Key = shapeMode ? r.Shape : r.Slot.ToString(), r.Enabled, r.WeightSet, r.Weight, r.Material }).Select(rows => rows.First()).ToList()
        }).ToList();
    }

    static string ComponentLabel(int kind) => kind == 0 ? "物品开关" : kind == 1 ? "形态键切换" : "材质替换";
    static bool HasComponent(ZincatMaEntry entry, int kind) => kind == 0 ? entry.Objects : kind == 1 ? entry.Shapes : entry.Materials;
    static void SetComponent(ZincatMaEntry entry, int kind, bool value)
    { if (kind == 0) entry.Objects = value; else if (kind == 1) entry.Shapes = value; else entry.Materials = value; }
    ZincatMaGroup NewGroup() { var group = new ZincatMaGroup(); groups.Add(group); activeGroupKey = group.AutoParameter; return group; }
    ZincatMaEntry AddValue(ZincatMaGroup group, string name = null)
    {
        if (!groups.Contains(group)) throw new InvalidOperationException("参数组已失效。");
        int? available = ZincatMaDraftRules.NextIntValue(group.Values.Select(e => e.Value));
        if (!available.HasValue) throw new InvalidOperationException("这个组的 0–255 已用完，请新建参数组。");
        Remember("添加 Int 选项"); activeGroupKey = group.AutoParameter; var entry = new ZincatMaEntry { Name = name ?? "选项 " + available.Value, Value = available.Value }; group.Values.Add(entry); return entry;
    }
    void AddObjects(GameObject[] objects, ZincatMaGroup destination = null)
    {
        var batch = objects.Where(o => o != null && o.scene.IsValid() && !EditorUtility.IsPersistent(o)).Distinct().ToArray();
        if (batch.Length == 0) throw new InvalidOperationException("请选择或拖入场景里的物件。");
        if (intMode && destination != null && !groups.Contains(destination)) throw new InvalidOperationException("参数组已失效。");
        if (intMode && destination != null && destination.Values.Count + batch.Length > 256) throw new InvalidOperationException("该组剩余 Int 值不足，请减少选择或新建参数组。");
        foreach (var obj in batch) CheckBindable(obj);
        Remember("添加物品开关卡片");
        if (intMode)
        {
            var group = destination ?? ActiveGroup ?? NewGroup();
            if (group.Values.Count + batch.Length > 256) throw new InvalidOperationException("该组剩余 Int 值不足，请减少选择或新建参数组。");
            foreach (var obj in batch) { var entry = AddValue(group, obj.name); entry.Bindings.Add(new ZincatMaBinding { Target = obj, Active = !obj.activeSelf }); }
        }
        else foreach (var obj in batch) { var entry = new ZincatMaEntry { Name = obj.name }; entry.Bindings.Add(new ZincatMaBinding { Target = obj, Active = !obj.activeSelf }); switches.Add(entry); }
    }
    void RegisterDrop(VisualElement area, Action<GameObject[]> action)
    {
        area.RegisterCallback<DragUpdatedEvent>(e => { if (DragObjects().Length == 0) return; DragAndDrop.visualMode = DragAndDropVisualMode.Copy; e.StopPropagation(); });
        area.RegisterCallback<DragPerformEvent>(e => { var objects = DragObjects(); if (objects.Length == 0) return; DragAndDrop.AcceptDrag(); Perform(() => action(objects)); e.StopPropagation(); });
    }
    static GameObject[] DragObjects() => DragAndDrop.objectReferences.Select(o => o as GameObject ?? (o as Component)?.gameObject).Where(o => o != null && o.scene.IsValid() && !EditorUtility.IsPersistent(o)).Distinct().ToArray();
    void More()
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("从选中物件的直接子级添加"), false, () => Perform(() => AddObjects(Selection.gameObjects.SelectMany(o => o.transform.Cast<Transform>().Select(t => t.gameObject)).ToArray())));
        menu.AddItem(new GUIContent("展开全部卡片"), false, () => Perform(() => { foreach (var entry in Entries()) entry.Expanded = true; }));
        menu.AddItem(new GUIContent("折叠全部卡片"), false, () => Perform(() => { foreach (var entry in Entries()) entry.Expanded = false; }));
        if (!intMode && switches.Count > 1) menu.AddItem(new GUIContent("合并全部卡片（沿用首张的名称、参数和图标）"), false, () => Perform(() =>
        {
            var first = switches[0]; var bindings = switches.Where(e => e.Objects).SelectMany(e => e.Bindings).ToArray();
            var shapeMeshes = MergeMeshes(switches.Where(e => e.Shapes).SelectMany(e => e.ShapeMeshes), true);
            var materialMeshes = MergeMeshes(switches.Where(e => e.Materials).SelectMany(e => e.MaterialMeshes), false);
            Remember("合并卡片"); first.Objects = switches.Any(e => e.Objects); first.Shapes = switches.Any(e => e.Shapes); first.Materials = switches.Any(e => e.Materials);
            first.ShapeMeshes = shapeMeshes; first.MaterialMeshes = materialMeshes;
            first.Bindings = bindings.GroupBy(b => new { b.Target, b.Active, b.Enabled }).Select(g => g.First()).ToList(); switches.RemoveRange(1, switches.Count - 1);
        }));
        menu.AddSeparator("");
        menu.AddItem(new GUIContent("清空当前模式的临时编辑"), false, () => Perform(() => { Remember("清空临时编辑"); if (intMode) groups.Clear(); else switches.Clear(); offset = Vector2.zero; scroll = null; })); menu.ShowAsContext();
    }
    IEnumerable<ZincatMaEntry> Entries() => intMode ? groups.SelectMany(g => g.Values) : switches;
    void UpdateFooter()
    {
        if (generate == null) return;
        var entries = Entries().ToArray(); var counts = CountAllDrafts(); string issue = Validate();
        bool canStart = (intMode ? groups.Count > 0 : entries.Length > 0) && !EditorApplication.isPlayingOrWillChangePlaymode;
        int loaded = groups.Count(g => g.SourceMenu != null), fresh = groups.Count - loaded;
        generate.text = !intMode ? "生成 " + entries.Length + " 个独立开关" : loaded == 0 ? "生成 " + fresh + " 个 Int 菜单" : fresh == 0 ? "写回 " + loaded + " 个 Int 菜单" : "写回 " + loaded + " 个菜单，并生成 " + fresh + " 个新菜单";
        if (groupPicker != null)
        {
            var names = groups.Select((g, i) => (i + 1) + ". " + g.Name).ToList();
            if (!groupPicker.choices.SequenceEqual(names)) groupPicker.choices = names;
            if (ActiveGroup != null) groupPicker.SetValueWithoutNotify(names[groups.IndexOf(ActiveGroup)]);
        }
        if (intMode && ActiveGroup != null) { addSelection.text = "＋ 选中物件 → 当前组"; addSelection.tooltip = "添加到：“" + ActiveGroup.Name + "”。组内 Int 值自动避重。"; }
        generate.SetEnabled(canStart); generate.style.opacity = canStart ? 1 : .45f;
        addSelection.SetEnabled(Selection.gameObjects.Any(o => o != null && o.scene.IsValid() && !EditorUtility.IsPersistent(o)));
        foreach (var pair in summaries)
        {
            var entry = pair.Key; var c = CountDraft(entry); var kinds = Enumerable.Range(0, 3).Where(k => HasComponent(entry, k)).Select(ComponentLabel);
            pair.Value.text = (kinds.Any() ? string.Join(" · ", kinds) : "请添加组件") + " · " + c.Ready + " 项可写入" + (c.Pending > 0 ? " · " + c.Pending + " 项待填" : "");
        }
        foreach (var update in cardUpdates.Values) update();
        foreach (var pair in groupSummaries) pair.Value.text = (pair.Key.SourceMenu != null ? "编辑已有菜单" : "新菜单") + " · " + pair.Key.Values.Count + " 个选项 · 共用 1 个 Int 参数";
        foreach (var row in rowUpdates.Values.ToArray()) row.Update();
        status.text = issue ?? counts.Ready + " 项已完成 · " + counts.Pending + " 项待填写 · " + counts.Disabled + " 项未参与生成";
        feedback.text = result ?? ""; feedback.messageType = resultError ? HelpBoxMessageType.Error : HelpBoxMessageType.Info;
        feedback.style.display = string.IsNullOrEmpty(result) ? DisplayStyle.None : DisplayStyle.Flex;
        reloadSource.style.display = resultError && result != null && result.Contains("重新读取") && intMode && ActiveGroup?.SourceMenu != null ? DisplayStyle.Flex : DisplayStyle.None;
    }

    void InstallScrollRestoration(ScrollView view)
    {
        // Capture user scrolling continuously, but never capture layout clamping while rebuilding.
        Action restore = () =>
        {
            if (scroll != view || !scrollRestorePending || view.panel == null) return;
            float viewport = view.contentViewport.layout.height;
            float content = view.contentContainer.layout.height;
            float range = view.verticalScroller.highValue;
            if (float.IsNaN(viewport) || float.IsNaN(content) || float.IsNaN(range) ||
                float.IsInfinity(viewport) || float.IsInfinity(content) || float.IsInfinity(range) ||
                viewport <= 0 || content <= 0 || range >= int.MaxValue / 2f) return;
            if (content > viewport + 1 && range <= 0) return;
            view.scrollOffset = new Vector2(0, Mathf.Clamp(offset.y, 0, Mathf.Max(0, range)));
            offset = view.scrollOffset; scrollRestorePending = false;
        };
        // Geometry callbacks run after the controls have their sizes. Scheduling the restore
        // from here also lets ScrollView finish updating its scrollbar limits first.
        if (!ReferenceEquals(view.userData, this))
        {
            view.userData = this;
            view.verticalScroller.valueChanged += value => { if (scroll == view && !scrollRestorePending) offset = new Vector2(0, Mathf.Max(0, value)); };
            view.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => { if (scroll == view && scrollRestorePending) view.schedule.Execute(restore); });
            view.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => { if (scroll == view && scrollRestorePending) view.schedule.Execute(restore); });
        }
        scrollRestoreTask?.Pause();
        scrollRestoreTask = view.schedule.Execute(restore).Every(16).Until(() => scroll != view || !scrollRestorePending);
    }
    static string BindingIssue(ZincatMaEntry entry, ZincatMaBinding binding)
    {
        if (!Editable(binding.Target)) return "请选择有效的场景物件。";
        if (binding.Target.GetComponent<VRCAvatarDescriptor>() != null) return "角色根不能作为显隐目标，请选择子物件。";
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && (binding.Target.scene != stage.scene || binding.Target == stage.prefabContentsRoot)) return "请使用当前 Prefab 的子级物件。";
        if (entry.Bindings.Count(b => b.Enabled && b.Target == binding.Target) > 1) return "这个物件有重复配置，请调整或取消一项参与生成。";
        return null;
    }
    static string MeshRowIssue(ZincatMaEntry entry, ZincatMaMesh mesh, ZincatMaMeshSetting setting, bool shapeMode)
    {
        if (mesh.Renderer == null || !Editable(mesh.Renderer.gameObject)) return "请选择有效的目标网格。";
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && (mesh.Renderer.gameObject.scene != stage.scene || mesh.Renderer.gameObject == stage.prefabContentsRoot)) return "请使用当前 Prefab 的子级网格。";
        if (shapeMode && !(mesh.Renderer is SkinnedMeshRenderer)) return "形态键需要蒙皮网格。";
        var choices = MeshChoices(mesh.Renderer, shapeMode);
        if (shapeMode)
        {
            if (string.IsNullOrEmpty(setting.Shape)) return "请选择形态键。";
            if (!choices.Contains(setting.Shape)) return "该形态键在当前网格中不存在，配置已保留。";
            if (!setting.DeleteShape && (!setting.WeightSet || float.IsNaN(setting.Weight) || float.IsInfinity(setting.Weight))) return "请填写有效的目标值，可超出 0–100。";
        }
        else
        {
            if (setting.Slot < 0 || setting.Slot >= choices.Length) return "请选择当前网格的有效材质槽。";
            if (!setting.AllowNullMaterial && (setting.Material == null || !EditorUtility.IsPersistent(setting.Material))) return "请选择项目中的替换材质。";
        }
        var meshes = shapeMode ? entry.ShapeMeshes : entry.MaterialMeshes;
        if (meshes.Where(m => m.Renderer == mesh.Renderer).SelectMany(m => m.Rows).Count(r => r.Enabled && (shapeMode ? r.Shape == setting.Shape : r.Slot == setting.Slot)) > 1)
            return "该属性有重复配置，请调整或取消一项参与生成。";
        return null;
    }
    ZincatMaDraftCount CountDraft(ZincatMaEntry entry)
    {
        var count = new ZincatMaDraftCount();
        if (entry.Objects) foreach (var binding in entry.Bindings) count.Add(binding.Enabled, BindingIssue(entry, binding));
        foreach (bool shapeMode in new[] { true, false })
        {
            if (!(shapeMode ? entry.Shapes : entry.Materials)) continue;
            foreach (var mesh in shapeMode ? entry.ShapeMeshes : entry.MaterialMeshes)
                foreach (var row in mesh.Rows) count.Add(row.Enabled, MeshRowIssue(entry, mesh, row, shapeMode));
        }
        return count;
    }
    ZincatMaDraftCount CountAllDrafts()
    {
        var count = new ZincatMaDraftCount();
        foreach (var entry in Entries()) { var c = CountDraft(entry); count.Ready += c.Ready; count.Pending += c.Pending; count.Disabled += c.Disabled; }
        return count;
    }
    string Validate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return "请在编辑模式下生成。";
        var stage = PrefabStageUtility.GetCurrentPrefabStage(); var scene = stage != null ? stage.scene : SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded) return "请先打开场景或 Prefab。";
        var entries = Entries().ToArray();
        if ((intMode ? groups.Count : switches.Count) == 0) return intMode ? "添加参数组，然后填写选项名称和各个值。" : "添加开关，选择要生成的组件。";
        if (entries.Any(e => string.IsNullOrWhiteSpace(e.Name))) return "请填写每个开关的名称。";
        if (entries.Any(e => !e.Objects && !e.Shapes && !e.Materials && e.SourceObject == null)) return "每个开关至少添加一种组件。";
        if (intMode)
            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group.Name)) return "请填写子菜单名称。";
                    if (group.Values.Any(e => e.Value < 0 || e.Value > 255)) return group.Name + "：Int 值须在 0–255 之间。";
                if (group.Values.Select(e => e.Value).Distinct().Count() != group.Values.Count) return group.Name + "：同组 Int 值不能重复。";
            }
        var parameters = intMode ? groups.Select(g => g.EffectiveParameter) : switches.Select(e => e.EffectiveParameter);
        if (parameters.Distinct().Count() != parameters.Count()) return "参数名重复，请为不同开关或参数组使用不同名称。";
        return null;
    }
    ZincatMaEntry ExportDraft(ZincatMaEntry entry, bool values)
    {
        var copy = new ZincatMaEntry { Name = entry.Name, Parameter = entry.Parameter, AutoParameter = entry.AutoParameter, Value = entry.Value, Icon = entry.Icon, Objects = entry.Objects, Shapes = entry.Shapes, Materials = entry.Materials };
        copy.Bindings = ZincatMaDraftRules.CopyBindings(entry.Bindings, values && entry.Objects, b => BindingIssue(entry, b));
        foreach (bool shapeMode in new[] { true, false })
        {
            if (!(shapeMode ? entry.Shapes : entry.Materials)) continue;
            var destination = shapeMode ? copy.ShapeMeshes : copy.MaterialMeshes;
            foreach (var mesh in shapeMode ? entry.ShapeMeshes : entry.MaterialMeshes)
            {
                var rows = ZincatMaDraftRules.CopyRows(mesh.Rows, values, r => MeshRowIssue(entry, mesh, r, shapeMode));
                if (rows.Count > 0 || (mesh.Rows.Count == 0 && mesh.Renderer != null && Editable(mesh.Renderer.gameObject))) destination.Add(new ZincatMaMesh { Renderer = mesh.Renderer, Rows = rows });
            }
        }
        if (!values)
        {
            var anchors = new List<GameObject>();
            if (entry.Objects) anchors.AddRange(entry.Bindings.Where(b => b.Enabled && Editable(b.Target) && b.Target.GetComponent<VRCAvatarDescriptor>() == null).Select(b => b.Target));
            if (entry.Shapes) anchors.AddRange(entry.ShapeMeshes.Where(m => m.Renderer != null && Editable(m.Renderer.gameObject) && (m.Rows.Count == 0 || m.Rows.Any(r => r.Enabled))).Select(m => m.Renderer.gameObject));
            if (entry.Materials) anchors.AddRange(entry.MaterialMeshes.Where(m => m.Renderer != null && Editable(m.Renderer.gameObject) && (m.Rows.Count == 0 || m.Rows.Any(r => r.Enabled))).Select(m => m.Renderer.gameObject));
            copy.PlacementTargets = anchors.Distinct().ToList();
        }
        return copy;
    }
    static string PlacementIssue(ZincatMaEntry entry)
    {
        var targets = entry.Targets.ToArray();
        if (targets.Length > 0 && (targets.Select(t => t.scene.handle).Distinct().Count() != 1 || targets.Select(Owner).Distinct().Count() != 1)) return entry.Name + "：本次生成的目标跨场景或角色，请拆分卡片。草稿会保留。";
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && targets.Any(t => t.scene != stage.scene || t == stage.prefabContentsRoot)) return entry.Name + "：生成位置需要位于当前 Prefab 的子级层级。";
        return null;
    }
    void Generate()
    {
        string error = Validate(); if (error != null) { result = error; resultError = true; return; }
        var counts = CountAllDrafts(); bool emptyOnly = false;
        int removedOptions = intMode ? groups.Where(g => g.SourceMenu != null).Sum(g => g.KnownOptions.Count(o => o != null && !g.Values.Any(e => e.SourceObject == o))) : 0;
        if (counts.Pending > 0 || counts.Disabled > 0 || removedOptions > 0)
        {
            int choice = EditorUtility.DisplayDialogComplex("生成前检查", counts.Ready + " 项已完成\n" + counts.Pending + " 项未完成\n" + counts.Disabled + " 项未勾选参与生成\n" + removedOptions + " 个已移除菜单选项将在写回时删除（可撤销）\n\n“生成已完成项”只写入已完成且勾选的配置；“仅生成空组件”不写入动作配置。已读取菜单的空组件模式会保留现有动作，仅新增选项留空。两种方式都会保留当前草稿。", "生成已完成项", "返回编辑", "仅生成空组件");
            if (choice == 1) return; emptyOnly = choice == 2;
        }
        var source = Entries().ToArray();
        var exports = source.ToDictionary(e => e, e => ExportDraft(e, !emptyOnly));
        foreach (var copy in exports.Values) { error = PlacementIssue(copy); if (error != null) { result = error; resultError = true; return; } }
        if (intMode)
            foreach (var group in groups.Where(g => g.SourceMenu != null || !string.IsNullOrEmpty(g.SourceParameter)))
            {
                error = ValidateImportedGroup(group); if (error != null) { activeGroupKey = group.AutoParameter; result = error; resultError = true; return; }
                foreach (var copy in group.Values.Select(e => exports[e]))
                    if (copy.Targets.Any(t => t.scene != group.SourceMenu.scene || Owner(t) != Owner(group.SourceContainer))) { result = group.Name + "：新配置需要属于已读取菜单的同一场景、同一角色。草稿会保留。"; resultError = true; return; }
            }
        bool hiddenDraft = source.Any(e => (!e.Objects && e.Bindings.Count > 0) || (!e.Shapes && e.ShapeMeshes.Count > 0) || (!e.Materials && e.MaterialMeshes.Count > 0));
        bool keep = ZincatMaDraftRules.KeepDraft(emptyOnly, counts.Pending, counts.Disabled, hiddenDraft) || (intMode && groups.Any(g => g.SourceMenu != null));
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        GameObject parent = stage != null ? stage.prefabContentsRoot : null; Scene scene = stage != null ? stage.scene : SceneManager.GetActiveScene();
        Undo.IncrementCurrentGroup(); int undo = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("生成 MA 组件与参数");
        var created = new List<GameObject>();
        int optionCount = source.Length; int updatedMenus = 0;
        var linkedGroups = new List<ZincatMaGroup>();
        try
        {
            if (intMode) Remember("记录 Int 菜单写回关联");
            if (intMode)
                foreach (var group in groups)
                {
                    if (group.SourceMenu != null)
                    {
                        created.Add(WriteImportedGroup(group, exports, emptyOnly)); linkedGroups.Add(group); updatedMenus++; continue;
                    }
                    var values = group.Values.Select(e => exports[e]).ToArray();
                    var anchor = values.SelectMany(e => e.Targets).FirstOrDefault();
                    var batches = values.Select(e => new { Entry = e, Anchor = e.Targets.FirstOrDefault() ?? anchor })
                        .GroupBy(row => new { Scene = row.Anchor != null ? row.Anchor.scene.handle : scene.handle, Avatar = Owner(row.Anchor) }).Select(batch => batch.Select(row => row.Entry).ToArray()).ToList();
                    if (batches.Count == 0) batches.Add(Array.Empty<ZincatMaEntry>());
                    foreach (var batch in batches)
                    {
                        var menu = CreatePlaced(group.Name.Trim(), batch.SelectMany(e => e.Targets), scene, parent);
                        var item = Undo.AddComponent<ModularAvatarMenuItem>(menu); item.label = group.Name.Trim(); item.MenuSource = SubmenuSource.Children;
                        item.Control = new VRCExpressionsMenu.Control { type = VRCExpressionsMenu.Control.ControlType.SubMenu };
                        Undo.AddComponent<ModularAvatarMenuInstaller>(menu); DeclareParameter(menu, group.EffectiveParameter, ParameterSyncType.Int);
                        var marker = MarkMenu(menu);
                        var linked = new ZincatMaGroup { Name = group.Name, Parameter = group.Parameter, AutoParameter = group.AutoParameter, SourceMenu = menu, SourceContainer = menu, SourceParameter = group.EffectiveParameter };
                        foreach (var copy in batch)
                        {
                            var obj = CreateSwitch(copy, menu.scene, menu, group.EffectiveParameter, true); MarkOption(obj, menu, marker);
                            var original = group.Values.First(e => ReferenceEquals(exports[e], copy)); original.SourceObject = obj; original.SourceFingerprint = EntryFingerprint(ReadIntOption(obj)); linked.Values.Add(original); linked.KnownOptions.Add(obj);
                        }
                        SyncIntMenuIcon(menu, linked.KnownOptions);
                        linkedGroups.Add(linked); created.Add(menu);
                    }
                }
            else foreach (var entry in exports.Values)
            {
                var obj = CreateSwitch(entry, scene, parent, entry.EffectiveParameter, false);
                DeclareParameter(obj, entry.EffectiveParameter, ParameterSyncType.Bool); Undo.AddComponent<ModularAvatarMenuInstaller>(obj); created.Add(obj);
            }
            if (intMode && keep) groups = linkedGroups;
            if (!keep)
            {
                Remember("完成 MA 生成"); scroll = null;
                if (intMode) { groups.Clear(); groupScroll = Vector2.zero; } else { switches.Clear(); switchScroll = Vector2.zero; }
            }
            foreach (var createdScene in created.Select(o => o.scene).Distinct()) EditorSceneManager.MarkSceneDirty(createdScene);
            Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(undo);
            Selection.objects = created.Cast<UnityEngine.Object>().ToArray(); if (created.Count > 0) EditorGUIUtility.PingObject(created[0]);
            result = (updatedMenus > 0 ? "已写回 " + updatedMenus + " 个原菜单，处理 " : "已生成 ") + optionCount + " 个选项/开关" + (emptyOnly ? "，新增动作留空" : "，配置已按生成前检查写入") + (keep ? (intMode ? "；草稿已保留，可继续添加并写回同一菜单。" : "；草稿已保留，再次生成会新建菜单。") : "。") + "Ctrl+Z 可撤销。"; resultError = false;
        }
        catch (Exception ex) { Undo.RevertAllDownToGroup(undo); result = "生成失败：" + ex.Message; resultError = true; }
    }

    static GameObject Create(string name, Scene scene, GameObject parent)
    {
        var obj = new GameObject(name); Undo.RegisterCreatedObjectUndo(obj, "生成 MA 组件"); SceneManager.MoveGameObjectToScene(obj, scene);
        if (parent != null) obj.transform.SetParent(parent.transform, false);
        return obj;
    }
    static VRCAvatarDescriptor Owner(GameObject obj) => obj != null ? obj.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
    static GameObject CreatePlaced(string name, IEnumerable<GameObject> objects, Scene fallbackScene, GameObject fallbackParent)
    {
        var targets = objects.Distinct().ToArray();
        if (targets.Length == 0) return Create(name, fallbackScene, fallbackParent);
        var parent = targets[0].transform.parent;
        while (parent != null && targets.Any(t => t.transform == parent || !t.transform.IsChildOf(parent))) parent = parent.parent;
        var obj = Create(name, targets[0].scene, parent != null ? parent.gameObject : null);
        int index = targets.Select(t => { var branch = t.transform; while (branch.parent != parent) branch = branch.parent; return branch.GetSiblingIndex(); }).Min();
        obj.transform.SetSiblingIndex(index); PrefabUtility.RecordPrefabInstancePropertyModifications(obj.transform); return obj;
    }
    static AvatarObjectReference Reference(GameObject target)
    {
        var reference = new AvatarObjectReference(target);
        if (string.IsNullOrEmpty(reference.referencePath)) reference.referencePath = target.name;
        return reference;
    }
    static GameObject CreateSwitch(ZincatMaEntry entry, Scene scene, GameObject parent, string parameter, bool useInt)
    {
        var obj = useInt ? Create(entry.Name.Trim(), scene, parent) : CreatePlaced(entry.Name.Trim(), entry.Targets, scene, parent);
        ConfigureSwitch(obj, entry, parameter, useInt, true); return obj;
    }
    void LoadIntMenu(GameObject selected, bool reload = false)
    {
        var marker = selected != null ? selected.GetComponent<ZincatSwitchMarker>() : null;
        var menu = marker != null && !marker.IsGroup && marker.GroupRoot != null ? marker.GroupRoot : selected;
        if (!Editable(menu)) throw new InvalidOperationException("请拖入场景或 Prefab 编辑中的 Int 菜单物件。");
        var menuItem = menu.GetComponent<ModularAvatarMenuItem>();
        if (menuItem == null || menuItem.Control == null || menuItem.Control.type != VRCExpressionsMenu.Control.ControlType.SubMenu || menuItem.MenuSource != SubmenuSource.Children)
            throw new InvalidOperationException("请拖入使用子级作为菜单来源的 MA Int 子菜单。");
        var container = MenuChildSource(menuItem);
        if (!Editable(container) || container.scene != menu.scene) throw new InvalidOperationException("菜单的子级来源无效，请在 MA Inspector 中检查。");
        if (!reload && groups.Any(g => g.SourceMenu == menu)) { result = "这个菜单已经读取，请在对应参数组里继续编辑。"; resultError = false; return; }
        var options = container.transform.Cast<Transform>().Select(t => t.gameObject).Select(o => new { Object = o, Item = o.GetComponent<ModularAvatarMenuItem>() })
            .Where(x => x.Item != null && x.Item.Control != null && x.Item.Control.type == VRCExpressionsMenu.Control.ControlType.Toggle).ToArray();
        var declared = menu.GetComponent<ModularAvatarParameters>();
        var intParameters = declared != null ? declared.parameters.Where(p => p.syncType == ParameterSyncType.Int && !p.isPrefix && !string.IsNullOrWhiteSpace(p.nameOrPrefix)).Select(p => p.nameOrPrefix).Distinct().ToArray() : Array.Empty<string>();
        var used = options.Select(x => x.Item.Control.parameter?.name).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
        string parameter = used.Length == 1 && (intParameters.Contains(used[0]) || (marker != null && marker.Mode == SwitchMode.Int)) ? used[0] : intParameters.Length == 1 ? intParameters[0] : null;
        if (string.IsNullOrEmpty(parameter)) throw new InvalidOperationException("无法确定这个菜单的共享 Int 参数，请检查 MA Parameters。兼容的旧菜单也可读取。");
        var matching = options.Where(x => x.Item.Control.parameter?.name == parameter).ToArray();
        foreach (var option in matching)
            if (float.IsNaN(option.Item.Control.value) || float.IsInfinity(option.Item.Control.value) || option.Item.Control.value < 0 || option.Item.Control.value > 255 || Mathf.Abs(option.Item.Control.value - Mathf.Round(option.Item.Control.value)) > .001f)
                throw new InvalidOperationException("菜单包含非 0–255 整数选项，请先在 MA Inspector 中检查。");
        var group = new ZincatMaGroup { Name = string.IsNullOrEmpty(menuItem.label) ? menu.name : menuItem.label, Parameter = parameter, SourceParameter = parameter, SourceMenu = menu, SourceContainer = container };
        foreach (var option in matching)
        {
            var entry = ReadIntOption(option.Object); entry.Expanded = true;
            entry.SourceFingerprint = EntryFingerprint(entry); group.Values.Add(entry); group.KnownOptions.Add(option.Object);
        }
        Remember("读取 Int 菜单"); activeGroupKey = group.AutoParameter; int existing = groups.FindIndex(g => g.SourceMenu == menu); if (reload && existing >= 0) groups[existing] = group; else groups.Add(group);
        result = "已读取 “" + group.Name + "” 的 " + group.Values.Count + " 个选项。添加新对象后，点击“写回已读取菜单”；现有菜单位置和未编辑选项会保留。"; resultError = false;
    }
    static ZincatMaEntry ReadIntOption(GameObject obj)
    {
        var item = obj.GetComponent<ModularAvatarMenuItem>();
        var entry = new ZincatMaEntry { Name = string.IsNullOrEmpty(item.label) ? obj.name : item.label, Value = Mathf.RoundToInt(item.Control.value), Icon = item.Control.icon, SourceObject = obj, Objects = false };
        var toggle = obj.GetComponent<ModularAvatarObjectToggle>();
        if (toggle != null)
        {
            entry.Objects = true;
            for (int i = 0; i < toggle.Objects.Count; i++)
            {
                var row = toggle.Objects[i]; entry.Bindings.Add(new ZincatMaBinding { Target = ResolveReference(row.Object, toggle, "m_objects", i), Active = row.Active });
            }
        }
        var shapes = obj.GetComponent<ModularAvatarShapeChanger>();
        if (shapes != null)
        {
            entry.Shapes = true;
            for (int i = 0; i < shapes.Shapes.Count; i++)
            {
                var row = shapes.Shapes[i]; var target = ResolveReference(row.Object, shapes, "m_shapes", i);
                var renderer = target != null ? target.GetComponent<SkinnedMeshRenderer>() : null;
                GetImportedMesh(entry.ShapeMeshes, renderer).Rows.Add(new ZincatMaMeshSetting { Shape = row.ShapeName, Weight = row.Value, WeightSet = !float.IsNaN(row.Value) && !float.IsInfinity(row.Value), DeleteShape = row.ChangeType == ShapeChangeType.Delete });
            }
        }
        var materials = obj.GetComponent<ModularAvatarMaterialSetter>();
        if (materials != null)
        {
            entry.Materials = true;
            for (int i = 0; i < materials.Objects.Count; i++)
            {
                var row = materials.Objects[i]; var target = ResolveReference(row.Object, materials, "m_objects", i);
                var renderer = target != null ? target.GetComponent<Renderer>() : null;
                GetImportedMesh(entry.MaterialMeshes, renderer).Rows.Add(new ZincatMaMeshSetting { Slot = row.MaterialIndex, Material = row.Material, AllowNullMaterial = row.Material == null });
            }
        }
        ZincatMaPhotoWindow.RestoreSource(entry); return entry;
    }
    static GameObject ResolveReference(AvatarObjectReference reference, Component owner, string collection, int index)
    {
        if (reference == null) return null;
        var resolved = reference.Get(owner); if (resolved != null) return resolved;
        // Direct references are retained when a menu is being authored outside an avatar.
        var serialized = new SerializedObject(owner); var rows = serialized.FindProperty(collection);
        if (rows == null || index < 0 || index >= rows.arraySize) return null;
        return rows.GetArrayElementAtIndex(index).FindPropertyRelative("Object")?.FindPropertyRelative("targetObject")?.objectReferenceValue as GameObject;
    }
    static ZincatMaMesh GetImportedMesh(List<ZincatMaMesh> meshes, Renderer renderer)
    {
        var mesh = meshes.FirstOrDefault(m => m.Renderer == renderer);
        if (mesh == null) { mesh = new ZincatMaMesh { Renderer = renderer }; meshes.Add(mesh); } return mesh;
    }
    static string EntryFingerprint(ZincatMaEntry entry)
    {
        var text = new System.Text.StringBuilder();
        Action<string> add = value => { value = value ?? ""; text.Append(value.Length).Append(':').Append(value).Append(';'); };
        Func<UnityEngine.Object, string> id = obj => obj != null ? obj.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "0";
        add(entry.Name?.Trim()); add(entry.Value.ToString(CultureInfo.InvariantCulture)); add(id(entry.Icon));
        add(entry.Objects.ToString()); add(entry.Shapes.ToString()); add(entry.Materials.ToString());
        foreach (var row in entry.Bindings) { add(id(row.Target)); add(row.Active.ToString()); add(row.Enabled.ToString()); }
        foreach (var list in new[] { entry.ShapeMeshes, entry.MaterialMeshes })
        {
            add("component");
            foreach (var mesh in list)
            {
                add(id(mesh.Renderer)); add("mesh");
                foreach (var row in mesh.Rows) { add(row.Shape); add(row.Slot.ToString(CultureInfo.InvariantCulture)); add(row.Weight.ToString("R", CultureInfo.InvariantCulture)); add(row.WeightSet.ToString()); add(id(row.Material)); add(row.Enabled.ToString()); add(row.DeleteShape.ToString()); add(row.AllowNullMaterial.ToString()); }
            }
        }
        return text.ToString();
    }
    static GameObject MenuChildSource(ModularAvatarMenuItem item) => item.menuSource_otherObjectChildren != null ? item.menuSource_otherObjectChildren : item.gameObject;
    string ValidateImportedGroup(ZincatMaGroup group)
    {
        if (!Editable(group.SourceMenu) || !Editable(group.SourceContainer) || group.SourceContainer.scene != group.SourceMenu.scene) return "已读取菜单或其子级来源已失效，请重新读取。";
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && group.SourceMenu.scene != stage.scene) return "请在菜单所属场景或 Prefab 中写回。";
        var menuItem = group.SourceMenu.GetComponent<ModularAvatarMenuItem>();
        if (menuItem == null || menuItem.Control == null) return group.Name + "：原物件缺少有效的 MA Menu Item，请重新读取。";
        if (menuItem.Control.type != VRCExpressionsMenu.Control.ControlType.SubMenu) return group.Name + "：原物件已改为非子菜单类型，请在 MA 中恢复子菜单后重新读取。";
        if (menuItem.MenuSource != SubmenuSource.Children) return group.Name + "：菜单来源已改为菜单资源，请在 MA 中改回使用子级后重新读取。";
        if (MenuChildSource(menuItem) != group.SourceContainer) return group.Name + "：子级来源物件已改变，请重新读取。当前草稿保留。";
        var declaration = group.SourceMenu.GetComponent<ModularAvatarParameters>();
        if (group.EffectiveParameter != group.SourceParameter && declaration != null && declaration.parameters.Any(p => !p.isPrefix && p.nameOrPrefix == group.EffectiveParameter)) return "新参数名已在原菜单中使用，请换一个名称。";
        if (group.SourceContainer.transform.Cast<Transform>().Select(t => t.gameObject).Except(group.KnownOptions).Any(obj => { var item = obj.GetComponent<ModularAvatarMenuItem>(); return item != null && item.Control != null && item.Control.type == VRCExpressionsMenu.Control.ControlType.Toggle && item.Control.parameter?.name == group.SourceParameter; })) return "原菜单中已添加新选项，请重新读取后继续编辑，避免 Int 值重复。当前草稿会保留。";
        foreach (var obj in group.KnownOptions.Where(o => o != null))
        {
            if (obj.transform.parent != group.SourceContainer.transform) return "原菜单选项已被移动，请重新读取，避免改动其他物件。";
            var item = obj.GetComponent<ModularAvatarMenuItem>();
            if (item == null || item.Control == null || item.Control.type != VRCExpressionsMenu.Control.ControlType.Toggle || item.Control.parameter?.name != group.SourceParameter) return "原菜单选项的类型或参数已变化，请重新读取。";
        }
        if (group.Values.Any(e => !string.IsNullOrEmpty(e.SourceFingerprint) && e.SourceObject == null)) return "原菜单选项已删除，请重新读取。当前草稿会保留。";
        foreach (var entry in group.Values.Where(e => e.SourceObject != null))
        {
            if (!group.KnownOptions.Contains(entry.SourceObject)) return "菜单选项的归属已变化，请重新读取。";
            if (EntryFingerprint(ReadIntOption(entry.SourceObject)) != entry.SourceFingerprint) return "“" + entry.Name + "” 已在原生 Inspector 中修改，请重新读取再写回。当前草稿会保留。";
        }
        return null;
    }
    static string ComponentFingerprint(ZincatMaEntry entry, int kind)
    {
        var action = new ZincatMaEntry { Objects = kind == 0 && entry.Objects, Shapes = kind == 1 && entry.Shapes, Materials = kind == 2 && entry.Materials };
        if (action.Objects) action.Bindings = entry.Bindings;
        if (action.Shapes) action.ShapeMeshes = entry.ShapeMeshes;
        if (action.Materials) action.MaterialMeshes = entry.MaterialMeshes;
        return EntryFingerprint(action);
    }
    static void DirtyNative(UnityEngine.Object obj)
    {
        EditorUtility.SetDirty(obj); PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
    }
    static ZincatSwitchMarker MarkMenu(GameObject menu)
    {
        var marker = menu.GetComponent<ZincatSwitchMarker>();
        if (marker == null) marker = Undo.AddComponent<ZincatSwitchMarker>(menu); else Undo.RecordObject(marker, "标记 Int 菜单");
        if (string.IsNullOrEmpty(marker.Id)) marker.Id = Guid.NewGuid().ToString("N");
        marker.Mode = SwitchMode.Int; marker.IsGroup = true; marker.GroupId = marker.Id; marker.GroupRoot = menu; DirtyNative(marker); return marker;
    }
    static void MarkOption(GameObject option, GameObject menu, ZincatSwitchMarker group)
    {
        var marker = option.GetComponent<ZincatSwitchMarker>();
        if (marker == null) marker = Undo.AddComponent<ZincatSwitchMarker>(option); else Undo.RecordObject(marker, "标记 Int 选项");
        if (string.IsNullOrEmpty(marker.Id)) marker.Id = Guid.NewGuid().ToString("N");
        marker.Mode = SwitchMode.Int; marker.IsGroup = false; marker.GroupId = group.GroupId; marker.GroupRoot = menu; DirtyNative(marker);
    }
    GameObject WriteImportedGroup(ZincatMaGroup group, Dictionary<ZincatMaEntry, ZincatMaEntry> exports, bool emptyOnly)
    {
        var menu = group.SourceMenu; var marker = MarkMenu(menu);
        Undo.RecordObject(menu, "更新 Int 菜单名称"); menu.name = group.Name.Trim(); DirtyNative(menu);
        var item = menu.GetComponent<ModularAvatarMenuItem>(); Undo.RecordObject(item, "更新 Int 子菜单"); item.label = group.Name.Trim(); DirtyNative(item);
        UpdateIntDeclaration(menu, group.SourceParameter, group.EffectiveParameter);
        var updated = new List<GameObject>();
        foreach (var entry in group.Values)
        {
            var copy = exports[entry]; var obj = entry.SourceObject;
            if (obj == null) obj = CreateSwitch(copy, menu.scene, group.SourceContainer, group.EffectiveParameter, true);
            else
            {
                var previous = ReadIntOption(obj);
                ConfigureMenuItem(obj, entry, group.EffectiveParameter, true, false);
                if (!emptyOnly)
                    for (int kind = 0; kind < 3; kind++)
                        if (ComponentFingerprint(entry, kind) != ComponentFingerprint(previous, kind)) ConfigureAction(obj, copy, kind);
            }
            MarkOption(obj, menu, marker); updated.Add(obj); entry.SourceObject = obj;
            entry.SourceFingerprint = EntryFingerprint(ReadIntOption(obj));
        }
        foreach (var removed in group.KnownOptions.Where(o => o != null && !updated.Contains(o)).ToArray()) Undo.DestroyObjectImmediate(removed);
        group.KnownOptions = updated; group.SourceParameter = group.EffectiveParameter;
        SyncIntMenuIcon(menu, updated); return menu;
    }
    static void SyncIntMenuIcon(GameObject menu, IEnumerable<GameObject> children)
    {
        var root = menu.GetComponent<ModularAvatarMenuItem>();
        if (root == null || root.Control == null || root.Control.type != VRCExpressionsMenu.Control.ControlType.SubMenu) return;
        var options = children.Where(o => o != null).Select(o => o.GetComponent<ModularAvatarMenuItem>())
            .Where(i => i != null && i.Control != null && i.Control.type == VRCExpressionsMenu.Control.ControlType.Toggle && i.Control.icon != null && (i.Control.value == 0 || i.Control.value == 1)).ToArray();
        var value = ZincatMaDraftRules.IntCoverValue(options.Select(i => (int)i.Control.value));
        var icon = value.HasValue ? options.First(i => i.Control.value == value.Value).Control.icon : null;
        if (root.Control.icon == icon) return;
        Undo.RecordObject(root, "同步 Int 总菜单图标"); root.Control.icon = icon; DirtyNative(root);
    }
    static void UpdateIntDeclaration(GameObject menu, string oldParameter, string parameter)
    {
        var declaration = menu.GetComponent<ModularAvatarParameters>();
        if (declaration == null) { DeclareParameter(menu, parameter, ParameterSyncType.Int); return; }
        Undo.RecordObject(declaration, "更新 Int 参数名");
        var list = declaration.parameters.ToList();
        int index = list.FindIndex(p => p.nameOrPrefix == oldParameter && !p.isPrefix);
        if (index >= 0) { var config = list[index]; config.nameOrPrefix = parameter; config.syncType = ParameterSyncType.Int; list[index] = config; }
        else if (!list.Any(p => p.nameOrPrefix == parameter)) list.Add(new ParameterConfig { nameOrPrefix = parameter, syncType = ParameterSyncType.Int, defaultValue = 0, hasExplicitDefaultValue = true, saved = true });
        declaration.parameters = list; DirtyNative(declaration);
    }
    static void ConfigureMenuItem(GameObject obj, ZincatMaEntry entry, string parameter, bool useInt, bool created)
    {
        if (obj.name != entry.Name.Trim()) { Undo.RecordObject(obj, "更新菜单项名称"); obj.name = entry.Name.Trim(); DirtyNative(obj); }
        var item = obj.GetComponent<ModularAvatarMenuItem>();
        if (item == null) { item = Undo.AddComponent<ModularAvatarMenuItem>(obj); created = true; }
        else Undo.RecordObject(item, "更新 MA 菜单项");
        item.label = entry.Name.Trim();
        if (created || item.Control == null || item.Control.value != (useInt ? entry.Value : 1)) item.automaticValue = false;
        if (created) { item.isSaved = true; item.isSynced = true; item.isDefault = useInt && entry.Value == 0; }
        if (item.Control == null) item.Control = new VRCExpressionsMenu.Control();
        item.Control.type = VRCExpressionsMenu.Control.ControlType.Toggle; item.Control.icon = entry.Icon;
        item.Control.value = useInt ? entry.Value : 1; item.Control.parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter }; DirtyNative(item);
    }
    static T GetConfiguredComponent<T>(GameObject obj, bool enabled) where T : Component
    {
        var component = obj.GetComponent<T>();
        if (!enabled) { if (component != null) Undo.DestroyObjectImmediate(component); return null; }
        if (component == null) component = Undo.AddComponent<T>(obj); else Undo.RecordObject(component, "更新 MA 组件配置"); return component;
    }
    static void ConfigureSwitch(GameObject obj, ZincatMaEntry entry, string parameter, bool useInt, bool created)
    {
        ConfigureMenuItem(obj, entry, parameter, useInt, created);
        for (int kind = 0; kind < 3; kind++) ConfigureAction(obj, entry, kind);
    }
    static void ConfigureAction(GameObject obj, ZincatMaEntry entry, int kind)
    {
        if (kind == 0)
        {
            var toggle = GetConfiguredComponent<ModularAvatarObjectToggle>(obj, entry.Objects);
            if (toggle != null) { toggle.Objects = entry.Bindings.Select(b => new ToggledObject { Object = Reference(b.Target), Active = b.Active }).ToList(); DirtyNative(toggle); }
        }
        else if (kind == 1)
        {
            var shapes = GetConfiguredComponent<ModularAvatarShapeChanger>(obj, entry.Shapes);
            if (shapes != null) { shapes.Shapes = entry.ShapeMeshes.SelectMany(m => m.Rows.Select(r => new ChangedShape { Object = Reference(m.Renderer.gameObject), ShapeName = r.Shape, ChangeType = r.DeleteShape ? ShapeChangeType.Delete : ShapeChangeType.Set, Value = r.Weight })).ToList(); DirtyNative(shapes); }
        }
        else
        {
            var materials = GetConfiguredComponent<ModularAvatarMaterialSetter>(obj, entry.Materials);
            if (materials != null) { materials.Objects = entry.MaterialMeshes.SelectMany(m => m.Rows.Select(r => new MaterialSwitchObject { Object = Reference(m.Renderer.gameObject), MaterialIndex = r.Slot, Material = r.Material })).ToList(); DirtyNative(materials); }
        }
    }

    static void DeclareParameter(GameObject obj, string name, ParameterSyncType type)
    {
        var parameters = Undo.AddComponent<ModularAvatarParameters>(obj);
        parameters.parameters = new List<ParameterConfig> { new ParameterConfig { nameOrPrefix = name, syncType = type, defaultValue = 0, hasExplicitDefaultValue = true, saved = true } };
    }
}

[Serializable]
internal class ZincatMaEntry
{
    public GameObject SourceObject;
    public string SourceFingerprint;
    public bool Expanded = true;
    public bool IconExpanded;
    public Texture2D Icon;
    public ZincatMaIconSource IconSource;
    public List<GameObject> IconTargets = new List<GameObject>();
    public Texture2D IconInput;
    public string IconInputPath;
    public string Name = "新开关";
    public string Parameter = "";
    public string AutoParameter = "ZincatSwitch_" + Guid.NewGuid().ToString("N");
    public int Value = 1;
    public bool Objects = true, Shapes, Materials;
    public bool ObjectsExpanded = true, ShapesExpanded = true, MaterialsExpanded = true;
    [NonSerialized] public List<GameObject> PlacementTargets;
    public List<ZincatMaBinding> Bindings = new List<ZincatMaBinding>();
    public List<ZincatMaMesh> ShapeMeshes = new List<ZincatMaMesh>();
    public List<ZincatMaMesh> MaterialMeshes = new List<ZincatMaMesh>();
    internal IEnumerable<GameObject> Targets => (IEnumerable<GameObject>)PlacementTargets ?? (Objects ? Bindings.Where(b => b.Enabled).Select(b => b.Target) : Enumerable.Empty<GameObject>())
        .Concat(Shapes ? ShapeMeshes.Where(m => m.Renderer != null && (m.Rows.Count == 0 || m.Rows.Any(r => r.Enabled))).Select(m => m.Renderer.gameObject) : Enumerable.Empty<GameObject>())
        .Concat(Materials ? MaterialMeshes.Where(m => m.Renderer != null && (m.Rows.Count == 0 || m.Rows.Any(r => r.Enabled))).Select(m => m.Renderer.gameObject) : Enumerable.Empty<GameObject>()).Where(t => t != null);
    internal string EffectiveParameter => string.IsNullOrWhiteSpace(Parameter) ? AutoParameter : Parameter.Trim();
}

internal enum ZincatMaIconSource { Targets, SceneObjects, Image }

[Serializable]
internal class ZincatMaBinding
{
    public bool Enabled = true;
    public GameObject Target;
    public bool Active;
}

[Serializable]
internal class ZincatMaGroup
{
    public bool SettingsExpanded;
    public GameObject SourceMenu, SourceContainer;
    public string SourceParameter;
    public List<GameObject> KnownOptions = new List<GameObject>();
    public string Name = "新菜单";
    public string Parameter = "";
    public string AutoParameter = "ZincatSwitch_" + Guid.NewGuid().ToString("N");
    public List<ZincatMaEntry> Values = new List<ZincatMaEntry>();
    internal string EffectiveParameter => string.IsNullOrWhiteSpace(Parameter) ? AutoParameter : Parameter.Trim();
}

[Serializable]
internal class ZincatMaMesh
{
    public bool Expanded = true;
    public Renderer Renderer;
    public List<ZincatMaMeshSetting> Rows = new List<ZincatMaMeshSetting>();
}

[Serializable]
internal class ZincatMaMeshSetting
{
    public bool Enabled = true;
    public string Shape;
    public int Slot = -1;
    public float Weight;
    public bool WeightSet;
    public string WeightText;
    public bool DeleteShape, AllowNullMaterial;
    public Material Material;
}

internal sealed class ZincatRowView
{
    internal ZincatMaEntry Entry;
    internal Action Update;
}

internal sealed class ZincatMaDraftCount
{
    internal int Ready, Pending, Disabled;
    internal void Add(bool enabled, string issue)
    { if (!enabled) Disabled++; else if (issue == null) Ready++; else Pending++; }
}

internal static class ZincatMaDraftRules
{
    internal static int? IntCoverValue(IEnumerable<int> values)
    {
        bool hasOne = false;
        foreach (int value in values) { if (value == 0) return 0; if (value == 1) hasOne = true; }
        return hasOne ? (int?)1 : null;
    }
    internal static Vector2Int FitIconSize(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("图片尺寸必须大于 0。");
        double scale = 256.0 / Math.Max(width, height);
        return new Vector2Int(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
    internal static string IntValueIssue(int value, IEnumerable<int> otherValues) => value < 0 || value > 255 ? "Int 值须在 0–255 之间。" : otherValues.Contains(value) ? "同组 Int 值重复，请换一个未占用的值。" : null;
    internal static void SetWeightText(ZincatMaMeshSetting row, string text)
    {
        row.WeightText = text ?? "";
        float value;
        row.WeightSet = (float.TryParse(row.WeightText, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || float.TryParse(row.WeightText, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && !float.IsNaN(value) && !float.IsInfinity(value);
        if (row.WeightSet) row.Weight = value;
    }
    internal static int? NextIntValue(IEnumerable<int> used)
    {
        var taken = new HashSet<int>(used);
        foreach (int value in Enumerable.Range(1, 255).Concat(new[] { 0 })) if (!taken.Contains(value)) return value;
        return null;
    }
    internal static void AppendSelection(List<ZincatMaMeshSetting> rows, string[] choices, IEnumerable<int> selected, bool shapeMode)
    {
        foreach (int index in selected.Distinct().OrderBy(i => i))
        {
            if (index < 0 || index >= choices.Length) continue;
            if (rows.Any(r => shapeMode ? r.Shape == choices[index] : r.Slot == index)) continue;
            rows.Add(new ZincatMaMeshSetting { Shape = shapeMode ? choices[index] : null, Slot = shapeMode ? -1 : index });
        }
    }
    internal static List<ZincatMaMeshSetting> CopyRows(IEnumerable<ZincatMaMeshSetting> rows, bool values, Func<ZincatMaMeshSetting, string> issue)
    {
        if (!values) return new List<ZincatMaMeshSetting>();
        return rows.Where(r => r.Enabled && issue(r) == null).Select(r => new ZincatMaMeshSetting { Enabled = r.Enabled, Shape = r.Shape, Slot = r.Slot, Weight = r.Weight, WeightSet = r.WeightSet, WeightText = r.WeightText, DeleteShape = r.DeleteShape, AllowNullMaterial = r.AllowNullMaterial, Material = r.Material }).ToList();
    }
    internal static List<ZincatMaBinding> CopyBindings(IEnumerable<ZincatMaBinding> bindings, bool values, Func<ZincatMaBinding, string> issue)
    {
        if (!values) return new List<ZincatMaBinding>();
        return bindings.Where(b => b.Enabled && issue(b) == null).Select(b => new ZincatMaBinding { Enabled = b.Enabled, Target = b.Target, Active = b.Active }).ToList();
    }
    internal static bool KeepDraft(bool emptyOnly, int pending, int disabled, bool hiddenDraft) => emptyOnly || pending > 0 || disabled > 0 || hiddenDraft;
}

internal sealed class ZincatMeshSelectionPopup : PopupWindowContent
{
    readonly string[] choices;
    readonly HashSet<int> selected;
    readonly HashSet<int> existing;
    readonly Action<HashSet<int>> apply;
    Vector2 scroll; string search = "";
    internal ZincatMeshSelectionPopup(string[] choices, IEnumerable<int> selected, Action<HashSet<int>> apply)
    { this.choices = choices; this.existing = new HashSet<int>(selected); this.selected = new HashSet<int>(); this.apply = apply; }
    public override Vector2 GetWindowSize() => new Vector2(340, 360);
    public override void OnGUI(Rect rect)
    {
        EditorGUILayout.LabelField("勾选要追加的新项目", EditorStyles.boldLabel);
        search = EditorGUILayout.TextField("搜索", search);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("全选")) for (int i = 0; i < choices.Length; i++) if (!existing.Contains(i)) selected.Add(i);
        if (GUILayout.Button("清空选择")) selected.Clear();
        EditorGUILayout.EndHorizontal();
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(225));
        for (int i = 0; i < choices.Length; i++)
        {
            if (search.Length > 0 && choices[i].IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            using (new EditorGUI.DisabledScope(existing.Contains(i)))
            {
                bool value = EditorGUILayout.ToggleLeft(choices[i] + (existing.Contains(i) ? " · 已添加" : ""), existing.Contains(i) || selected.Contains(i));
                if (!existing.Contains(i)) { if (value) selected.Add(i); else selected.Remove(i); }
            }
        }
        if (choices.Length == 0) EditorGUILayout.LabelField("这个网格没有可选项目。");
        EditorGUILayout.EndScrollView();
        EditorGUILayout.LabelField("追加 " + selected.Count + " 项 · 已有 " + existing.Count + " 项保持原配置");
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("取消")) editorWindow.Close();
        if (GUILayout.Button("追加选中项")) { var picked = new HashSet<int>(selected); editorWindow.Close(); apply(picked); }
        EditorGUILayout.EndHorizontal();
    }
}

internal static class ZincatQuickMenuStyle
{
    internal static Color Surface => EditorGUIUtility.isProSkin ? (Color)new Color32(52, 52, 52, 255) : new Color(.95f, .95f, .96f);
    internal static Color Card => EditorGUIUtility.isProSkin ? (Color)new Color32(58, 58, 58, 255) : Color.white;
    internal static Color Text => EditorGUIUtility.isProSkin ? new Color32(236, 236, 238, 255) : new Color32(36, 36, 38, 255);
    internal static Color Muted => EditorGUIUtility.isProSkin ? new Color32(185, 189, 196, 255) : new Color32(97, 97, 102, 255);
    internal static Color Accent => new Color32(23, 105, 210, 255);
    internal static Color Focus => EditorGUIUtility.isProSkin ? (Color)new Color32(124, 182, 255, 255) : Accent;
    internal static Color Line => EditorGUIUtility.isProSkin ? new Color32(78, 80, 84, 255) : new Color32(193, 195, 201, 255);
    internal static Label Hint(string text, float bottom)
    {
        var label = new Label(text); label.style.whiteSpace = WhiteSpace.Normal; label.style.color = Muted; label.style.fontSize = 12; label.style.marginBottom = bottom; return label;
    }
    internal static void Border(VisualElement e, Color color, float width, float radius)
    {
        e.style.borderLeftWidth = e.style.borderRightWidth = e.style.borderTopWidth = e.style.borderBottomWidth = width;
        e.style.borderLeftColor = e.style.borderRightColor = e.style.borderTopColor = e.style.borderBottomColor = color;
        e.style.borderTopLeftRadius = e.style.borderTopRightRadius = e.style.borderBottomLeftRadius = e.style.borderBottomRightRadius = radius;
    }
    internal static void Apply(VisualElement root)
    {
        root.style.fontSize = 12; root.style.color = Text; root.style.unityFontStyleAndWeight = FontStyle.Normal;
        root.Query<Button>().ForEach(b =>
        {
            bool primary = b.name == "quick-primary-action";
            bool selected = b.name == "quick-mode-selected";
            Color idle = primary ? Accent : EditorGUIUtility.isProSkin ? (Color)new Color32(72, 72, 74, 255) : (Color)new Color32(235, 235, 239, 255);
            Color hover = primary ? new Color32(30, 114, 220, 255) : EditorGUIUtility.isProSkin ? new Color32(84, 84, 87, 255) : new Color32(223, 225, 230, 255);
            b.style.minHeight = 28; b.style.flexShrink = 0; b.style.color = primary ? Color.white : Text; b.style.backgroundColor = idle;
            if (selected) b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.paddingLeft = b.style.paddingRight = b.name == "quick-icon-button" ? 2 : 8; Border(b, primary || selected ? Accent : Line, 1, 6);
            b.RegisterCallback<MouseEnterEvent>(_ => b.style.backgroundColor = hover);
            b.RegisterCallback<MouseLeaveEvent>(_ => b.style.backgroundColor = idle);
            b.RegisterCallback<FocusInEvent>(_ => Border(b, Focus, 1, 6));
            b.RegisterCallback<FocusOutEvent>(_ => Border(b, primary ? Accent : Line, 1, 6));
        });
        root.Query<TextField>().ForEach(f => Field(f, f.Q<VisualElement>(className: "unity-text-input")));
        root.Query<ObjectField>().ForEach(f => Field(f, f.Q<VisualElement>(className: "unity-base-field__input")));
        root.Query<DropdownField>().ForEach(f => Field(f, f.Q<VisualElement>(className: "unity-base-field__input")));
        root.Query<IntegerField>().ForEach(f => Field(f, f.Q<VisualElement>(className: "unity-base-field__input")));
        root.Query<FloatField>().ForEach(f => Field(f, f.Q<VisualElement>(className: "unity-base-field__input")));
        root.Query<PopupField<string>>().ForEach(f => Field(f, f.Q<VisualElement>(className: "unity-base-field__input")));
        root.Query<ObjectField>().ForEach(f =>
        {
            f.Query<Label>().ForEach(l => { l.style.minWidth = 0; l.style.flexShrink = 1; l.style.textOverflow = TextOverflow.Ellipsis; l.style.whiteSpace = WhiteSpace.NoWrap; l.style.overflow = Overflow.Hidden; });
        });
        root.Query<Toggle>().ForEach(t => t.style.minHeight = 24);
    }
    static void Field(VisualElement field, VisualElement input)
    {
        field.style.minWidth = 0; field.style.minHeight = 28; field.style.flexShrink = 1;
        var label = field.Q<Label>(className: "unity-base-field__label");
        if (label != null && !string.IsNullOrEmpty(label.text)) { label.style.minWidth = label.style.width = field.ClassListContains("quick-inline-value") ? 14 : 78; label.style.color = Muted; }
        if (input == null) return;
        input.style.minWidth = 0; input.style.minHeight = 28; input.style.flexShrink = 1; input.style.color = Text; input.style.overflow = Overflow.Hidden;
        input.style.backgroundColor = EditorGUIUtility.isProSkin ? (Color)new Color32(48, 48, 50, 255) : Color.white; Border(input, Line, 1, 4);
        field.RegisterCallback<FocusInEvent>(_ => Border(input, Focus, 1, 4)); field.RegisterCallback<FocusOutEvent>(_ => Border(input, Line, 1, 4));
    }
}
