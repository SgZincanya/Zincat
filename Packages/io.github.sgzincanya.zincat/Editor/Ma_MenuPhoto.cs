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
}

internal sealed class ZincatMaPhotoWindow : EditorWindow
{
    [Serializable] internal sealed class ViewSettings
    {
        public float pitch = 22, yaw = 335, padding = .1f;
        public bool transparentCompatibility;
    }
    const string PrefsKey = "Zincat.MaMenu.PartPhoto.Settings";
    ViewSettings settings;
    internal static ViewSettings ReadSettings()
    {
        try { return JsonUtility.FromJson<ViewSettings>(EditorPrefs.GetString(PrefsKey, "")) ?? new ViewSettings(); }
        catch { return new ViewSettings(); }
    }
    Func<List<ZincatMaPhotoOption>> read;
    List<ZincatMaPhotoOption> options = new List<ZincatMaPhotoOption>();
    string active, message;
    Image preview;
    Label notice, previewStatus;
    Texture2D previewTexture;
    IVisualElementScheduledItem pendingPreview;
    static bool batchBusy;
    internal static void OpenSettings(Func<List<ZincatMaPhotoOption>> provider)
    {
        var window = GetWindow<ZincatMaPhotoWindow>("分件截图设置"); window.minSize = new Vector2(420, 610); window.read = provider;
        window.Reload(); window.Show();
    }
    void OnDisable() { pendingPreview?.Pause(); ReleasePreview(); read = null; }
    void ReleasePreview() { if (preview != null) preview.image = null; if (previewTexture) Object.DestroyImmediate(previewTexture); previewTexture = null; }
    public void CreateGUI() { Reload(); }
    void Reload()
    {
        settings = ReadSettings();
        try { options = read != null ? read() : new List<ZincatMaPhotoOption>(); }
        catch (Exception ex) { options.Clear(); message = ex.Message; }
        if (!options.Any(o => o.Key == active)) active = (options.FirstOrDefault(o => !o.ManualImage && CanRender(o)) ?? options.FirstOrDefault())?.Key;
        Build(); QueuePreview();
    }
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
        root.Add(ZincatQuickMenuStyle.Hint("调整角度后自动预览；预览不修改图标，确认后可单拍或一键全拍。", 8));
        var scroll = new ScrollView(); scroll.style.flexGrow = 1; scroll.style.minHeight = 0; root.Add(scroll);
        if (options.Count > 0)
        {
            var choose = new DropdownField("预览选项", options.Select((o, i) => (i + 1) + ". " + o.Name).ToList(), options.FindIndex(o => o.Key == active));
            choose.RegisterValueChangedCallback(e => { if (choose.index >= 0) { active = options[choose.index].Key; QueuePreview(); } }); scroll.Add(choose);
        }
        scroll.Add(new Button(Reload) { text = "读取最新菜单", tooltip = "更新当前模式的选项列表及配置" });
        preview = new Image { scaleMode = ScaleMode.ScaleToFit }; preview.style.height = 256; preview.style.flexShrink = 0; preview.style.marginTop = 8; preview.style.marginBottom = 8;
        preview.style.backgroundColor = EditorGUIUtility.isProSkin ? new Color(.16f, .16f, .17f) : new Color(.84f, .84f, .86f); scroll.Add(preview);
        previewStatus = ZincatQuickMenuStyle.Hint("", 5); scroll.Add(previewStatus);
        var pitch = new Slider("俯仰 X", -80, 80) { value = settings.pitch, showInputField = true };
        var yaw = new Slider("旋转 Y", 0, 360) { value = settings.yaw, showInputField = true };
        var padding = new Slider("边缘留白", .02f, .4f) { value = settings.padding, showInputField = true };
        void Save() { EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(settings)); QueuePreview(); }
        pitch.RegisterValueChangedCallback(e => { settings.pitch = e.newValue; Save(); }); yaw.RegisterValueChangedCallback(e => { settings.yaw = e.newValue; Save(); }); padding.RegisterValueChangedCallback(e => { settings.padding = e.newValue; Save(); });
        var presets = new VisualElement(); presets.style.flexDirection = FlexDirection.Row; presets.style.flexWrap = Wrap.Wrap;
        void View(float x, float y) { settings.pitch = x; settings.yaw = y; pitch.SetValueWithoutNotify(x); yaw.SetValueWithoutNotify(y); Save(); }
        presets.Add(new Button(() => View(22, 335)) { text = "标准" }); presets.Add(new Button(() => View(45, 45)) { text = "大斜角" }); presets.Add(new Button(() => View(0, 0)) { text = "正面" }); presets.Add(new Button(() => View(0, 90)) { text = "侧面" });
        scroll.Add(presets); scroll.Add(pitch); scroll.Add(yaw); scroll.Add(padding);
        var compatibility = new Toggle("透明截图兼容") { value = settings.transparentCompatibility };
        compatibility.RegisterValueChangedCallback(e => { settings.transparentCompatibility = e.newValue; Save(); }); scroll.Add(compatibility);
        var actions = new VisualElement(); actions.style.flexDirection = FlexDirection.Row; actions.style.flexWrap = Wrap.Wrap; actions.style.marginTop = 8;
        actions.Add(new Button(() => Run(() =>
        {
            var option = Current(); if (option == null || !CanRender(option)) { message = "此选项没有可截图来源，图标保持原样。"; return; }
            var image = Render(option, settings); try { option.Apply(image); } finally { Object.DestroyImmediate(image); }
            message = "当前图标已拍摄并回填。"; QueuePreview();
        })) { text = "拍摄并回填当前" });
        actions.Add(new Button(() => Run(() => { message = CaptureAll(read != null ? read() : options); QueuePreview(); })) { text = "一键拍摄全部", tooltip = "当前模式全部选项逐项拍摄并回填，空目标跳过；自定义图片按原来源处理" }); scroll.Add(actions);
        notice = ZincatQuickMenuStyle.Hint(message ?? "选择有网格的选项查看预览。", 8); scroll.Add(notice);
        scroll.Add(ZincatQuickMenuStyle.Hint("统一输出 256×256，直接保存为菜单图标资源。", 0));
        foreach (var child in root.Children()) if (child != scroll) child.style.flexShrink = 0;
        ZincatQuickMenuStyle.Apply(root);
    }
    ZincatMaPhotoOption Current()
    {
        var latest = read != null ? read() : options; return latest.FirstOrDefault(o => o.Key == active);
    }
    void QueuePreview()
    {
        pendingPreview?.Pause(); if (previewStatus != null) previewStatus.text = "正在更新预览…";
        pendingPreview = rootVisualElement.schedule.Execute(() =>
        {
            if (batchBusy) { QueuePreview(); return; }
            try
            {
                ReleasePreview(); var option = Current();
                if (option == null || !CanRender(option)) { previewStatus.text = "此选项没有可截图来源，保持空白；可换一个选项预览。"; return; }
                previewTexture = Render(option, settings); preview.image = previewTexture;
                previewStatus.text = option.ManualImage ? "自定义图片预览，保持原图比例。" : "角度预览已更新；拍摄回填前不会改变菜单图标。";
            }
            catch (Exception ex) { if (previewStatus != null) previewStatus.text = ex.Message; }
        }).StartingIn(180);
    }
    internal static bool CanRender(ZincatMaPhotoOption option) => option.ManualImage ? option.SourceImage != null || IsImageFile(option.ImagePath) && File.Exists(option.ImagePath) || option.Entry.Icon != null : ZincatMaPhotoRenderer.CanCapture(option.Targets ?? option.Entry.Targets);
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
        return ZincatMaPhotoRenderer.Capture(option, view.yaw, view.pitch, view.padding, view.transparentCompatibility);
    }
    internal static string CaptureAll(IReadOnlyList<ZincatMaPhotoOption> options)
    {
        if (batchBusy) return "正在批量拍摄，请等待。";
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请退出运行模式后截图。");
        batchBusy = true; int done = 0, skipped = 0, failed = 0; bool cancelled = false; var errors = new List<string>();
        try
        {
            var view = ReadSettings();
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
    internal static bool CanCapture(IEnumerable<GameObject> targets) => targets.Where(t => t != null).SelectMany(t => t.GetComponentsInChildren<Renderer>(true)).Any(r => r.enabled && (r is SkinnedMeshRenderer skin ? skin.sharedMesh != null : r is MeshRenderer && r.GetComponent<MeshFilter>()?.sharedMesh != null));
    static GameObject SourceRoot(GameObject target) { var avatar = target.GetComponentInParent<VRCAvatarDescriptor>(true); return avatar ? avatar.gameObject : target.transform.root.gameObject; }
    internal static Texture2D Capture(ZincatMaPhotoOption option, float yaw, float pitch, float padding, bool transparentCompatibility)
    {
        var targets = (option.Targets ?? option.Entry.Targets).Where(t => t != null).Distinct().ToArray();
        if (!CanCapture(targets)) throw new InvalidOperationException("这个选项没有可截图的网格。");
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var roots = new List<GameObject>();
            foreach (var pack in targets.GroupBy(SourceRoot))
            {
                var source = pack.Key;
                var holder = new GameObject("分件截图临时分身") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(holder, scene); holder.SetActive(false);
                var copy = Object.Instantiate(source, holder.transform, false); copy.hideFlags = HideFlags.HideAndDontSave;
                copy.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation); copy.transform.localScale = source.transform.lossyScale;
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                foreach (var particles in copy.GetComponentsInChildren<ParticleSystem>(true)) particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ApplyMeshes(source, copy, option.Entry); roots.AddRange(pack.Select(t => Counterpart(t, source, copy)));
            }
            return WardrobePartIconRenderer.CapturePreview(roots, SourceRoot(targets[0]).transform.rotation, yaw, pitch, padding, transparentCompatibility);
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
