// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zincat.Lighting.Editor
{
    public enum LightChannel { Minimum, Maximum, Monochrome }
    public enum ShaderFamily { Unsupported, LilToon, Poiyomi }

    public static class MaterialSupport
    {
        public static ShaderFamily Identify(Material material)
        {
            if (material == null || material.shader == null) return ShaderFamily.Unsupported;
            string name = material.shader.name;
            if (name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0) return ShaderFamily.LilToon;
            if (name.IndexOf("Poiyomi", StringComparison.OrdinalIgnoreCase) >= 0) return ShaderFamily.Poiyomi;
            return ShaderFamily.Unsupported;
        }

        public static bool IsLocked(Material material)
        {
            return Identify(material) == ShaderFamily.Poiyomi &&
                material.shader.name.StartsWith("Hidden/Locked/", StringComparison.OrdinalIgnoreCase);
        }

        public static string PoiyomiReadinessIssue(Material material, IEnumerable<string> properties)
        {
            if (Identify(material) != ShaderFamily.Poiyomi) return null;
            if (!IsLocked(material)) return "材质尚未锁定。请准备 Poiyomi 材质。";
            foreach (string property in properties)
            {
                int index = material.shader.FindPropertyIndex(property);
                if (index < 0) return "当前 Shader 缺少属性 " + property + "。请重新准备材质。";
                string animated = material.GetTag(property + "Animated", false, "");
                if (animated == "2") return property + " 使用 RA 分槽动画；请改为 A，或排除对应 Renderer。";
                // Poiyomi 10 keeps DoNotLock properties dynamic even without an Animated tag.
                // A retained property alone is insufficient: ordinary properties may still be baked into HLSL.
                if (animated == "1" || IsExemptFromLocking(material.shader, index)) continue;
                return property + " 未启用 A 动画标记（当前值：" + (animated.Length == 0 ? "空" : animated) + "）。请准备 Poiyomi 材质。";
            }
            return null;
        }

        private static bool IsExemptFromLocking(Shader shader, int index)
        {
            return shader.GetPropertyAttributes(index).Contains("DoNotLock") ||
                (shader.GetPropertyType(index) != ShaderPropertyType.Texture &&
                 (shader.GetPropertyFlags(index) & ShaderPropertyFlags.NonModifiableTextureData) != 0) ||
                shader.GetPropertyDescription(index).EndsWith("NL", StringComparison.Ordinal);
        }

        public static string[] Properties(ShaderFamily family, LightChannel channel, bool additive)
        {
            if (family == ShaderFamily.LilToon)
            {
                switch (channel)
                {
                    case LightChannel.Minimum: return new[] { "_LightMinLimit" };
                    case LightChannel.Maximum: return new[] { "_LightMaxLimit" };
                    default: return new[] { "_MonochromeLighting" };
                }
            }
            if (family == ShaderFamily.Poiyomi)
            {
                switch (channel)
                {
                    case LightChannel.Minimum: return new[] { "_LightingMinLightBrightness" };
                    case LightChannel.Maximum: return additive ? new[] { "_LightingCap", "_LightingAdditiveLimit" } : new[] { "_LightingCap" };
                    default: return additive ? new[] { "_LightingMonochromatic", "_LightingAdditiveMonochromatic" } : new[] { "_LightingMonochromatic" };
                }
            }
            return Array.Empty<string>();
        }

        public static Material PrepareCopy(Material source)
        {
            if (Identify(source) == ShaderFamily.Unsupported) throw new ArgumentException("不支持该材质。", nameof(source));
            var copy = new Material(source) { name = source.name };
            try
            {
                if (!IsLocked(source)) return copy;
                Type optimizer = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Thry.ThryEditor.ShaderOptimizer", false)).FirstOrDefault(t => t != null);
                MethodInfo getOriginal = optimizer?.GetMethod("GetOriginalShader", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(Material), typeof(bool) }, null);
                if (getOriginal == null) throw new InvalidOperationException("已锁定的 Poiyomi 材质需要兼容的 ThryEditor 原 Shader 查询接口。请更新 Poiyomi 或先手动解锁材质。");
                Shader original = getOriginal.Invoke(null, new object[] { source, false }) as Shader;
                if (original == null || original.name.StartsWith("Hidden/Locked/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("无法找到已锁定材质的原 Shader：" + source.name);
                // Restore a temporary staging object. Keep the old optimized shader available for rollback and other users.
                copy.shader = original;
                copy.renderQueue = source.renderQueue;
                copy.SetOverrideTag("RenderType", source.GetTag("RenderType", false, ""));
                string keywords = source.GetTag("OriginalKeywords", false, string.Join(" ", source.shaderKeywords));
                copy.shaderKeywords = keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string suffix = optimizer.GetMethod("GetRenamedPropertySuffix", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(Material) }, null)?.Invoke(null, new object[] { source }) as string;
                for (int i = 0; i < original.GetPropertyCount(); i++)
                {
                    string property = original.GetPropertyName(i);
                    ShaderPropertyType type = original.GetPropertyType(i);
                    if (!string.IsNullOrEmpty(suffix) && source.GetTag(property + "Animated", false, "") == "2")
                    {
                        string renamed = property + "_" + suffix;
                        if (source.HasProperty(renamed)) CopyValue(source, renamed, copy, property, type);
                    }
                    if (type == ShaderPropertyType.Texture && copy.GetTexture(property) == null)
                    {
                        string textureGuid = source.GetTag("_stripped_tex_" + property, false, "");
                        if (!string.IsNullOrEmpty(textureGuid))
                            copy.SetTexture(property, AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(textureGuid)));
                    }
                }
                if (copy.HasProperty("_ShaderOptimizerEnabled")) copy.SetFloat("_ShaderOptimizerEnabled", 0);
                return copy;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(copy);
                throw;
            }
        }

        public static void AllowAnimation(Material material, string property)
        {
            if (!material.HasProperty(property)) return;
            if (material.GetTag(property + "Animated", false, "") == "2")
                throw new InvalidOperationException($"材质 {material.name} 的 {property} 使用 RA 分槽动画；请将该光照属性改为 A，或排除这个 Renderer。新控制不会覆盖已有 RA 配置。");
            material.SetOverrideTag(property + "Animated", "1");
        }

        private static void CopyValue(Material from, string source, Material to, string destination, ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color: to.SetColor(destination, from.GetColor(source)); break;
                case ShaderPropertyType.Vector: to.SetVector(destination, from.GetVector(source)); break;
                case ShaderPropertyType.Texture:
                    to.SetTexture(destination, from.GetTexture(source));
                    to.SetTextureScale(destination, from.GetTextureScale(source));
                    to.SetTextureOffset(destination, from.GetTextureOffset(source));
                    break;
                case ShaderPropertyType.Int: to.SetInteger(destination, from.GetInteger(source)); break;
                default: to.SetFloat(destination, from.GetFloat(source)); break;
            }
        }
    }
}
