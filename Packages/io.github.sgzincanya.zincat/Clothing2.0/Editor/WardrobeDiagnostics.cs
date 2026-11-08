#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zincat.Clothing.Editor
{
    /// <summary>本地、有界诊断记录。不修改场景，不上传，不放进 Assets。</summary>
    [InitializeOnLoad]
    internal static class WardrobeDiagnostics
    {
        internal const string Revision = "2026.10.23-inverted-sync-2";
        private const int MaxEvents = 240;
        private static readonly Queue<string> Events = new Queue<string>();
        private static readonly string DirectoryPath = Path.GetFullPath("Library/Zincat/Diagnostics");
        private static bool pendingWrite, writing;
        private static double nextWrite;
        private static string lastError;

        static WardrobeDiagnostics()
        {
            // 重启或重编译后仍保留上一段近期记录，避免覆盖刚发生问题时的线索。
            try
            {
                var previous = Path.Combine(DirectoryPath, "recent-events.log");
                if (File.Exists(previous) && new FileInfo(previous).Length < 4 * 1024 * 1024)
                    foreach (var line in File.ReadLines(previous))
                    {
                        Events.Enqueue(line);
                        while (Events.Count > MaxEvents) Events.Dequeue();
                    }
            }
            catch (Exception e) { lastError = e.Message; }
            Application.logMessageReceived += OnLog;
            AssemblyReloadEvents.beforeAssemblyReload += Flush;
            EditorApplication.quitting += Flush;
            Record("诊断", "启动", Revision);
        }

        internal static void Record(string source, string action, string detail = "")
        {
            if (writing) return;
            if (detail != null && detail.Length > 4000) detail = detail.Substring(0, 4000) + "…";
            Events.Enqueue(DateTime.Now.ToString("O") + " [" + source + "] " + action + " " + detail);
            while (Events.Count > MaxEvents) Events.Dequeue();
            if (pendingWrite) return;
            pendingWrite = true;
            nextWrite = EditorApplication.timeSinceStartup + 1;
            EditorApplication.update += WriteWhenReady;
        }

        private static void WriteWhenReady()
        {
            if (EditorApplication.timeSinceStartup >= nextWrite) Flush();
        }

        private static void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (!(condition.Contains("Zincat") || condition.Contains("Wardrobe") || stack.Contains("Zincat"))) return;
            Record("异常", type.ToString(), condition + "\n" + stack);
        }

        private static void Flush()
        {
            EditorApplication.update -= WriteWhenReady;
            pendingWrite = false;
            if (writing) return;
            writing = true;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(Path.Combine(DirectoryPath, "recent-events.log"), string.Join("\n", Events), Encoding.UTF8);
                lastError = null;
            }
            catch (Exception e) { lastError = e.Message; }
            finally { writing = false; }
        }

        internal static void Attach(VisualElement root, string source, Func<string> capture)
        {
            var button = new Button(() =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("导出诊断记录…"), false, () =>
                {
                    var path = EditorUtility.SaveFilePanel("导出诊断记录（仅本地，不自动上传）", "", "衣柜诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), "txt");
                    if (string.IsNullOrEmpty(path)) return;
                    try { File.WriteAllText(path, BuildReport(source, capture()), Encoding.UTF8); EditorUtility.RevealInFinder(path); }
                    catch (Exception e) { EditorUtility.DisplayDialog("导出失败", e.Message, "确定"); }
                });
                menu.AddItem(new GUIContent("记录当前插件状态"), false, () =>
                {
                    try
                    {
                        SaveSnapshot(source, capture());
                        EditorUtility.DisplayDialog("已记录当前状态", "保存在本机 Library/Zincat/Diagnostics。不会修改场景，也不会自动上传。", "确定");
                    }
                    catch (Exception e) { EditorUtility.DisplayDialog("记录失败", e.Message, "确定"); }
                });
                menu.AddItem(new GUIContent("打开本地记录文件夹"), false, () => { Flush(); EditorUtility.RevealInFinder(DirectoryPath); });
                menu.ShowAsContext();
            }) { text = "诊断记录", name = "wardrobe-diagnostics" };
            button.tooltip = "记录窗口设置、菜单组件、预览状态和最近刷新事件。可能含对象名与资源路径，分享前请检查；不上传。";
            button.style.position = Position.Absolute;
            button.style.top = 14; button.style.right = 18; button.style.height = 28;
            root.Add(button);
        }

        internal static string BuildReport(string source, string state)
        {
            var text = new StringBuilder();
            text.AppendLine("Zincat 衣柜诊断 / " + Revision);
            text.AppendLine("时间=" + DateTime.Now.ToString("O") + ", 窗口=" + source);
            text.AppendLine("Unity=" + Application.unityVersion + ", platform=" + Application.platform);
            text.AppendLine("MA=" + PackageInfo.FindForAssembly(typeof(ModularAvatarMenuItem).Assembly)?.version);
            text.AppendLine("NDMF=" + PackageInfo.FindForAssembly(typeof(nadena.dev.ndmf.preview.PropCacheDebug).Assembly)?.version);
            text.AppendLine("playMode=" + EditorApplication.isPlayingOrWillChangePlaymode + ", compiling=" + EditorApplication.isCompiling);
            text.AppendLine("仅本地记录；可能含对象名与资源路径，分享前请检查。不包含完整场景、模型或材质文件。");
            if (lastError != null) text.AppendLine("诊断文件写入错误=" + lastError);
            text.AppendLine("\n=== 当前插件状态 ===\n" + state);
            text.AppendLine("\n=== 最近事件（最多 " + MaxEvents + " 条） ===\n" + string.Join("\n", Events));
            return text.ToString();
        }

        internal static string SaveSnapshot(string source, string state)
        {
            Record(source, "记录状态"); Flush();
            string path = Path.Combine(DirectoryPath, source == "分件" ? "parts-state.txt" : "wardrobe-state.txt");
            File.WriteAllText(path, BuildReport(source, state), Encoding.UTF8);
            return path;
        }

        internal static void DescribeTree(StringBuilder text, GameObject root)
        {
            if (root == null) { text.AppendLine("场景菜单=未绑定"); return; }
            text.AppendLine("场景菜单=" + root.name + ", scene=" + root.scene.path);
            int count = 0;
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                if (++count > 400) { text.AppendLine("菜单层级超过400，已截断"); break; }
                text.AppendLine("节点 " + AnimationUtility.CalculateTransformPath(node, root.transform) + " id=" + node.gameObject.GetInstanceID() + " active=" + node.gameObject.activeSelf);
                foreach (var component in node.GetComponents<Component>())
                {
                    if (component == null) { text.AppendLine("Missing Script"); continue; }
                    if (component is Transform) continue;
                    text.AppendLine("  " + component.GetType().FullName);
                    if (component is ModularAvatarMenuItem || component is ReactiveComponent ||
                        component is WardrobeGeneratedMenu || component is WardrobeGeneratedSwitch)
                        text.AppendLine(EditorJsonUtility.ToJson(component, true));
                }
            }
        }
    }
}
#endif
