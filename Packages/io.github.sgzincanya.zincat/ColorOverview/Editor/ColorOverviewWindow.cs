#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zincat.ColorOverview
{
    public sealed partial class ColorOverviewWindow : EditorWindow
    {
        private sealed class Variant
        {
            public string Name, Path, PrefabGuid;
            public bool Enabled = true;
            public List<MatSwap> Swaps = new List<MatSwap>();
        }
        private sealed class Set
        {
            public string Name;
            public bool Enabled = true;
            public bool Expanded;
            public readonly List<Variant> Variants = new List<Variant>();
        }
        private sealed class DrawItem
        {
            public Mesh Mesh;
            public Matrix4x4 Matrix;
            public Material[] Materials;
        }
        private readonly List<Set> sets = new List<Set>();
        private Vector2 scroll;
        private string output;
        private int resolution = 512, columns = 5;
        private float yaw = 0, pitch = 5;
        private bool includeInactive;
        private string status;

        [MenuItem("Zincat/配色总览截图")]
        public static void Open() { GetWindow<ColorOverviewWindow>("配色总览截图"); }
        // Optional headless entry point, still requires a graphics device (do not use -nographics).
        public static void BatchExport()
        {
            var window = CreateInstance<ColorOverviewWindow>();
            var report = new StringBuilder();
            bool before = ShaderUtil.allowAsyncCompilation;
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-colorOverviewOutput");
                window.output = index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.GetFullPath("ColorOverviews");
                Directory.CreateDirectory(window.output);
                ShaderUtil.allowAsyncCompilation = false;
                if (window.sets.Count == 0) throw new InvalidOperationException("截图列表为空，请先载入分组。或在窗口中粘贴 AI 结果。");
                foreach (var set in window.sets)
                {
                    if (set.Variants.Count == 0) throw new InvalidOperationException("未找到资源：" + set.Name);
                    window.Export(set, report);
                    Debug.Log("COLOR_OVERVIEW_SAVED " + set.Name + " " + set.Variants.Count);
                    File.WriteAllText(Path.Combine(window.output, "清单.txt"), report.ToString(), new UTF8Encoding(true));
                }
                Debug.Log("COLOR_OVERVIEW_COMPLETE");
            }
            finally { ShaderUtil.allowAsyncCompilation = before; EditorUtility.ClearProgressBar(); DestroyImmediate(window); }
        }
        private void OnEnable()
        {
            minSize = new Vector2(560, 480);
            aiShowText = false; uiSettings = false;
            output = EditorPrefs.GetString("Zincat.ColorOverview.Output", Path.GetFullPath("ColorOverviews"));
            sets.Clear();
            LoadAiSession();
        }
        private static string[] Prefabs(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return Array.Empty<string>();
            return AssetDatabase.FindAssets("t:Prefab", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, Comparer<string>.Create(EditorUtility.NaturalCompare)).ToArray();
        }
        private void OnGUI() { DrawOverviewUI(); }
        private static bool Visible(Renderer renderer, GameObject root, bool all)
        {
            if (all) return true;
            if (!renderer.enabled) return false;
            for (Transform t = renderer.transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == root.transform) break;
            }
            return true;
        }
        private static List<DrawItem> Collect(Variant variant, bool all, List<Mesh> owned)
        {
            if (!string.IsNullOrEmpty(variant.PrefabGuid)) variant.Path = AssetPath(variant.PrefabGuid);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(variant.Path);
            if (!root) throw new InvalidOperationException("资源不存在：" + variant.Path);
            var result = new List<DrawItem>();
            var swaps = variant.Swaps.ToDictionary(s => AssetDatabase.LoadAssetAtPath<Material>(AssetPath(s.From)),
                s => AssetDatabase.LoadAssetAtPath<Material>(AssetPath(s.To)));
            if (swaps.Any(s => !s.Key || !s.Value)) throw new InvalidOperationException("配色材质已失效。");
            var replaced = new HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!Visible(r, root, all)) continue;
                Mesh mesh = null;
                if (r is SkinnedMeshRenderer skin && skin.sharedMesh)
                { mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave }; owned.Add(mesh); skin.BakeMesh(mesh); }
                else mesh = SourceMesh(r);
                if (!mesh || mesh.vertexCount == 0 || mesh.subMeshCount == 0) continue;
                var materials = r.sharedMaterials.ToArray();
                if (materials.Length == 0 || materials.All(m => !m)) continue;
                for (int m = 0; m < materials.Length; m++)
                {
                    if (materials[m] && swaps.TryGetValue(materials[m], out var replacement))
                    { replaced.Add(materials[m]); materials[m] = replacement; }
                }
                result.Add(new DrawItem { Mesh = mesh, Matrix = root.transform.worldToLocalMatrix * r.localToWorldMatrix, Materials = materials });
            }
            if (result.Count == 0) throw new InvalidOperationException("没有可显示网格：" + variant.Path);
            if (replaced.Count != swaps.Count) throw new InvalidOperationException("材质替换未匹配到可见网格，请检查配色映射或启用隐藏部件：" + variant.Name);
            return result;
        }
        private static Bounds ViewBounds(List<DrawItem> items, Quaternion view)
        {
            Quaternion inverse = Quaternion.Inverse(view);
            Bounds result = default; bool first = true;
            foreach (var item in items)
            {
                // Mesh bounds rather than renderer bounds avoids oversized skinned culling bounds.
                var b = item.Mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var p = inverse * item.Matrix.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (first) { result = new Bounds(p, Vector3.zero); first = false; } else result.Encapsulate(p);
                }
            }
            return result;
        }
        private static bool IsNeverPassError(Shader shader, ShaderMessage message)
        {
            // Only disregard an error whose source line is proven to belong to a LightMode=Never
            // program. Do not exempt whole shader families or errors in shared include files.
            string path = AssetDatabase.GetAssetPath(shader);
            if (message.line <= 0 || !File.Exists(path)) return false;
            if (!string.IsNullOrEmpty(message.file) &&
                !string.Equals(Path.GetFullPath(message.file), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return false;
            string source = File.ReadAllText(path);
            // Preserve line numbers while removing comments before identifying ShaderLab programs.
            source = Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", match =>
                new string(match.Value.Select(c => c == '\n' || c == '\r' ? c : ' ').ToArray()));
            int offset = 0;
            for (int line = 1; line < message.line; line++)
            {
                offset = source.IndexOf('\n', offset);
                if (offset < 0) return false;
                offset++;
            }
            foreach (Match pass in Regex.Matches(source,
                @"\bPass\s*\{(?<header>[\s\S]*?)\b(?:HLSLPROGRAM|CGPROGRAM)\b(?<program>[\s\S]*?)\b(?:ENDHLSL|ENDCG)\b"))
            {
                var program = pass.Groups["program"];
                if (offset >= program.Index && offset < program.Index + program.Length &&
                    Regex.IsMatch(pass.Groups["header"].Value, "\"LightMode\"\\s*=\\s*\"Never\"", RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }

        private static bool HasBlockingShaderError(Shader shader)
        {
            if (shader == null || !ShaderUtil.ShaderHasError(shader)) return false;
            var errors = ShaderUtil.GetShaderMessages(shader)
                .Where(message => message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).ToArray();
            return errors.Length == 0 || errors.Any(message => !IsNeverPassError(shader, message));
        }

        private static Texture2D Render(List<DrawItem> items, Bounds bounds, Quaternion view, int size)
        {
            var preview = new PreviewRenderUtility();
            Texture2D result = null;
            bool opened = false;
            var previous = RenderTexture.active;
            RenderTexture scaled = null;
            try
            {
                preview.BeginPreview(new Rect(0, 0, size, size), GUIStyle.none); opened = true;
                var camera = preview.camera;
                camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(.01f, Mathf.Max(bounds.size.x, bounds.size.y) * .58f);
                float distance = Mathf.Max(1, bounds.size.magnitude * 3);
                camera.transform.SetPositionAndRotation(view * (bounds.center - Vector3.forward * distance), view);
                camera.nearClipPlane = .001f; camera.farClipPlane = distance + bounds.size.magnitude * 4 + 1;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.19f, .21f, .25f, 1);
                camera.allowHDR = false;
                preview.ambientColor = new Color(.65f, .65f, .65f, 1);
                preview.lights[0].intensity = 1.1f;
                preview.lights[0].transform.rotation = Quaternion.Euler(35, 210, 0);
                preview.lights[1].intensity = .6f;
                preview.lights[1].transform.rotation = Quaternion.Euler(340, 30, 0);
                foreach (var item in items)
                for (int i = 0; i < item.Mesh.subMeshCount; i++)
                {
                    var mat = item.Materials[Mathf.Min(i, item.Materials.Length - 1)];
                    if (mat) preview.DrawMesh(item.Mesh, item.Matrix, mat, i);
                }
                preview.Render();
                var bad = items.SelectMany(i => i.Materials).Where(m => m && HasBlockingShaderError(m.shader)).Distinct().ToArray();
                if (bad.Length > 0) throw new InvalidOperationException("着色器编译失败，未保存该套截图：" + string.Join(", ", bad.Select(m => m.name)));
                var texture = preview.EndPreview(); opened = false;
                if (!texture) throw new InvalidOperationException("未获得截图。");
                scaled = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(texture, scaled); RenderTexture.active = scaled;
                result = new Texture2D(size, size, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, size, size), 0, 0); result.Apply();
                return result;
            }
            catch { if (result) DestroyImmediate(result); throw; }
            finally
            {
                if (opened) { try { preview.EndPreview(); } catch { } }
                RenderTexture.active = previous;
                if (scaled) RenderTexture.ReleaseTemporary(scaled);
                preview.Cleanup();
            }
        }
        private void Run()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) { status = "需要启用图形设备的 Unity 编辑器。"; return; }
            var selected = sets.Where(s => s.Enabled && s.Variants.Any(v => v.Enabled)).ToArray();
            if (selected.Length == 0) { status = "请至少勾选一套。"; return; }
            if (selected.Any(s => s.Variants.Count == 0)) { status = "有勾选资源未找到，请检查资源目录。"; return; }
            if (selected.Select(s => SafeFileName(s.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            { status = "分组名称产生了重复文件名，请让 AI 使用不同的套装名。"; return; }
            status = null;
            var report = new StringBuilder("Zincat Color Overview\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n");
            report.AppendLine("Standalone source prefab rendering; no MA/NDMF build. Hidden parts: " + includeInactive);
            var failed = new List<string>(); int done = 0;
            bool asyncBefore = ShaderUtil.allowAsyncCompilation;
            try
            {
                output = Path.GetFullPath(output);
                Directory.CreateDirectory(output);
                EditorPrefs.SetString("Zincat.ColorOverview.Output", output);
                ShaderUtil.allowAsyncCompilation = false;
                foreach (var set in selected)
                {
                    try { Export(set, report); done++; }
                    catch (OperationCanceledException) { report.AppendLine("CANCELLED: " + set.Name); status = "已取消，已完成 " + done + " 张。当前未完成套装不保存。"; break; }
                    catch (Exception e) { failed.Add(set.Name); report.AppendLine("FAILED " + set.Name + ": " + e); Debug.LogException(e); }
                }
                File.WriteAllText(Path.Combine(output, "清单.txt"), report.ToString(), new UTF8Encoding(true));
                if (status == null || !status.StartsWith("已取消")) status = "完成 " + done + " 张。" + (failed.Count > 0 ? "失败：" + string.Join("、", failed) + "，请查看清单。" : "全部展示项已包含。");
                EditorUtility.RevealInFinder(output);
            }
            catch (Exception e) { status = e.Message; Debug.LogException(e); }
            finally { ShaderUtil.allowAsyncCompilation = asyncBefore; EditorUtility.ClearProgressBar(); Repaint(); }
        }
        private static string SafeFileName(string name)
        {
            string value = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.').Replace(' ', '_');
            if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("分组名称不能为空。");
            if (Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) value = "_" + value;
            return value;
        }
        private void Export(Set set, StringBuilder report)
        {
            var variants = set.Variants.Where(v => v.Enabled).ToList();
            int count = variants.Count, cols = Mathf.Min(columns, count), rows = (count + cols - 1) / cols;
            int gap = 12, label = 56, header = 76;
            int width = cols * (resolution + gap) + gap, height = header + rows * (resolution + label + gap) + gap;
            if (width > Mathf.Min(16384, SystemInfo.maxTextureSize) || height > Mathf.Min(16384, SystemInfo.maxTextureSize))
                throw new InvalidOperationException("总览图尺寸过大，请降低每格清晰度或调整每行数量。");
            var owned = new List<Mesh>();
            var collections = new List<List<DrawItem>>();
            Texture2D sheet = null;
            ColorOverviewCaptions captions = null;
            var view = Quaternion.Euler(pitch, 180 + yaw, 0);
            try
            {
                Bounds bounds = default; bool first = true;
                foreach (var variant in variants)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(set.Name, "检查 " + variant.Name, collections.Count / (float)count * .2f)) throw new OperationCanceledException();
                    var items = Collect(variant, includeInactive, owned); collections.Add(items);
                    var b = ViewBounds(items, view);
                    if (first) { bounds = b; first = false; } else bounds.Encapsulate(b);
                }
                // Compile source passes before rendering to avoid asynchronous placeholder screenshots.
                foreach (var mat in collections.SelectMany(c => c).SelectMany(i => i.Materials).Where(m => m).Distinct())
                {
                    if (!mat.shader || mat.shader.name == "Hidden/InternalErrorShader") throw new InvalidOperationException("无效着色器：" + mat.name);
                    for (int pass = 0; pass < mat.passCount; pass++)
                        if (mat.GetShaderPassEnabled(mat.GetPassName(pass)) &&
                            mat.shader.FindPassTagValue(pass, new ShaderTagId("LightMode")).name != "Never")
                            ShaderUtil.CompilePass(mat, pass, true);
                }
                sheet = new Texture2D(width, height, TextureFormat.RGBA32, false);
                sheet.SetPixels32(Enumerable.Repeat(new Color32(24, 27, 34, 255), width * height).ToArray());
                string subtitle = count + " VARIANTS / SOURCE PREFABS";
                string captionChars = set.Name + subtitle + string.Join("", variants.Select(v => v.Name)) + "0123456789 ";
                captions = new ColorOverviewCaptions(captionChars);
                captions.Draw(sheet, set.Name, gap, 8, 3, width - gap * 2);
                captions.Draw(sheet, subtitle, gap, 45, 2, width - gap * 2);
                report.AppendLine("\n" + set.Name + " | " + count + " entries | " + width + "x" + height);
                for (int i = 0; i < count; i++)
                {
                    var variant = variants[i];
                    if (EditorUtility.DisplayCancelableProgressBar(set.Name, (i + 1) + "/" + count + " " + variant.Name, .2f + .8f * i / count)) throw new OperationCanceledException();
                    int x = gap + i % cols * (resolution + gap), top = header + i / cols * (resolution + label + gap);
                    var tile = Render(collections[i], bounds, view, resolution);
                    try { sheet.SetPixels32(x, height - top - resolution, resolution, resolution, tile.GetPixels32()); }
                    finally { DestroyImmediate(tile); }
                    captions.Draw(sheet, (i + 1).ToString("D2") + " " + variant.Name, x + 4, top + resolution + 2, 2, resolution - 8);
                    report.AppendLine((i + 1).ToString("D2") + " | " + variant.Name + " | " + variant.Path +
                        string.Concat(variant.Swaps.Select(sw => " | " + AssetPath(sw.From) + " => " + AssetPath(sw.To))));
                }
                sheet.Apply();
                string file = Path.Combine(output, SafeFileName(set.Name) + ".png");
                File.WriteAllBytes(file, sheet.EncodeToPNG());
                report.AppendLine("SAVED " + file);
            }
            finally { captions?.Dispose(); if (sheet) DestroyImmediate(sheet); foreach (var mesh in owned) DestroyImmediate(mesh); }
        }
    }
}
#endif
