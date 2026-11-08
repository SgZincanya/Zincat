#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zincat.Clothing.Editor
{
    /// <summary>仅统一编辑窗口的视觉样式，不参与菜单生成或场景状态。</summary>
    internal static class WardrobeUiStyle
    {
        internal static Color Text => EditorGUIUtility.isProSkin ? new Color32(236, 236, 238, 255) : new Color32(36, 36, 38, 255);
        internal static Color Muted => EditorGUIUtility.isProSkin ? new Color32(185, 189, 196, 255) : new Color32(97, 97, 102, 255);
        internal static Color Link => EditorGUIUtility.isProSkin ? new Color32(124, 182, 255, 255) : new Color32(23, 105, 210, 255);
        private static Color Primary => new Color32(23, 105, 210, 255);
        private static Color Line => EditorGUIUtility.isProSkin ? new Color32(78, 80, 84, 255) : new Color32(193, 195, 201, 255);
        private static Color Control => EditorGUIUtility.isProSkin ? new Color32(72, 72, 74, 255) : new Color32(235, 235, 239, 255);

        internal static void Apply(VisualElement root)
        {
            root.style.color = Text;
            root.style.fontSize = 12;
            root.style.unityFontStyleAndWeight = FontStyle.Normal;
            root.Query<TextElement>().ForEach(text => text.style.unityFontStyleAndWeight = FontStyle.Normal);
            root.Query<Button>().ForEach(button =>
            {
                // 保留模板选择卡与小方框的专用选中状态、尺寸。
                if (button.ClassListContains("wardrobe-state-button"))
                {
                    StateFocus(button);
                    return;
                }
                Action(button);
            });
            root.Query<TextField>().ForEach(field => Field(field, field.Q<VisualElement>(className: "unity-text-input")));
            root.Query<ObjectField>().ForEach(field => Field(field, field.Q<VisualElement>(className: "unity-base-field__input")));
            root.Query<Toggle>().ForEach(toggle =>
            {
                toggle.style.minHeight = 24;
                toggle.style.unityFontStyleAndWeight = FontStyle.Normal;
            });
        }

        private static void StateFocus(Button button)
        {
            if (button.ClassListContains("wardrobe-styled-state")) return;
            button.AddToClassList("wardrobe-styled-state");
            var idle = button.style.borderLeftColor.value;
            void SetColor(Color color)
            {
                button.style.borderTopColor = button.style.borderBottomColor = color;
                button.style.borderLeftColor = button.style.borderRightColor = color;
            }
            button.RegisterCallback<FocusInEvent>(_ => SetColor(Link));
            button.RegisterCallback<FocusOutEvent>(_ => SetColor(idle));
        }

        private static void Field(VisualElement field, VisualElement input)
        {
            field.style.minWidth = 0;
            field.style.minHeight = 28;
            if (input == null) return;
            input.style.minWidth = 0;
            input.style.minHeight = 28;
            input.style.color = Text;
            input.style.backgroundColor = EditorGUIUtility.isProSkin ? (Color)new Color32(48, 48, 50, 255) : Color.white;
            Border(input, Line, 1, 4);
            var label = field.Q<Label>(className: "unity-base-field__label");
            if (label != null)
            {
                label.style.minWidth = 88;
                label.style.width = 88;
                label.style.color = Muted;
                label.style.unityTextAlign = TextAnchor.MiddleLeft;
            }
            if (field.ClassListContains("wardrobe-styled-field")) return;
            field.AddToClassList("wardrobe-styled-field");
            field.RegisterCallback<FocusInEvent>(_ => Border(input, Link, 1, 4));
            field.RegisterCallback<FocusOutEvent>(_ => Border(input, Line, 1, 4));
        }

        private static void Action(Button button)
        {
            bool primary = button.name == "wardrobe-primary-action" || button.name == "vocabulary-more";
            bool quiet = button.name == "batch-delete-preset" || button.name == "wardrobe-remove-outfit";
            bool menuRow = button.name == "existing-menu-row";
            Color idle = primary ? Primary : quiet ? Color.clear : menuRow
                ? (EditorGUIUtility.isProSkin ? (Color)new Color32(58, 58, 58, 255) : Color.white) : Control;
            Color hover = primary ? new Color32(30, 114, 220, 255) : EditorGUIUtility.isProSkin
                ? (Color)new Color32(84, 84, 87, 255) : new Color32(223, 225, 230, 255);
            Color pressed = primary ? new Color32(18, 85, 177, 255) : EditorGUIUtility.isProSkin
                ? (Color)new Color32(64, 64, 67, 255) : new Color32(210, 213, 219, 255);
            button.style.minHeight = 28;
            button.style.opacity = button.enabledInHierarchy ? 1 : .45f;
            button.style.flexShrink = 0;
            button.style.paddingLeft = button.style.paddingRight = 10;
            button.style.color = primary ? Color.white : quiet ? Muted : Text;
            button.style.backgroundColor = idle;
            Border(button, primary ? Primary : Line, quiet ? 0 : 1, menuRow ? 3 : 6);
            if (button.ClassListContains("wardrobe-styled-action")) return;
            button.AddToClassList("wardrobe-styled-action");
            bool focused = false, hovering = false, down = false;
            void Paint()
            {
                button.style.backgroundColor = down ? pressed : hovering ? hover : idle;
                Border(button, focused ? Link : primary ? Primary : Line, focused || !quiet ? 1 : 0, menuRow ? 3 : 6);
            }
            button.RegisterCallback<MouseEnterEvent>(_ => { hovering = true; Paint(); });
            button.RegisterCallback<MouseLeaveEvent>(_ => { hovering = down = false; Paint(); });
            button.RegisterCallback<PointerDownEvent>(_ => { down = true; Paint(); });
            button.RegisterCallback<PointerUpEvent>(_ => { down = false; Paint(); });
            button.RegisterCallback<FocusInEvent>(_ => { focused = true; Paint(); });
            button.RegisterCallback<FocusOutEvent>(_ => { focused = down = false; Paint(); });
            button.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Space || evt.keyCode == KeyCode.Return) { down = true; Paint(); }
            });
            button.RegisterCallback<KeyUpEvent>(_ => { down = false; Paint(); });
        }

        private static void Border(VisualElement element, Color color, float width, float radius)
        {
            element.style.borderTopWidth = element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = element.style.borderRightWidth = width;
            element.style.borderTopColor = element.style.borderBottomColor = color;
            element.style.borderLeftColor = element.style.borderRightColor = color;
            element.style.borderTopLeftRadius = element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = element.style.borderBottomRightRadius = radius;
        }
    }
}
#endif
