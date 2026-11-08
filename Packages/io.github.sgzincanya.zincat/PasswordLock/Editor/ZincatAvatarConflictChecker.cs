#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Zincat.Tools
{
    // Standalone, read-only diagnostic. No VRChat SDK / MA / password-lock dependency.
    public sealed class ZincatAvatarConflictChecker : EditorWindow
    {
        internal sealed class Conflict
        {
            internal string path;
            internal Type type;
            internal Component[] components;
            internal bool SameObject => components.Select(c => c.gameObject.GetInstanceID()).Distinct().Count() == 1;
        }

        GameObject avatar;
        GameObject scannedAvatar;
        bool includeLights = true;
        bool skipGenerated = true;
        bool scanned;
        Vector2 scroll;
        List<Conflict> conflicts = new List<Conflict>();

        [MenuItem("Zincat/角色路径冲突检查")]
        public static void Open()
        {
            OpenForAvatar(GuessAvatar(Selection.activeGameObject));
        }

        public static void OpenForAvatar(GameObject root)
        {
            var window = GetWindow<ZincatAvatarConflictChecker>("角色冲突检查");
            window.minSize = new Vector2(440, 320);
            window.avatar = root;
            window.scanned = false;
            window.conflicts.Clear();
            if (root) window.Scan();
        }

        static GameObject GuessAvatar(GameObject selected)
        {
            if (!selected) return null;
            for (var t = selected.transform; t; t = t.parent)
                if (t.GetComponents<Component>().Any(c => c && c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                    return t.gameObject;
            return selected;
        }

        void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("查找导致密码锁无法生成的组件", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("检查所选角色内所有 Renderer，以及可选的 Light（包含未激活对象）。只读取信息，不改名、不拆分、不删除。", MessageType.Info);
            EditorGUI.BeginChangeCheck();
            avatar = (GameObject)EditorGUILayout.ObjectField("角色根对象", avatar, typeof(GameObject), true);
            includeLights = EditorGUILayout.Toggle("同时检查灯光", includeLights);
            skipGenerated = EditorGUILayout.Toggle("跳过密码锁生成节点", skipGenerated);
            if (EditorGUI.EndChangeCheck()) { scanned = false; conflicts.Clear(); }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("使用当前选中对象"))
                { avatar = GuessAvatar(Selection.activeGameObject); scanned = false; conflicts.Clear(); }
                using (new EditorGUI.DisabledScope(!avatar))
                    if (GUILayout.Button("检查", GUILayout.Height(26))) Scan();
            }
            if (!scanned) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("发现 " + conflicts.Count + " 组冲突", EditorStyles.boldLabel);
            if (conflicts.Count == 0)
            {
                EditorGUILayout.HelpBox("当前范围未发现重复的“路径＋组件类型”。若仍报错，确认选择的是完整角色；也可能是其他插件在构建阶段生成了冲突对象。", MessageType.Info);
                return;
            }
            if (GUILayout.Button("复制检查报告")) EditorGUIUtility.systemCopyBuffer = Report();
            EditorGUILayout.HelpBox("这里按整个角色检查。密码锁设置为局部隐藏或配置了例外时，应再核对冲突对象是否在实际隐藏范围内。重命名可能影响既有动画，修改前请备份。", MessageType.Info);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var conflict in conflicts)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(conflict.SameObject ? "同一物体：多个同类型组件" : "不同物体：层级名称路径相同", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("动画路径", DisplayPath(conflict.path), EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField("组件类型", conflict.type.Name);
                    foreach (var component in conflict.components)
                    {
                        if (!component) { EditorGUILayout.LabelField("对象已删除，请重新检查。"); continue; }
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.ObjectField(component.gameObject, typeof(GameObject), true);
                            if (GUILayout.Button("定位", GUILayout.Width(48)))
                            { Selection.activeGameObject = component.gameObject; EditorGUIUtility.PingObject(component.gameObject); }
                        }
                        EditorGUILayout.LabelField(IndexedPath(component.transform), EditorStyles.miniLabel);
                    }
                    EditorGUILayout.LabelField(conflict.SameObject
                        ? "核对组件是否都需要；需要保留时，可考虑拆分到独立子对象。"
                        : "检查同级重名节点。名称前的横线、空格也算名称的一部分。", EditorStyles.wordWrappedLabel);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        void Scan()
        {
            scannedAvatar = avatar;
            conflicts = FindConflicts(avatar, includeLights, skipGenerated);
            scanned = true;
        }

        internal static List<Conflict> FindConflicts(GameObject root, bool lights, bool skip)
        {
            if (!root) throw new ArgumentNullException(nameof(root));
            var components = new List<Component>();
            components.AddRange(root.GetComponentsInChildren<Renderer>(true));
            if (lights) components.AddRange(root.GetComponentsInChildren<Light>(true));
            return components.Where(c => !skip || !UnderGenerated(c.transform, root.transform))
                .GroupBy(c => new { path = AnimationUtility.CalculateTransformPath(c.transform, root.transform), type = c.GetType() })
                .Where(group => group.Count() > 1)
                .OrderBy(group => group.Key.path, StringComparer.Ordinal)
                .ThenBy(group => group.Key.type.FullName, StringComparer.Ordinal)
                .Select(group => new Conflict { path = group.Key.path, type = group.Key.type, components = group.ToArray() }).ToList();
        }

        static bool UnderGenerated(Transform t, Transform root)
        {
            for (; t; t = t.parent)
            {
                if (t.name == "ZincatPasswordLock_Generated") return true;
                if (t == root) break;
            }
            return false;
        }

        static string DisplayPath(string path) => string.IsNullOrEmpty(path) ? "（角色根对象）" : path;

        static string IndexedPath(Transform t)
        {
            var parts = new List<string>();
            for (; t; t = t.parent) parts.Add(t.name + " [同级序号 " + (t.GetSiblingIndex() + 1) + "]");
            parts.Reverse();
            return string.Join(" / ", parts);
        }

        string Report()
        {
            var text = new StringBuilder("Zincat 角色路径冲突检查\n");
            text.AppendLine("角色：" + (scannedAvatar ? scannedAvatar.name : "（已删除）"));
            text.AppendLine("冲突组数：" + conflicts.Count);
            text.AppendLine("说明：包含未激活对象；未应用密码锁的局部隐藏范围和例外设置。\n");
            foreach (var conflict in conflicts)
            {
                text.AppendLine(conflict.SameObject ? "[同一物体多个组件]" : "[不同物体路径相同]");
                text.AppendLine("路径：" + DisplayPath(conflict.path));
                text.AppendLine("类型：" + conflict.type.FullName);
                foreach (var component in conflict.components)
                    if (component) text.AppendLine("  " + IndexedPath(component.transform) + " | 组件ID " + component.GetInstanceID());
                text.AppendLine();
            }
            return text.ToString();
        }
    }
}
#endif
