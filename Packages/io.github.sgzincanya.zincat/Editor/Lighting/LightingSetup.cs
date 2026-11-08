// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Zincat.Lighting.Editor
{
    public static class LightingSetup
    {
        public static IEnumerable<Material> Materials(ZincatLightingPrefab settings)
        {
            var avatar = settings.GetComponentInParent<VRCAvatarDescriptor>();
            if (avatar == null) throw new InvalidOperationException("请把组件放在角色层级中。");
            return avatar.GetComponentsInChildren<Renderer>(true)
                .Where(r => settings.excludedRenderers == null || !settings.excludedRenderers.Contains(r))
                .SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct();
        }

        public static string[] RequiredProperties(ZincatLightingPrefab settings)
        {
            var properties = new List<string>();
            if (settings.minimum.enabled) properties.AddRange(MaterialSupport.Properties(ShaderFamily.Poiyomi, LightChannel.Minimum, settings.followAdditiveLighting));
            if (settings.maximum.enabled) properties.AddRange(MaterialSupport.Properties(ShaderFamily.Poiyomi, LightChannel.Maximum, settings.followAdditiveLighting));
            if (settings.monochrome.enabled) properties.AddRange(MaterialSupport.Properties(ShaderFamily.Poiyomi, LightChannel.Monochrome, settings.followAdditiveLighting));
            return properties.Distinct().ToArray();
        }

        public static bool IsReady(Material material, ZincatLightingPrefab settings)
        {
            return ReadinessIssue(material, settings) == null;
        }

        public static string ReadinessIssue(Material material, ZincatLightingPrefab settings)
        {
            return MaterialSupport.PoiyomiReadinessIssue(material, RequiredProperties(settings));
        }

        // Upload hooks call this before NDMF pauses asset imports. The builder
        // also calls it once as a fallback for other build entry points.
        public static int PrepareForBuild(GameObject avatar)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            var configurations = avatar.GetComponentsInChildren<ZincatLightingPrefab>(true)
                .Where(s => s.enabled && s.gameObject.activeInHierarchy).ToArray();
            if (configurations.Length == 0) return 0;
            if (configurations.Length > 1)
                throw new InvalidOperationException("一个 Avatar 只允许启用一套 Zincat 光照控制。请关闭重复组件。");
            var settings = configurations[0];
            if (settings.gameObject == avatar)
                throw new InvalidOperationException("请将光照菜单预制体放在角色下面，不要给 Avatar 根对象添加光照配置组件。");
            try { return PrepareMaterials(settings); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Poiyomi 材质自动准备失败：" + ex.GetBaseException().Message, ex);
            }
        }

        // Temporary objects only stage changes for validation; no new .mat asset or renderer replacement is created.
        public static int PrepareMaterials(ZincatLightingPrefab settings)
        {
            LightingBuilder.Validate(settings);
            var sources = Materials(settings).Where(m => MaterialSupport.Identify(m) == ShaderFamily.Poiyomi && !IsReady(m, settings)).ToArray();
            if (sources.Length == 0) return 0;
            Type optimizer = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Thry.ThryEditor.ShaderOptimizer", false)).FirstOrDefault(t => t != null);
            MethodInfo lockMethod = optimizer?.GetMethod("LockMaterials", BindingFlags.Public | BindingFlags.Static);
            if (lockMethod == null) throw new InvalidOperationException("找不到 Poiyomi 的 ThryEditor 锁定接口。");
            var staged = new List<Material>();
            var snapshots = new List<Material>();
            bool modified = false;
            int undoGroup = -1;
            try
            {
                foreach (var source in sources)
                {
                    if (AssetDatabase.GetAssetPath(source).Length == 0 || !AssetDatabase.IsOpenForEdit(source))
                        throw new InvalidOperationException("材质必须是可写的工程资产：" + source.name);
                    var prepared = MaterialSupport.PrepareCopy(source); staged.Add(prepared);
                    foreach (string property in RequiredProperties(settings))
                    {
                        if (!prepared.HasProperty(property)) throw new InvalidOperationException(source.name + " 缺少属性 " + property);
                        MaterialSupport.AllowAnimation(prepared, property);
                    }
                    snapshots.Add(new Material(source));
                }
                // Keep existing optimized shaders intact. Switch only these existing materials to their restored state.
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Prepare Zincat Lighting Materials");
                Undo.RecordObjects(sources, "Prepare Zincat Lighting Materials");
                modified = true;
                for (int i = 0; i < sources.Length; i++)
                {
                    EditorUtility.CopySerialized(staged[i], sources[i]);
                    EditorUtility.SetDirty(sources[i]);
                }
                var parameters = lockMethod.GetParameters();
                object[] args = parameters.Select((p, i) => i == 0 ? (object)sources : p.HasDefaultValue ? p.DefaultValue : System.Activator.CreateInstance(p.ParameterType)).ToArray();
                bool success = (bool)lockMethod.Invoke(null, args);
                if (!success || sources.Any(m => !MaterialSupport.IsLocked(m) || !IsReady(m, settings)))
                    throw new InvalidOperationException("Poiyomi 重新锁定未完成，材质已恢复到操作前状态。");
                foreach (var material in sources) AssetDatabase.SaveAssetIfDirty(material);
                return sources.Length;
            }
            catch
            {
                if (modified)
                    for (int i = 0; i < snapshots.Count; i++)
                    {
                        EditorUtility.CopySerialized(snapshots[i], sources[i]);
                        EditorUtility.SetDirty(sources[i]);
                        AssetDatabase.SaveAssetIfDirty(sources[i]);
                    }
                throw;
            }
            finally
            {
                foreach (var material in staged.Concat(snapshots)) Object.DestroyImmediate(material);
                if (undoGroup >= 0) Undo.CollapseUndoOperations(undoGroup);
            }
        }

        public static bool MenuNeedsSynchronization(ZincatLightingPrefab settings)
        {
            if (settings.menuRoot != settings.gameObject || settings.GetComponent<ModularAvatarMenuGroup>() == null ||
                settings.submenuObject == null || !settings.submenuObject.transform.IsChildOf(settings.transform)) return true;
            var parent = settings.submenuObject.GetComponent<ModularAvatarMenuItem>();
            if (parent == null || parent.Control == null || parent.Control.type != VRCExpressionsMenu.Control.ControlType.SubMenu || parent.MenuSource != SubmenuSource.Children) return true;
            return (settings.GetComponent<ModularAvatarMenuInstaller>() != null) != settings.installMenuAutomatically ||
                NeedsUpdate(settings, settings.minimumMenu, settings.minimum.enabled, "/Minimum") ||
                NeedsUpdate(settings, settings.maximumMenu, settings.maximum.enabled, "/Maximum") ||
                NeedsUpdate(settings, settings.environmentMenu, settings.monochrome.enabled, "/Monochrome");
        }

        private static bool NeedsUpdate(ZincatLightingPrefab settings, GameObject obj, bool active, string suffix)
        {
            var item = obj == null ? null : obj.GetComponent<ModularAvatarMenuItem>();
            return item == null || !obj.transform.IsChildOf(settings.transform) || obj.activeSelf != active || item.Control == null ||
                item.Control.type != VRCExpressionsMenu.Control.ControlType.RadialPuppet || item.Control.subParameters == null ||
                item.Control.subParameters.Length != 1 || item.Control.subParameters[0] == null || item.Control.subParameters[0].name != settings.parameterPrefix + suffix ||
                item.isSynced != settings.networkSynced || item.isSaved != settings.rememberValues;
        }

        public static void SynchronizeMenu(ZincatLightingPrefab settings, bool undo = true)
        {
            LightingBuilder.Validate(settings);
            if (settings.menuRoot != null && settings.menuRoot != settings.gameObject)
                throw new InvalidOperationException("菜单根对象必须是光照配置组件所在对象。");
            if (undo) Undo.RecordObject(settings, "Update Zincat MA Menu");
            settings.menuRoot = settings.gameObject;
            if (settings.GetComponent<ModularAvatarMenuGroup>() == null) Add<ModularAvatarMenuGroup>(settings.gameObject, undo);
            if (settings.submenuObject == null)
            {
                settings.submenuObject = Create(settings.menuTitle, settings.transform, undo);
                Add<ModularAvatarMenuItem>(settings.submenuObject, undo).Control = new VRCExpressionsMenu.Control { name = settings.menuTitle };
            }
            if (!settings.submenuObject.transform.IsChildOf(settings.transform)) throw new InvalidOperationException("子菜单不在预制体层级中。");
            var parent = settings.submenuObject.GetComponent<ModularAvatarMenuItem>();
            if (parent == null)
            {
                parent = Add<ModularAvatarMenuItem>(settings.submenuObject, undo);
                parent.Control = new VRCExpressionsMenu.Control { name = settings.submenuObject.name };
            }
            if (undo) Undo.RecordObject(parent, "Update Zincat Submenu");
            if (parent.Control == null) parent.Control = new VRCExpressionsMenu.Control { name = settings.submenuObject.name };
            parent.Control.type = VRCExpressionsMenu.Control.ControlType.SubMenu;
            parent.MenuSource = SubmenuSource.Children;
            if (undo) PrefabUtility.RecordPrefabInstancePropertyModifications(parent);
            settings.minimumMenu = UpdateItem(settings, settings.minimumMenu, "最低亮度", "/Minimum", settings.minimum.enabled, undo);
            settings.maximumMenu = UpdateItem(settings, settings.maximumMenu, "最高亮度", "/Maximum", settings.maximum.enabled, undo);
            settings.environmentMenu = UpdateItem(settings, settings.environmentMenu, "环境光颜色影响", "/Monochrome", settings.monochrome.enabled, undo);
            var installer = settings.GetComponent<ModularAvatarMenuInstaller>();
            if (settings.installMenuAutomatically && installer == null) Add<ModularAvatarMenuInstaller>(settings.gameObject, undo);
            if (!settings.installMenuAutomatically && installer != null)
            { if (undo) Undo.DestroyObjectImmediate(installer); else Object.DestroyImmediate(installer); }
            if (undo)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(settings);
                EditorUtility.SetDirty(settings);
                if (settings.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(settings.gameObject.scene);
            }
        }

        private static T Add<T>(GameObject obj, bool undo) where T : Component => undo ? Undo.AddComponent<T>(obj) : obj.AddComponent<T>();
        private static GameObject Create(string name, Transform parent, bool undo)
        {
            var obj = new GameObject(name);
            if (undo) { Undo.RegisterCreatedObjectUndo(obj, "Create Zincat Menu"); Undo.SetTransformParent(obj.transform, parent, "Parent Zincat Menu"); }
            else obj.transform.SetParent(parent, false);
            return obj;
        }
        private static GameObject UpdateItem(ZincatLightingPrefab settings, GameObject obj, string label, string suffix, bool active, bool undo)
        {
            if (obj == null)
            {
                obj = Create(label, settings.submenuObject.transform, undo);
                Add<ModularAvatarMenuItem>(obj, undo).Control = new VRCExpressionsMenu.Control { name = label };
            }
            if (!obj.transform.IsChildOf(settings.transform)) throw new InvalidOperationException("菜单项不在预制体层级中。");
            var item = obj.GetComponent<ModularAvatarMenuItem>();
            if (item == null)
            {
                item = Add<ModularAvatarMenuItem>(obj, undo);
                item.Control = new VRCExpressionsMenu.Control { name = obj.name };
            }
            if (undo) Undo.RecordObjects(new Object[] { obj, item }, "Update Zincat Menu Item");
            if (item.Control == null) item.Control = new VRCExpressionsMenu.Control { name = obj.name };
            item.Control.type = VRCExpressionsMenu.Control.ControlType.RadialPuppet;
            item.Control.subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = settings.parameterPrefix + suffix } };
            item.isSynced = settings.networkSynced; item.isSaved = settings.rememberValues;
            obj.SetActive(active);
            if (undo)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(item);
                PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
            }
            return obj;
        }

        public static void ValidateMenu(ZincatLightingPrefab settings, Transform avatar)
        {
            if (settings.menuRoot == null || !settings.menuRoot.transform.IsChildOf(avatar))
                throw new InvalidOperationException("光照菜单预制体不在当前角色层级中。");
            ValidateItem(settings.minimumMenu, settings.minimum.enabled, settings.parameterPrefix + "/Minimum");
            ValidateItem(settings.maximumMenu, settings.maximum.enabled, settings.parameterPrefix + "/Maximum");
            ValidateItem(settings.environmentMenu, settings.monochrome.enabled, settings.parameterPrefix + "/Monochrome");
        }

        private static void ValidateItem(GameObject itemObject, bool active, string parameter)
        {
            var item = itemObject == null ? null : itemObject.GetComponent<ModularAvatarMenuItem>();
            if (item == null || itemObject.activeSelf != active || item.Control == null || item.Control.type != VRCExpressionsMenu.Control.ControlType.RadialPuppet ||
                item.Control.subParameters == null || item.Control.subParameters.Length != 1 || item.Control.subParameters[0] == null || item.Control.subParameters[0].name != parameter)
                throw new InvalidOperationException("MA 菜单配置无效，请检查光照预制体的菜单项。");
        }
    }
}
