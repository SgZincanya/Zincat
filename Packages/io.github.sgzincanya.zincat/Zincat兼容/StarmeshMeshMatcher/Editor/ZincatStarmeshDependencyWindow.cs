// Zincat Starmesh Mesh Matcher 0.2.8: this entry point has no third-party dependencies.
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Zincat.StarmeshTools
{
    public sealed class ZincatStarmeshDependencyWindow : EditorWindow
    {
        public const string IntegrationAssembly = "Zincat.StarmeshMatcher.Editor";

        private static bool Supported(string name, Version minimum)
        {
            var package = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().FirstOrDefault(p => p.name == name);
            return package != null && Version.TryParse(package.version.Split('-')[0], out var version) &&
                version >= minimum && version < new Version(2, 0, 0);
        }

        public static bool DependenciesAvailable() => Supported("dev.hai-vr.starmesh", new Version(1, 7, 0)) &&
            Supported("dev.hai-vr.haimeshlib", new Version(1, 6, 0));

        [InitializeOnLoadMethod]
        private static void WatchDependencyRemoval()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (DependenciesAvailable()) return;
                foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                    if (window.GetType().FullName == "Zincat.StarmeshTools.ZincatStarmeshMatcherWindow") window.Close();
            };
        }

        public static Type FindIntegration()
        {
            if (EditorApplication.isCompiling || !DependenciesAvailable()) return null;
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == IntegrationAssembly);
            return assembly?.GetType("Zincat.StarmeshTools.ZincatStarmeshMatcherWindow", false);
        }

        [MenuItem("Zincat/Zincat兼容/Starmesh 自动匹配 Mesh", false, 110)]
        public static void Open()
        {
            var integration = FindIntegration();
            if (integration != null)
            {
                integration.GetMethod("Open", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                return;
            }
            var window = GetWindow<ZincatStarmeshDependencyWindow>("Starmesh 依赖检查");
            window.minSize = new Vector2(460, 240);
        }

        private static void DrawPackage(string label, string packageName, string supported)
        {
            var package = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().FirstOrDefault(p => p.name == packageName);
            EditorGUILayout.LabelField(label, package == null ? "未安装" : "已安装 " + package.version);
            EditorGUILayout.LabelField("支持版本：" + supported, EditorStyles.wordWrappedMiniLabel);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Starmesh 辅助功能已暂时锁定", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("依赖未安装、版本不在支持范围内，或 Unity 尚未完成编译时，辅助模块不会参与编译。其他 Zincat 工具可以继续使用。", MessageType.Info);
            DrawPackage("Starmesh", "dev.hai-vr.starmesh", "1.7.0 ≤ 版本 < 2.0.0");
            EditorGUILayout.Space(6);
            DrawPackage("HaiMeshLib", "dev.hai-vr.haimeshlib", "1.6.0 ≤ 版本 < 2.0.0");
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("安装或恢复依赖后，等待 Unity 编译完成，再重新打开此功能。已有 Body、匹配框和名单设置会保留。", EditorStyles.wordWrappedLabel);
            if (GUILayout.Button("重新检查并打开", GUILayout.Height(28)))
            {
                if (FindIntegration() != null) Close();
                Open();
            }
        }
    }
}
