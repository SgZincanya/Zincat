using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Zincat.PasswordLock.Editor
{
    // Native IMGUI inputs remain native; only grouping and action hierarchy are styled.
    internal static class PasswordLockUi
    {
        private static bool initialized, dark;
        private static readonly List<Texture2D> textures = new List<Texture2D>();
        internal static GUIStyle Title, Heading, Body, Caption, Panel, Primary, Secondary, Digit, Password, Foldout;
        internal static Color Background, Content, Muted, Accent, Signal;

        internal static void Ensure()
        {
            if (initialized && dark == EditorGUIUtility.isProSkin) return;
            foreach (var texture in textures) if (texture) Object.DestroyImmediate(texture);
            textures.Clear();
            dark = EditorGUIUtility.isProSkin;
            initialized = true;
            Background = Hex(dark ? "#202124" : "#F5F5F7");
            var surface = Hex(dark ? "#2B2C30" : "#FFFFFF");
            Content = Hex(dark ? "#F5F5F7" : "#1D1D1F");
            Muted = Hex(dark ? "#B8B8C0" : "#606068");
            Accent = Hex(dark ? "#0A64DA" : "#0062CC");
            Signal = Hex(dark ? "#FF9A90" : "#B3261E");
            Title = Label(24, FontStyle.Bold, Content);
            Heading = Label(14, FontStyle.Bold, Content);
            Body = Label(13, FontStyle.Normal, Content);
            Caption = Label(13, FontStyle.Normal, Muted);
            Panel = new GUIStyle { padding = new RectOffset(16, 16, 16, 16),
                margin = new RectOffset(0, 0, 0, 12), border = new RectOffset(9, 9, 9, 9) };
            Panel.normal.background = Rounded(surface);
            Primary = Button(Accent, Color.white, 14);
            var secondary = Hex(dark ? "#383A40" : "#EBEBEF");
            Secondary = Button(secondary, Content, 13);
            Digit = Button(secondary, Content, 21);
            Digit.fixedHeight = 48;
            Password = new GUIStyle(EditorStyles.textField) { fontSize = 18, fixedHeight = 38,
                padding = new RectOffset(10, 10, 6, 6) };
            Password.normal.textColor = Content;
            Password.focused.textColor = Content;
            Foldout = new GUIStyle(EditorStyles.foldout) { fontSize = 13, fixedHeight = 28 };
            Foldout.normal.textColor = Content;
            Foldout.onNormal.textColor = Content;
        }

        private static GUIStyle Label(int size, FontStyle weight, Color color)
        {
            var style = new GUIStyle(EditorStyles.label) { fontSize = size, fontStyle = weight,
                wordWrap = true, richText = false, padding = new RectOffset(0, 0, 0, 0) };
            style.normal.textColor = color;
            return style;
        }

        private static GUIStyle Button(Color background, Color foreground, int size)
        {
            var style = new GUIStyle(GUI.skin.button) { fontSize = size, fontStyle = FontStyle.Normal,
                fixedHeight = 36, border = new RectOffset(9, 9, 9, 9),
                padding = new RectOffset(12, 12, 8, 8), margin = new RectOffset(0, 0, 0, 0) };
            style.normal.background = Rounded(background);
            style.hover.background = Rounded(Color.Lerp(background, dark ? Color.white : Color.black, .08f));
            style.active.background = Rounded(Color.Lerp(background, dark ? Color.white : Color.black, .18f));
            style.focused.background = Rounded(background, background == Accent ? foreground : Accent);
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = foreground;
            style.onNormal = style.normal;
            style.onHover = style.hover;
            style.onActive = style.active;
            style.onFocused = style.focused;
            return style;
        }

        private static Texture2D Rounded(Color color, Color? outline = null)
        {
            const int size = 32;
            const float radius = 8;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + .5f, y + .5f);
                    var center = new Vector2(Mathf.Clamp(p.x, radius, size - radius), Mathf.Clamp(p.y, radius, size - radius));
                    float distance = Vector2.Distance(p, center);
                    var pixel = outline.HasValue && (distance > radius - 2 || x < 2 || y < 2 || x >= size - 2 || y >= size - 2)
                        ? outline.Value : color;
                    pixel.a *= Mathf.Clamp01(radius - distance + .5f);
                    pixels[y * size + x] = pixel;
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            textures.Add(texture);
            return texture;
        }

        private static Color Hex(string text)
        {
            ColorUtility.TryParseHtmlString(text, out var color);
            return color;
        }

        internal static void BeginPanel(string heading)
        {
            GUILayout.BeginVertical(Panel);
            if (!string.IsNullOrEmpty(heading))
            {
                GUILayout.Label(heading, Heading);
                GUILayout.Space(12);
            }
        }
        internal static void EndPanel() => GUILayout.EndVertical();
        internal static void Note(string text) => GUILayout.Label(text, Caption);
        internal static void Space() => GUILayout.Space(8);
        internal static void Message(string text, bool error = false)
        {
            var style = new GUIStyle(Body);
            style.normal.textColor = error ? Signal : Content;
            GUILayout.Label(text, style);
        }
    }
}
