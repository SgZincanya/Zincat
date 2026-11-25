// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Zincat.Lighting.Editor
{
    internal static class LightingExtraControls
    {
        public static bool IsExtra(LightChannel channel) => channel >= LightChannel.Emission;

        public static Dictionary<string, float> Collect(Renderer renderer, LightChannel channel,
            HashSet<Material> skipped, out string issue)
        {
            issue = null;
            var materials = renderer.sharedMaterials.Where(m => m != null).Distinct().ToArray();
            var eligible = materials.Where(m => !skipped.Contains(m) && MaterialSupport.Identify(m) != ShaderFamily.Unsupported)
                .ToDictionary(m => m, m => Values(m, channel));
            var result = new Dictionary<string, float>();
            foreach (string property in eligible.Values.SelectMany(values => values.Keys).Distinct())
            {
                float baseline = eligible.Values.First(values => values.ContainsKey(property))[property];
                string root = property.Split('.')[0];
                bool conflict = materials.Where(m => m.HasProperty(root) &&
                    (channel != LightChannel.Emission || MaterialSupport.Identify(m) == ShaderFamily.Unsupported ||
                     MaterialSupport.Properties(m, channel, false).Contains(property))).Any(m =>
                    !eligible.TryGetValue(m, out var values) || !values.TryGetValue(property, out float other) ||
                    !Mathf.Approximately(other, baseline));
                if (conflict)
                {
                    if (issue == null) issue = renderer.name + " 的 " + root + " 在材质槽之间存在不同基准或不受控材质，已跳过对应额外属性。";
                    continue;
                }
                result.Add(property, baseline);
            }
            // Color temperature must control RGB together.
            if (channel == LightChannel.Temperature && result.Count != 4) result.Clear();
            if (channel == LightChannel.Saturation && result.Keys.Any(p => p.StartsWith("_MainTexHSVG.")) &&
                result.Keys.Count(p => p.StartsWith("_MainTexHSVG.")) != 4)
                foreach (string property in result.Keys.Where(p => p.StartsWith("_MainTexHSVG.")).ToArray())
                    result.Remove(property);
            return result;
        }

        private static Dictionary<string, float> Values(Material material, LightChannel channel)
        {
            var result = new Dictionary<string, float>();
            foreach (string property in MaterialSupport.Properties(material, channel, false))
            {
                string[] parts = property.Split('.');
                float value;
                if (parts.Length == 1) value = material.GetFloat(property);
                else if (parts[0] == "_MainTexHSVG")
                {
                    Vector4 vector = material.GetVector(parts[0]);
                    value = parts[1] == "x" ? vector.x : parts[1] == "y" ? vector.y : parts[1] == "z" ? vector.z : vector.w;
                }
                else
                {
                    Color color = material.GetColor(parts[0]);
                    value = parts[1] == "r" ? color.r : parts[1] == "g" ? color.g : parts[1] == "b" ? color.b : color.a;
                }
                result.Add(property, value);
            }
            return result;
        }

        public static float Evaluate(LightChannel channel, string property, float baseline, float value)
        {
            switch (channel)
            {
                case LightChannel.Emission: return baseline * value;
                case LightChannel.Saturation:
                    if (property == "_Saturation") return (baseline + 1) * value - 1;
                    return property.EndsWith(".y") ? baseline * value : baseline;
                case LightChannel.Temperature:
                    if (property.EndsWith(".a")) return baseline;
                    Color tint = value <= 0.5f
                        ? Color.Lerp(new Color(1, 0.72f, 0.45f), Color.white, value * 2)
                        : Color.Lerp(Color.white, new Color(0.55f, 0.75f, 1), (value - 0.5f) * 2);
                    return baseline * (property.EndsWith(".r") ? tint.r : property.EndsWith(".g") ? tint.g : tint.b);
                case LightChannel.Monochrome: return 1 - value;
                default: return value;
            }
        }
    }
}
