#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;
using Zincat.Clothing.Editor;
using Object = UnityEngine.Object;

internal sealed class ZincatMaPhotoOption
{
    internal string Key, Name;
    internal bool ManualImage;
    internal Texture2D SourceImage;
    internal string ImagePath;
    internal Action<Texture2D> Apply;
    internal ZincatMaEntry Entry;
    internal GameObject[] Targets;
    internal HashSet<GameObject> ClosedTargets;
}

internal sealed class ZincatMaPhotoWindow : EditorWindow
{
    [Serializable] internal sealed class ViewSettings
    {
        public float pitch = 22, yaw = 335, padding = .1f;
        public bool transparentCompatibility;
    }
    const string PrefsKey = "Zincat.MaMenu.PartPhoto.Settings";
    const string DefaultAngleKey = "Zincat.MaMenu.PartPhoto.DefaultAngle";
    [Serializable] sealed class AngleSlot { public float pitch, yaw; }
    ViewSettings settings;
    static AngleSlot ReadDefaultAngle(ViewSettings fallback)
    {
        var stored = EditorPrefs.GetString(DefaultAngleKey, "");
        try
        {
            if (!string.IsNullOrEmpty(stored))
            {
                var saved = JsonUtility.FromJson<AngleSlot>(stored);
                if (saved != null) return saved;
            }
        }
        catch (ArgumentException) { }
        // The first default uses the previously saved working angle.
        return SaveDefaultAngle(fallback);
    }
    static AngleSlot SaveDefaultAngle(ViewSettings view)
    {
        var saved = new AngleSlot { pitch = view.pitch, yaw = view.yaw };
        EditorPrefs.SetString(DefaultAngleKey, JsonUtility.ToJson(saved)); return saved;
    }
    internal static ViewSettings ReadSettings()
    {
        ViewSettings view;
        try { view = JsonUtility.FromJson<ViewSettings>(EditorPrefs.GetString(PrefsKey, "")) ?? new ViewSettings(); }
        catch { view = new ViewSettings(); }
        var saved = ReadDefaultAngle(view); view.pitch = saved.pitch; view.yaw = saved.yaw; return view;
    }
    [SerializeField] IntegrateMovementTool owner;
    [SerializeField] string active;
    List<ZincatMaPhotoOption> options = new List<ZincatMaPhotoOption>();
    string message;
    Image preview;
    Label notice, previewStatus;
    DropdownField picker;
    Texture2D previewTexture;
    IVisualElementScheduledItem pendingPreview;
    static bool batchBusy;
    internal static void OpenSettings(IntegrateMovementTool generator)
    {
        var window = GetWindow<ZincatMaPhotoWindow>("分件截图设置"); window.minSize = new Vector2(420, 610); window.owner = generator;
        window.InitializeView(); window.Show();
    }
    void OnDisable() { pendingPreview?.Pause(); ReleasePreview(); }
    void ReleasePreview() { if (preview != null) preview.image = null; if (previewTexture) Object.DestroyImmediate(previewTexture); previewTexture = null; }
    public void CreateGUI() { InitializeView(); }
    void InitializeView() { settings = ReadSettings(); Build(); RefreshPreview(); }
    void RefreshOptions()
    {
        if (owner == null) owner = Resources.FindObjectsOfTypeAll<IntegrateMovementTool>().FirstOrDefault();
        options = owner != null ? owner.PhotoOptions() : new List<ZincatMaPhotoOption>();
        if (!options.Any(o => o.Key == active))
            active = (options.FirstOrDefault(o => !o.ManualImage && CanRender(o)) ?? options.FirstOrDefault())?.Key;
        var names = options.Select((o, i) => (i + 1) + ". " + o.Name).ToList();
        int selected = options.FindIndex(o => o.Key == active);
        if (names.Count == 0) names.Add("没有菜单选项");
        if (!picker.choices.SequenceEqual(names)) picker.choices = names;
        picker.SetValueWithoutNotify(names[Math.Max(0, selected)]); picker.SetEnabled(selected >= 0);
    }
    void RefreshPreview() => QueuePreview(true);
    void Run(Action action)
    {
        try { action(); } catch (Exception ex) { message = ex.Message; }
        if (notice != null) notice.text = message ?? "";
    }
    void Build()
    {
        pendingPreview?.Pause(); ReleasePreview(); var root = rootVisualElement; root.Clear();
        root.style.paddingLeft = root.style.paddingRight = 14; root.style.paddingTop = root.style.paddingBottom = 12; root.style.backgroundColor = ZincatQuickMenuStyle.Surface;
        var title = new Label("分件截图设置"); title.style.fontSize = 18; root.Add(title);
        root.Add(ZincatQuickMenuStyle.Hint("调整角度会自动预览，也可手动刷新。拍摄后才会更新图标。", 8));
        var scroll = new ScrollView(); scroll.style.flexGrow = 1; scroll.style.minHeight = 0; root.Add(scroll);
        picker = new DropdownField("预览选项", new List<string> { "没有菜单选项" }, 0); picker.SetEnabled(false);
        picker.RegisterValueChangedCallback(e => { if (picker.index >= 0 && picker.index < options.Count) { active = options[picker.index].Key; RefreshPreview(); } }); scroll.Add(picker);
        var actions = new VisualElement(); actions.style.flexDirection = FlexDirection.Row; actions.style.flexWrap = Wrap.Wrap; actions.style.marginTop = 4;
        actions.Add(new Button(RefreshPreview) { text = "刷新预览", tooltip = "重新读取选项，按当前角度刷新画面" });
        actions.Add(new Button(() => Run(CaptureCurrent)) { text = "拍摄并回填" });
        actions.Add(new Button(() => Run(() => { RefreshOptions(); message = CaptureAll(options, settings); RefreshPreview(); })) { text = "一键全拍", tooltip = "使用当前角度逐项拍摄，跳过没有来源的选项" }); scroll.Add(actions);
        preview = new Image { scaleMode = ScaleMode.ScaleToFit }; preview.style.height = 256; preview.style.flexShrink = 0; preview.style.marginTop = 8; preview.style.marginBottom = 8;
        preview.style.backgroundColor = EditorGUIUtility.isProSkin ? new Color(.16f, .16f, .17f) : new Color(.84f, .84f, .86f); scroll.Add(preview);
        previewStatus = ZincatQuickMenuStyle.Hint("", 5); scroll.Add(previewStatus);
        notice = ZincatQuickMenuStyle.Hint(message ?? "", 4); scroll.Add(notice);
        var pitch = new Slider("俯仰 X", -80, 80) { value = settings.pitch, showInputField = true };
        var yaw = new Slider("旋转 Y", 0, 360) { value = settings.yaw, showInputField = true };
        var padding = new Slider("边缘留白", .02f, .4f) { value = settings.padding, showInputField = true };
        void Save() { EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(settings)); QueuePreview(); }
        pitch.RegisterValueChangedCallback(e => { settings.pitch = e.newValue; Save(); }); yaw.RegisterValueChangedCallback(e => { settings.yaw = e.newValue; Save(); }); padding.RegisterValueChangedCallback(e => { settings.padding = e.newValue; Save(); });
        var presets = new VisualElement(); presets.style.flexDirection = FlexDirection.Row; presets.style.flexWrap = Wrap.Wrap;
        void View(float x, float y) { settings.pitch = x; settings.yaw = y; pitch.SetValueWithoutNotify(x); yaw.SetValueWithoutNotify(y); Save(); }
        var defaultButton = new Button(() => { var saved = ReadDefaultAngle(settings); View(saved.pitch, saved.yaw); }) { text = "默认" };
        void ShowSavedAngle(AngleSlot saved) { defaultButton.tooltip = "已存默认角度：X " + saved.pitch.ToString("0.#") + "° · Y " + saved.yaw.ToString("0.#") + "°"; }
        ShowSavedAngle(ReadDefaultAngle(settings)); presets.Add(defaultButton);
        presets.Add(new Button(() => View(22, 335)) { text = "标准" }); presets.Add(new Button(() => View(45, 45)) { text = "大斜角" }); presets.Add(new Button(() => View(0, 0)) { text = "正面" }); presets.Add(new Button(() => View(0, 90)) { text = "侧面" });
        presets.Add(new Button(() => Run(() => { var saved = SaveDefaultAngle(settings); ShowSavedAngle(saved); message = defaultButton.tooltip; })) { text = "保存为默认", tooltip = "保存当前俯仰和旋转；生成器的快捷拍照也会使用此角度" });
        scroll.Add(presets); scroll.Add(pitch); scroll.Add(yaw); scroll.Add(padding);
        var compatibility = new Toggle("透明截图兼容") { value = settings.transparentCompatibility };
        compatibility.RegisterValueChangedCallback(e => { settings.transparentCompatibility = e.newValue; Save(); }); scroll.Add(compatibility);
        scroll.Add(ZincatQuickMenuStyle.Hint("统一输出 256×256，直接保存为菜单图标资源。", 0));
        foreach (var child in root.Children()) if (child != scroll) child.style.flexShrink = 0;
        ZincatQuickMenuStyle.Apply(root);
    }
    ZincatMaPhotoOption Current() { RefreshOptions(); return options.FirstOrDefault(o => o.Key == active); }
    void CaptureCurrent()
    {
        var option = Current(); if (option == null || !CanRender(option)) { message = NoPreviewText(); return; }
        pendingPreview?.Pause();
        var image = Render(option, settings);
        try
        {
            option.Apply(image);
            ShowPreview(option, image); image = null;
            message = "当前图标已拍摄并回填。";
        }
        finally { if (image) Object.DestroyImmediate(image); }
    }
    string NoPreviewText() => options.Count == 0 ? "请在 MA 组件生成器添加选项或读取菜单，再点刷新预览。" : "这个选项没有可截图来源，请选择其他选项。";
    void ShowPreview(ZincatMaPhotoOption option, Texture2D image)
    {
        ReleasePreview(); previewTexture = image; preview.image = image;
        previewStatus.text = option.ManualImage ? "自定义图片，保持原图比例。" : "角度预览已更新。";
    }
    void QueuePreview(bool immediate = false)
    {
        pendingPreview?.Pause(); if (previewStatus != null) previewStatus.text = "正在更新预览…";
        pendingPreview = rootVisualElement.schedule.Execute(() =>
        {
            if (batchBusy) { QueuePreview(); return; }
            try
            {
                var option = Current();
                if (option == null || !CanRender(option)) { ReleasePreview(); previewStatus.text = NoPreviewText(); return; }
                ShowPreview(option, Render(option, settings));
            }
            catch (Exception ex) { if (previewStatus != null) previewStatus.text = ex.Message; }
        }).StartingIn(immediate ? 0 : 180);
    }
    internal static bool CanRender(ZincatMaPhotoOption option) => option.ManualImage ? option.SourceImage != null || IsImageFile(option.ImagePath) && File.Exists(option.ImagePath) || option.Entry.Icon != null : ZincatMaPhotoRenderer.CanCapture(option.Targets ?? option.Entry.Targets, option.ClosedTargets);
    internal static Texture2D Render(ZincatMaPhotoOption option, ViewSettings view)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请退出运行模式后截图。");
        if (option.ManualImage)
        {
            if (option.SourceImage) return ProcessImage(option.SourceImage, option.Name);
            if (IsImageFile(option.ImagePath) && File.Exists(option.ImagePath)) return ProcessImageFile(option.ImagePath, option.Name);
            if (option.Entry.Icon) return ProcessImage(option.Entry.Icon, option.Name);
            throw new InvalidOperationException("请先放入自定义图片。");
        }
        try { return ZincatMaPhotoRenderer.Capture(option, view.yaw, view.pitch, view.padding, view.transparentCompatibility); }
        catch (NullReferenceException ex)
        {
            Debug.LogException(ex);
            throw new InvalidOperationException("“" + option.Name + "”拍照时遇到无效的模型或渲染引用，详细位置已记录到 Unity Console。", ex);
        }
    }
    internal static string CaptureAll(IReadOnlyList<ZincatMaPhotoOption> options, ViewSettings view = null)
    {
        if (batchBusy) return "正在批量拍摄，请等待。";
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请退出运行模式后截图。");
        batchBusy = true; int done = 0, skipped = 0, failed = 0; bool cancelled = false; var errors = new List<string>();
        try
        {
            view = view ?? ReadSettings();
            for (int i = 0; i < options.Count; i++)
            {
                var option = options[i];
                if (EditorUtility.DisplayCancelableProgressBar("一键拍摄全部", option.Name, (float)i / options.Count)) { cancelled = true; break; }
                if (!CanRender(option)) { skipped++; continue; }
                Texture2D image = null;
                try { image = Render(option, view); option.Apply(image); done++; }
                catch (Exception ex) { failed++; if (errors.Count < 3) errors.Add(option.Name + "：" + ex.Message); }
                finally { if (image) Object.DestroyImmediate(image); }
            }
        }
        finally { batchBusy = false; EditorUtility.ClearProgressBar(); }
        return (cancelled ? "已停止，" : "拍摄完成，") + done + " 项已回填 · " + skipped + " 项空来源跳过 · " + failed + " 项失败" + (errors.Count > 0 ? "\n" + string.Join("\n", errors) : "。");
    }
    internal static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string((value ?? "图标").Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(name)) name = "图标"; return name.Length > 80 ? name.Substring(0, 80) : name;
    }
    internal static bool IsImageFile(string path) => !string.IsNullOrEmpty(path) && new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(path).ToLowerInvariant());
    internal static Texture2D ProcessImage(Texture source, string name)
    {
        if (!source) throw new InvalidOperationException("请先放入图片。");
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) throw new InvalidOperationException("当前没有可用的图形设备。");
        var size = ZincatMaDraftRules.FitIconSize(source.width, source.height);
        var temporary = RenderTexture.GetTemporary(size.x, size.y, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active; Texture2D result = null;
        try
        {
            // GPU scaling supports non-readable imported textures without changing their importer.
            Graphics.Blit(source, temporary); RenderTexture.active = temporary;
            result = new Texture2D(256, 256, TextureFormat.RGBA32, false, false);
            result.SetPixels32(new Color32[256 * 256]);
            result.ReadPixels(new Rect(0, 0, size.x, size.y), (256 - size.x) / 2, (256 - size.y) / 2); result.Apply(false, false);
            var image = result; result = null; return image;
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(temporary); if (result) Object.DestroyImmediate(result); }
    }
    internal static Texture2D ProcessImageFile(string path, string name)
    {
        if (!IsImageFile(path) || !File.Exists(path)) throw new InvalidOperationException("请选择有效的 PNG 或 JPG 图片。");
        var source = new Texture2D(2, 2);
        try { if (!source.LoadImage(File.ReadAllBytes(path))) throw new InvalidOperationException("图片无法读取。"); return ProcessImage(source, name); }
        finally { Object.DestroyImmediate(source); }
    }
    [Serializable] sealed class IconSourceData
    {
        public ZincatMaIconSource mode;
        public List<string> objects = new List<string>();
        public string imageGuid, imagePath;
    }
    const string SourcePrefix = "Zincat.MaMenu.IconSource:";
    internal static void WriteSource(TextureImporter importer, ZincatMaEntry entry)
    {
        var data = new IconSourceData { mode = entry.IconSource, imageGuid = entry.IconInput ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(entry.IconInput)) : "", imagePath = entry.IconInputPath };
        data.objects = entry.IconTargets.Where(t => t != null).Select(t => GlobalObjectId.GetGlobalObjectIdSlow(t).ToString()).ToList();
        importer.userData = SourcePrefix + JsonUtility.ToJson(data);
    }
    internal static void RestoreSource(ZincatMaEntry entry)
    {
        if (!entry.Icon) return;
        var path = AssetDatabase.GetAssetPath(entry.Icon); var importer = AssetImporter.GetAtPath(path);
        string json = importer != null && importer.userData.StartsWith(SourcePrefix, StringComparison.Ordinal) ? importer.userData.Substring(SourcePrefix.Length) : null;
        // Read older local metadata once for existing icons; new captures store it with the icon resource.
        if (string.IsNullOrEmpty(json))
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zincat", "MaMenuPhotos", "source_" + AssetDatabase.AssetPathToGUID(path) + ".json");
            if (File.Exists(legacy)) json = File.ReadAllText(legacy);
        }
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var data = JsonUtility.FromJson<IconSourceData>(json); if (data == null) return;
            entry.IconSource = data.mode; entry.IconInputPath = data.imagePath;
            entry.IconInput = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(data.imageGuid ?? ""));
            entry.IconTargets = (data.objects ?? new List<string>()).Select(id => GlobalObjectId.TryParse(id, out var value) ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(value) as GameObject : null).Where(t => t != null).ToList();
        }
        catch { }
    }
}

