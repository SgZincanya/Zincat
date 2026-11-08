#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zincat.Clothing.Editor
{
    // 只绘制网格；不 Instantiate 衣服，因此不会运行其脚本或改变原场景状态。
    internal static class WardrobePartIconRenderer
    {
        private sealed class Item { public Mesh Mesh; public Matrix4x4 Matrix; public Material[] Materials; }
        private static bool VisibleBelowRoot(Renderer renderer, GameObject root)
        {
            if (!renderer.enabled) return false;
            for (var node = renderer.transform; node != null && node != root.transform; node = node.parent)
                if (!node.gameObject.activeSelf) return false;
            return true;
        }
        internal static bool SupportsTransparentCompatibility(Material material)
        {
            if (material == null || material.shader == null || !material.HasProperty("_ColorMask") ||
                !material.HasProperty("_ZWrite") || !material.HasProperty("_ZTest") || !material.HasProperty("_Cull")) return false;

            // 实际混合状态优先于标签和队列；不透明、裁剪、加法等模式不套用透明分面绘制。
            // 包括 lilToon 的 _SrcBlend 和部分着色器的 _SrcBlendMode 命名。
            string src = material.HasProperty("_SrcBlend") ? "_SrcBlend" : "_SrcBlendMode";
            string dst = material.HasProperty("_DstBlend") ? "_DstBlend" : "_DstBlendMode";
            if (material.HasProperty(src) && material.HasProperty(dst))
            {
                float source = material.GetFloat(src), destination = material.GetFloat(dst);
                return (Mathf.Approximately(source, (float)BlendMode.One) ||
                        Mathf.Approximately(source, (float)BlendMode.SrcAlpha)) &&
                    Mathf.Approximately(destination, (float)BlendMode.OneMinusSrcAlpha) &&
                    (!material.HasProperty("_BlendOp") || Mathf.Approximately(material.GetFloat("_BlendOp"), (float)BlendOp.Add));
            }

            // 未暴露混合属性时要求明确的透明类型，不以高队列号推断材质类型。
            return string.Equals(material.GetTag("RenderType", false, ""), "Transparent", StringComparison.OrdinalIgnoreCase) ||
                material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0 &&
                material.shader.name.EndsWith("/Transparent", StringComparison.OrdinalIgnoreCase);
        }
        internal static bool IsLegacyGeneratedIcon(Texture2D icon)
        {
            if (icon == null) return false;
            var path = AssetDatabase.GetAssetPath(icon);
            return WardrobeIconStorage.IsGeneratedPath(path);
        }

        internal static Quaternion ViewRotation(Quaternion avatarRotation, float yaw, float pitch) =>
            avatarRotation * Quaternion.Euler(0, yaw, 0) * Quaternion.Euler(pitch, 180, 0);

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

        internal static Texture2D Capture(IEnumerable<GameObject> targets, Quaternion avatarRotation, float yaw, float pitch = 22, float padding = .1f, Texture2D overwrite = null, bool transparentCompatibility = false, string outputFolder = null)
        {
            var texture = CapturePreview(targets, avatarRotation, yaw, pitch, padding, transparentCompatibility);
            try { return SavePng(texture.EncodeToPNG(), overwrite, outputFolder); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        // 返回内存图片，由调用方决定缓存位置；保持原有工程图标保存流程。
        internal static Texture2D CapturePreview(IEnumerable<GameObject> targets, Quaternion avatarRotation, float yaw, float pitch = 22, float padding = .1f, bool transparentCompatibility = false)
        {
            if (float.IsNaN(yaw) || float.IsInfinity(yaw) || float.IsNaN(pitch) || float.IsInfinity(pitch) || float.IsNaN(padding) || float.IsInfinity(padding))
                throw new InvalidOperationException("拍摄参数必须是有效数字。");
            pitch = Mathf.Clamp(pitch, -80, 80); padding = Mathf.Clamp(padding, .02f, .4f);
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("当前 Unity 没有图形设备，无法截图；可先手动拖入图标。");
            var baked = new List<Mesh>();
            var items = new List<Item>();
            var backMaterials = new Dictionary<Material, Material>();
            var frontMaterials = new Dictionary<Material, Material>();
            PreviewRenderUtility preview = null;
            bool previewOpened = false;
            Texture2D result = null;
            bool previousAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                // 覆盖本次所有 DrawMesh/Render 请求，避免把异步编译占位色写入 PNG。
                ShaderUtil.allowAsyncCompilation = false;
                foreach (var original in targets.Where(t => t != null).SelectMany(t => t.GetComponentsInChildren<Renderer>(true).Where(r => WardrobeMaIconPreview.IsSupportedRenderer(r) && VisibleBelowRoot(r, t))).Distinct())
                {
                    var renderer = WardrobeMaIconPreview.Active?.Resolve(original) ?? original;
                    Mesh mesh = null;
                    if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                    { mesh = new Mesh(); baked.Add(mesh); skin.BakeMesh(mesh); }
                    else if (renderer is MeshRenderer) mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh == null || mesh.vertexCount == 0 || mesh.subMeshCount == 0) continue;
                    if (renderer.sharedMaterials.Length == 0 || renderer.sharedMaterials.All(m => m == null)) continue;
                    items.Add(new Item { Mesh = mesh, Matrix = renderer.localToWorldMatrix, Materials = renderer.sharedMaterials });
                }
                if (items.Count == 0) throw new InvalidOperationException("这个分类中没有可拍摄的网格与材质，请检查分件，或手动拖入图片。");
                var rotation = avatarRotation * Quaternion.Euler(0, yaw, 0);
                // 先围绕 Avatar 转向，再俯仰；正俯仰从上方拍摄，避免绕世界轴旋转。
                var viewRotation = ViewRotation(avatarRotation, yaw, pitch);
                var inverse = Quaternion.Inverse(viewRotation);
                var bounds = new Bounds(); bool first = true;
                foreach (var item in items)
                {
                    if (!item.Mesh.isReadable)
                    {
                        // 静态网格可能关闭CPU读取；仍可GPU绘制，沿用其边界以保留兼容性。
                        var b = item.Mesh.bounds;
                        for (int i = 0; i < 8; i++)
                        {
                            var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                            corner = inverse * item.Matrix.MultiplyPoint3x4(corner);
                            if (first) { bounds = new Bounds(corner, Vector3.zero); first = false; } else bounds.Encapsulate(corner);
                        }
                        continue;
                    }
                    var vertices = item.Mesh.vertices;
                    var boundIndices = new HashSet<int>();
                    for (int submesh = 0; submesh < item.Mesh.subMeshCount; submesh++)
                        if (item.Materials[Mathf.Min(submesh, item.Materials.Length - 1)] != null)
                            foreach (int index in item.Mesh.GetIndices(submesh)) boundIndices.Add(index);
                    foreach (int index in boundIndices)
                    {
                        var p = inverse * item.Matrix.MultiplyPoint3x4(vertices[index]);
                        if (first) { bounds = new Bounds(p, Vector3.zero); first = false; } else bounds.Encapsulate(p);
                    }
                }
                float size = Mathf.Max(bounds.size.x, bounds.size.y, .001f) * (.5f + padding * .5f);
                if (float.IsNaN(size) || float.IsInfinity(size)) throw new InvalidOperationException("网格边界无效，无法自动构图。");
                preview = new PreviewRenderUtility();
                preview.BeginPreview(new Rect(0, 0, 256, 256), GUIStyle.none);
                previewOpened = true;
                var camera = preview.camera;
                camera.orthographic = true; camera.orthographicSize = size;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear;
                camera.allowHDR = false; camera.allowMSAA = false;
                float distance = Mathf.Max(size * 3, bounds.size.z + size);
                camera.transform.SetPositionAndRotation(viewRotation * (bounds.center - Vector3.forward * distance), viewRotation);
                camera.nearClipPlane = Mathf.Max(.0001f, size * .001f); camera.farClipPlane = distance + bounds.size.z + size * 4;
                preview.ambientColor = new Color(.65f, .65f, .65f, 1);
                preview.lights[0].intensity = 1.1f; preview.lights[0].transform.rotation = rotation * Quaternion.Euler(40, 210, 0);
                preview.lights[1].intensity = .5f; preview.lights[1].transform.rotation = rotation * Quaternion.Euler(340, 30, 0);
                if (transparentCompatibility)
                {
                    foreach (var source in items.SelectMany(item => item.Materials).Where(m => m != null).Distinct())
                    {
                        // 仅对支持这些绘制属性的透明材质创建临时副本；不修改来源材质或 Renderer。
                        if (!SupportsTransparentCompatibility(source)) continue;
                        // 队列保证全部背面先于正面；透明表面不再预写深度挡住内侧。
                        var back = new Material(source) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 2990 };
                        backMaterials.Add(source, back);
                        back.SetFloat("_ColorMask", 15); back.SetFloat("_ZWrite", 0); back.SetFloat("_ZTest", 4);
                        back.SetFloat("_Cull", (float)CullMode.Front);
                        back.SetShaderPassEnabled("FORWARD_ADD", false);
                        var front = new Material(source) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 2991 };
                        frontMaterials.Add(source, front);
                        front.SetFloat("_ZWrite", 0);
                        front.SetFloat("_Cull", (float)CullMode.Back);
                    }
                    foreach (var item in items)
                        for (int i = 0; i < item.Mesh.subMeshCount; i++)
                        {
                            var source = item.Materials[Mathf.Min(i, item.Materials.Length - 1)];
                            if (source != null && backMaterials.TryGetValue(source, out var back))
                                preview.DrawMesh(item.Mesh, item.Matrix, back, i);
                        }
                }
                foreach (var item in items)
                    for (int i = 0; i < item.Mesh.subMeshCount; i++)
                    {
                        var material = item.Materials[Mathf.Min(i, item.Materials.Length - 1)];
                        if (material != null) preview.DrawMesh(item.Mesh, item.Matrix,
                            frontMaterials.TryGetValue(material, out var color) ? color : material, i);
                    }
                var used = items.SelectMany(item => item.Materials).Where(m => m != null)
                    .Concat(backMaterials.Values).Concat(frontMaterials.Values).Distinct().ToArray();
                foreach (var material in used)
                    for (int pass = 0; pass < material.passCount; pass++)
                        if (material.GetShaderPassEnabled(material.GetPassName(pass)) &&
                            !string.Equals(material.shader.FindPassTagValue(pass, new ShaderTagId("LightMode")).name,
                                "Never", StringComparison.OrdinalIgnoreCase))
                            ShaderUtil.CompilePass(material, pass, true);
                preview.Render();
                // Close the preview before error reporting so failure restores Unity's render state too.
                previewOpened = false;
                var rendered = preview.EndPreview() as RenderTexture;
                var failedShaders = used.Where(m => HasBlockingShaderError(m.shader))
                    .GroupBy(m => m.shader).ToArray();
                if (failedShaders.Length > 0)
                {
                    foreach (var failed in failedShaders)
                    {
                        var shader = failed.Key;
                        string materials = string.Join("\n", failed.Select(m =>
                            "材质=" + m.name + "; 路径=" + AssetDatabase.GetAssetPath(m)).Take(8));
                        var errors = ShaderUtil.GetShaderMessages(shader)
                            .Where(message => message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error &&
                                !IsNeverPassError(shader, message)).ToArray();
                        string details = "Shader=" + shader.name + "; 路径=" + AssetDatabase.GetAssetPath(shader) + "\n" + materials + "\n" +
                            (errors.Length == 0 ? "Unity报告该Shader存在错误，但未提供详细编译消息。" : string.Join("\n", errors.Take(5).Select(message =>
                                "编译错误=" + message.message + "; 文件=" + message.file + "; 行=" + message.line + "; 平台=" + message.platform)));
                        WardrobeDiagnostics.Record("截图", "着色器编译失败", details);
                    }
                    throw new InvalidOperationException("本次截图着色器编译失败，未保存图片。\nShader：" +
                        string.Join("、", failedShaders.Select(group => group.Key.name).Take(3)) + "\n具体材质和编译错误已写入诊断记录。");
                }
                if (rendered == null) throw new InvalidOperationException("未获得有效截图。");
                var previousTarget = RenderTexture.active;
                var scaled = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGB32);
                try
                {
                    // PreviewRenderUtility 会按编辑器 DPI 放大目标，必须缩放完整画面，不能只读左下256像素。
                    Graphics.Blit(rendered, scaled);
                    RenderTexture.active = scaled;
                    result = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                    result.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); result.Apply();
                }
                finally { RenderTexture.active = previousTarget; RenderTexture.ReleaseTemporary(scaled); }
                var pixels = result.GetPixels32();
                if (!pixels.Any(p => p.a > 16)) throw new InvalidOperationException("截图为空，请换一个拍摄角度，或手动拖入图标。");
                var captured = result; result = null; captured.hideFlags = HideFlags.HideAndDontSave; return captured;
            }
            finally
            {
                // CompilePass/DrawMesh/Render can also throw before the normal EndPreview call.
                if (previewOpened)
                {
                    try { preview.EndPreview(); }
                    catch (Exception e) { WardrobeDiagnostics.Record("截图", "结束预览异常", e.ToString()); }
                }
                ShaderUtil.allowAsyncCompilation = previousAsync;
                if (result != null) UnityEngine.Object.DestroyImmediate(result);
                preview?.Cleanup();
                foreach (var material in backMaterials.Values.Concat(frontMaterials.Values)) UnityEngine.Object.DestroyImmediate(material);
                foreach (var mesh in baked) UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        internal static string BackupPath(Texture2D icon)
        {
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(icon));
            if (string.IsNullOrEmpty(guid)) throw new InvalidOperationException("图标没有有效的资源关联，未覆盖。");
            return "Library/Zincat/IconBackups/" + guid + ".previous.png";
        }

        private static Texture2D SavePng(byte[] png, Texture2D overwrite, string outputFolder)
        {
            bool updating = overwrite != null;
            string path;
            if (updating)
            {
                path = AssetDatabase.GetAssetPath(overwrite);
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    throw new InvalidOperationException("原自动图标不是可写的工程 PNG，未覆盖。请先清除图标，再重新截图。");
            }
            else
            {
                string folder = outputFolder ?? WardrobeIconStorage.Folder(null);
                WardrobeIconStorage.EnsureFolder(folder);
                path = folder + "/Part_" + Guid.NewGuid().ToString("N") + ".png";
            }
            string backup = updating ? BackupPath(overwrite) : null;
            string temporary = Path.Combine(Path.GetDirectoryName(path), ".zincat-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool replaced = false;
            try
            {
                File.WriteAllBytes(temporary, png);
                if (updating)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Copy(path, backup, true); // 备份失败就停止，不触碰原图。
                    File.Replace(temporary, path, null); // 同目录原子替换，不改 .meta。
                }
                else File.Move(temporary, path);
                replaced = true;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                if (!updating)
                {
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    importer.textureType = TextureImporterType.Default; importer.alphaIsTransparency = true;
                    importer.mipmapEnabled = false; importer.maxTextureSize = 256;
                    WardrobeIconStorage.SetCompression(importer);
                    importer.SaveAndReimport();
                }
                else
                {
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    var desktop = importer.GetPlatformTextureSettings("Standalone");
                    if (importer.textureCompression != TextureImporterCompression.CompressedLQ || !importer.crunchedCompression || importer.compressionQuality != 50 ||
                        desktop.overridden && (desktop.textureCompression != TextureImporterCompression.CompressedLQ || !desktop.crunchedCompression || desktop.compressionQuality != 50 || desktop.format != TextureImporterFormat.Automatic))
                    { WardrobeIconStorage.SetCompression(importer); importer.SaveAndReimport(); }
                }
                var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (imported == null) throw new InvalidOperationException("Unity 未能读取生成的 PNG。");
                return imported;
            }
            catch (Exception error)
            {
                if (updating && replaced)
                {
                    try { File.Copy(backup, path, true); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport); }
                    catch (Exception restore) { throw new InvalidOperationException("更新失败且自动恢复失败；原图备份在 " + backup + "。" + restore.Message, error); }
                    throw new InvalidOperationException("图标更新失败，已恢复原图。" + error.Message, error);
                }
                throw;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
#endif
