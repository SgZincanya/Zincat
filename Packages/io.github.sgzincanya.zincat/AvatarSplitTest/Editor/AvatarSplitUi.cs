#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Zincat.Clothing.Editor;

namespace Zincat.AvatarSplitTest
{
    internal static class SplitUi
    {
        public static bool Dark => EditorGUIUtility.isProSkin;
        public static Color Background => Dark ? (Color)new Color32(52,52,52,255) : new Color32(242,242,245,255);
        public static Color Surface => Dark ? (Color)new Color32(58,58,58,255) : Color.white;
        public static Color Selected => Dark ? (Color)new Color32(44,69,99,255) : new Color32(232,241,255,255);
        public static Color Border => Dark ? (Color)new Color32(80,82,86,255) : new Color32(193,195,201,255);
        public static Color Text => WardrobeUiStyle.Text;
        public static Color Muted => WardrobeUiStyle.Muted;
        public static Label Label(string text, int size = 13, bool muted = false)
        {
            var label = new Label(text);
            label.style.fontSize = size; label.style.color = muted ? Muted : Text;
            label.style.unityTextAlign = TextAnchor.UpperLeft;
            label.style.whiteSpace = WhiteSpace.Normal; label.style.flexShrink = 0;
            return label;
        }
        public static VisualElement Row()
        {
            var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center; row.style.flexShrink = 0; return row;
        }
        public static void Box(VisualElement element, int padding = 14)
        {
            element.style.backgroundColor = Surface;
            element.style.paddingTop = element.style.paddingBottom = padding;
            element.style.paddingLeft = element.style.paddingRight = padding;
            element.style.borderTopLeftRadius = element.style.borderTopRightRadius = 8;
            element.style.borderBottomLeftRadius = element.style.borderBottomRightRadius = 8;
            Outline(element, Border);
        }
        public static void Outline(VisualElement element, Color color)
        {
            element.style.borderTopWidth = element.style.borderBottomWidth = 1;
            element.style.borderLeftWidth = element.style.borderRightWidth = 1;
            element.style.borderTopColor = element.style.borderBottomColor = color;
            element.style.borderLeftColor = element.style.borderRightColor = color;
        }
        public static Button Button(string text, System.Action action, bool primary = false)
        {
            var button = new Button(action) { text = text, name = primary ? "wardrobe-primary-action" : "" };
            button.style.minHeight = 32; button.style.marginLeft = 0; button.style.marginRight = 6;
            return button;
        }
        public static void Apply(VisualElement root)
        {
            // Toolbar icon buttons must not inherit the full-size action button treatment.
            root.Query<ToolbarSearchField>().ForEach(search => search.Query<Button>().ForEach(button =>
                button.AddToClassList("wardrobe-state-button")));
            WardrobeUiStyle.Apply(root);
            root.style.fontSize = 13;
            root.Query<Toggle>().ForEach(t => { t.style.minHeight = 30; t.style.fontSize = 13; });
            root.Query<ToolbarSearchField>().ForEach(StyleSearch);
        }
        static void StyleSearch(ToolbarSearchField search)
        {
            search.style.height = search.style.minHeight = search.style.maxHeight = 32;
            search.style.backgroundImage = StyleKeyword.None;
            search.style.backgroundColor = Dark ? (Color)new Color32(45,45,47,255) : Color.white;
            search.style.borderTopLeftRadius = search.style.borderTopRightRadius = 6;
            search.style.borderBottomLeftRadius = search.style.borderBottomRightRadius = 6;
            Outline(search, Border);
            search.style.marginTop = search.style.marginBottom = 0;
            search.style.paddingTop = search.style.paddingBottom = 0;
            search.style.paddingLeft = search.style.paddingRight = 6;
            search.style.alignItems = Align.Center;
            search.Query<Button>().ForEach(button =>
            {
                button.style.width = button.style.minWidth = button.style.maxWidth = 20;
                button.style.height = button.style.minHeight = button.style.maxHeight = 20;
                button.style.paddingTop = button.style.paddingBottom = 0;
                button.style.paddingLeft = button.style.paddingRight = 0;
                button.style.marginTop = button.style.marginBottom = 0;
                button.style.alignSelf = Align.Center;
                button.style.flexShrink = 0;
                button.style.backgroundColor = Color.clear;
                button.style.borderTopWidth = button.style.borderBottomWidth = 0;
                button.style.borderLeftWidth = button.style.borderRightWidth = 0;
            });
            search.Query<TextField>().ForEach(field =>
            {
                field.style.height = field.style.minHeight = 30;
                field.style.marginTop = field.style.marginBottom = 0;
                field.style.paddingTop = field.style.paddingBottom = 7;
                field.style.fontSize = 13;
                var input = field.Q<VisualElement>(className: "unity-text-input");
                if (input == null) return;
                input.style.height = input.style.minHeight = 16;
                input.style.paddingTop = input.style.paddingBottom = 0;
                input.style.unityTextAlign = TextAnchor.MiddleLeft;
                input.style.borderTopWidth = input.style.borderBottomWidth = 0;
                input.style.borderLeftWidth = input.style.borderRightWidth = 0;
                input.style.backgroundColor = Color.clear;
                input.style.justifyContent = Justify.Center;
                input.Query<TextElement>().ForEach(text =>
                {
                    text.style.unityTextAlign = TextAnchor.MiddleLeft;
                    text.style.paddingTop = text.style.paddingBottom = 0;
                    text.style.marginTop = text.style.marginBottom = 0;
                    text.style.height = 16;
                    text.style.alignSelf = Align.Stretch;
                });
            });
            if (search.ClassListContains("split-search-styled")) return;
            search.AddToClassList("split-search-styled");
            var placeholder = Label("搜索衣服名称", 13, true);
            placeholder.name = "split-search-placeholder";
            placeholder.pickingMode = PickingMode.Ignore;
            placeholder.style.position = Position.Absolute;
            placeholder.style.left = 30; placeholder.style.right = 30;
            placeholder.style.top = placeholder.style.bottom = 0;
            placeholder.style.unityTextAlign = TextAnchor.MiddleLeft;
            placeholder.style.display = string.IsNullOrEmpty(search.value) ? DisplayStyle.Flex : DisplayStyle.None;
            search.Add(placeholder);
            search.RegisterValueChangedCallback(evt => placeholder.style.display =
                string.IsNullOrEmpty(evt.newValue) ? DisplayStyle.Flex : DisplayStyle.None);
            search.RegisterCallback<FocusInEvent>(_ =>
            {
                StyleSearch(search);
                Outline(search, Dark ? (Color)new Color32(124,182,255,255) : new Color32(23,105,210,255));
            });
            search.RegisterCallback<FocusOutEvent>(_ => StyleSearch(search));
        }
    }
}
#endif