internal static class ZincatMaPhotoRenderer
{
    static IEnumerable<Renderer> SelectedRenderers(IEnumerable<GameObject> targets, ISet<GameObject> closed)
    {
        foreach (var renderer in WardrobePartIconRenderer.VisibleRenderers(targets))
        {
            if (!(renderer is SkinnedMeshRenderer skin ? skin.sharedMesh != null : renderer.GetComponent<MeshFilter>()?.sharedMesh != null)) continue;
            bool hidden = false;
            if (closed != null && closed.Count != 0)
                for (var node = renderer.transform; node != null; node = node.parent)
                    if (closed.Contains(node.gameObject)) { hidden = true; break; }
            if (!hidden) yield return renderer;
        }
    }
    internal static bool CanCapture(IEnumerable<GameObject> targets, ISet<GameObject> closed = null) => SelectedRenderers(targets, closed).Any();
    static GameObject SourceRoot(GameObject target) { var avatar = target.GetComponentInParent<VRCAvatarDescriptor>(true); return avatar ? avatar.gameObject : target.transform.root.gameObject; }
    internal static Texture2D Capture(ZincatMaPhotoOption option, float yaw, float pitch, float padding, bool transparentCompatibility)
    {
        var renderers = SelectedRenderers(option.Targets ?? option.Entry.Targets, option.ClosedTargets).ToArray();
        if (renderers.Length == 0) throw new InvalidOperationException("这个选项没有可截图的网格。");
        var rotation = SourceRoot(renderers[0].gameObject).transform.rotation;
        var entry = option.Entry;
        bool changesMesh = (entry.Shapes && entry.ShapeMeshes.Any(m => m.Rows.Count > 0)) ||
                           (entry.Materials && entry.MaterialMeshes.Any(m => m.Rows.Count > 0));
        var skins = renderers.OfType<SkinnedMeshRenderer>().ToArray();
        bool needsSync = skins.Any(r => r.GetComponents<ModularAvatarBlendshapeSync>().Any(s => s != null && s.Bindings != null && s.Bindings.Count > 0));
        // Only mesh overrides or MA synchronization need isolated renderers.
        if (!changesMesh && !needsSync) return WardrobePartIconRenderer.CaptureRenderers(renderers, rotation, yaw, pitch, padding, transparentCompatibility);
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var copies = new Dictionary<GameObject, GameObject>();
            foreach (var source in renderers.Select(r => SourceRoot(r.gameObject)).Distinct())
            {
                var holder = new GameObject("分件截图临时分身") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(holder, scene); holder.SetActive(false);
                var copy = Object.Instantiate(source, holder.transform, false); copy.hideFlags = HideFlags.HideAndDontSave;
                copies.Add(source, copy);
                copy.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation); copy.transform.localScale = source.transform.lossyScale;
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) if (behaviour != null) behaviour.enabled = false;
                foreach (var particles in copy.GetComponentsInChildren<ParticleSystem>(true)) if (particles != null) particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ApplyMeshes(source, copy, option.Entry);
            }
            Renderer PreviewRenderer(Renderer original)
            {
                for (var node = original.transform; node != null; node = node.parent)
                    if (copies.TryGetValue(node.gameObject, out var copy))
                        return (Renderer)Counterpart(original.gameObject, node.gameObject, copy).GetComponent(original.GetType());
                // A dependency outside the captured hierarchy can be read, never written.
                return original;
            }
            if (needsSync) WardrobeMaIconPreview.ApplyBlendshapeSync(skins, PreviewRenderer);
            return WardrobePartIconRenderer.CaptureRenderers(renderers.Select(PreviewRenderer), rotation, yaw, pitch, padding, transparentCompatibility);
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
    }
    internal static GameObject Counterpart(GameObject target, GameObject source, GameObject copy)
    {
        if (!target) throw new InvalidOperationException("菜单目标已失效。");
        var path = new Stack<int>(); var node = target.transform;
        while (node != source.transform) { if (!node || !node.parent) throw new InvalidOperationException("截图目标不属于源物件。"); path.Push(node.GetSiblingIndex()); node = node.parent; }
        node = copy.transform; foreach (int index in path) node = node.GetChild(index); return node.gameObject;
    }
    static void ApplyMeshes(GameObject source, GameObject copy, ZincatMaEntry entry)
    {
        bool Inside(Renderer r) => r && (r.gameObject == source || r.transform.IsChildOf(source.transform));
        if (entry.Shapes)
            foreach (var mesh in entry.ShapeMeshes.Where(m => Inside(m.Renderer)))
            {
                var renderer = Counterpart(mesh.Renderer.gameObject, source, copy).GetComponent<SkinnedMeshRenderer>();
                foreach (var row in mesh.Rows) { int index = renderer.sharedMesh.GetBlendShapeIndex(row.Shape); if (index >= 0) renderer.SetBlendShapeWeight(index, row.DeleteShape ? 0 : row.Weight); }
            }
        if (entry.Materials)
            foreach (var mesh in entry.MaterialMeshes.Where(m => Inside(m.Renderer)))
            {
                var renderer = Counterpart(mesh.Renderer.gameObject, source, copy).GetComponent<Renderer>(); var materials = renderer.sharedMaterials;
                foreach (var row in mesh.Rows) if (row.Slot >= 0 && row.Slot < materials.Length) materials[row.Slot] = row.Material;
                renderer.sharedMaterials = materials;
            }
    }
}
#endif
