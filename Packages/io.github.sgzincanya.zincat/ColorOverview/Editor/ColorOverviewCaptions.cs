#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEngine;

namespace Zincat.ColorOverview
{
    // Atlas is requested once per sheet and copied before drawing, so Unicode captions
    // need no external font files and do not depend on subsequent dynamic atlas rebuilds.
    internal sealed class ColorOverviewCaptions : IDisposable
    {
        private readonly Font font;
        private readonly Color32[] atlas;
        private readonly int width, height;
        public ColorOverviewCaptions(string allText)
        {
            font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 24);
            if (!font) throw new InvalidOperationException("无法加载截图标签字体。");
            font.hideFlags = HideFlags.HideAndDontSave;
            string chars = new string((allText ?? "").Distinct().ToArray());
            font.RequestCharactersInTexture(chars, 24, FontStyle.Normal);
            font.RequestCharactersInTexture(chars, 36, FontStyle.Normal);
            var source = font.material.mainTexture;
            width = source.width; height = source.height;
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, target); RenderTexture.active = target;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0); readable.Apply();
                atlas = readable.GetPixels32();
            }
            catch { UnityEngine.Object.DestroyImmediate(font); throw; }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); if (readable) UnityEngine.Object.DestroyImmediate(readable); }
        }
        public void Draw(Texture2D target, string text, int x, int top, int scale, int maxWidth)
        {
            int size = scale * 12, start = x, row = 0;
            foreach (char c in text)
            {
                if (!font.GetCharacterInfo(c, out var info, size, FontStyle.Normal)) continue;
                if (x + info.advance > start + maxWidth)
                { x = start; top += size + 3; if (++row > 1) break; }
                int gw = info.maxX - info.minX, gh = info.maxY - info.minY;
                for (int yy = 0; yy < gh; yy++) for (int xx = 0; xx < gw; xx++)
                {
                    int px = x + info.minX + xx, py = target.height - 1 - (top + size - info.maxY + yy);
                    if (px < 0 || px >= target.width || py < 0 || py >= target.height) continue;
                    float u = (xx + .5f) / gw, v = 1 - (yy + .5f) / gh;
                    Vector2 uv = Vector2.Lerp(Vector2.Lerp(info.uvBottomLeft, info.uvBottomRight, u), Vector2.Lerp(info.uvTopLeft, info.uvTopRight, u), v);
                    byte alpha = atlas[Mathf.Clamp((int)(uv.y * height), 0, height - 1) * width + Mathf.Clamp((int)(uv.x * width), 0, width - 1)].a;
                    if (alpha == 0) continue;
                    var original = target.GetPixel(px, py);
                    target.SetPixel(px, py, Color.Lerp(original, new Color32(231, 235, 245, 255), alpha / 255f));
                }
                x += info.advance;
            }
        }
        public void Dispose() { if (font) UnityEngine.Object.DestroyImmediate(font); }
    }
}
#endif
