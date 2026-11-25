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

        public static string[] RequiredProperties(ZincatLightingPrefab settings, Material material = null)
        {
            var properties = new List<string>();
            if (settings.minimum.enabled) properties.AddRange(MaterialSupport.Properties(ShaderFamily.Poiyomi, LightChannel.Minimum, settings.followAdditiveLighting));
            if (settings.maximum.enabled) properties.AddRange(MaterialSupport.Properties(ShaderFamily.Poiyomi, LightChannel.Maximum, settings.followAdditiveLighting));
            if (settings.monochrome.enabled) properties.AddRange(MaterialSupport.Properties(ShaderFamily.Poiyomi, LightChannel.Monochrome, settings.followAdditiveLighting));
            foreach (var channel in new[] { LightChannel.Emission, LightChannel.Temperature, LightChannel.Saturation })
                if (ExtraControl(settings, channel).enabled)
                    properties.AddRange(material == null
                        ? MaterialSupport.Properties(ShaderFamily.Poiyomi, channel, false)
                        : MaterialSupport.Properties(material, channel, false));
            return properties.Select(property => property.Split('.')[0]).Distinct().ToArray();
        }

        internal static HashSet<Material> GetSkippedPoiyomiMaterials(IEnumerable<Material> materials, ZincatLightingPrefab settings)
        {
            // A prefab can ship the optimized shader without the Poiyomi shaders used to regenerate it.
            // Already animated materials can still be used as-is.
            var skipped = new HashSet<Material>(materials.Where(m =>
                MaterialSupport.IsLocked(m) && ReadinessIssue(m, settings) != null));
            if (skipped.Count > 0 && MaterialSupport.HasPoiyomiSourceShaders()) skipped.Clear();
            return skipped;
        }

        public static bool IsReady(Material material, ZincatLightingPrefab settings)
        {
            return ReadinessIssue(material, settings) == null;
        }

        public static string ReadinessIssue(Material material, ZincatLightingPrefab settings)
        {
            string issue = MaterialSupport.PoiyomiReadinessIssue(material, RequiredProperties(settings, material));
            if (issue != null) return issue;
            if (MaterialSupport.Identify(material) == ShaderFamily.Poiyomi && settings.saturation.enabled &&
                material.HasProperty("_Saturation") && material.HasProperty("_MainColorAdjustToggle") &&
                material.GetFloat("_MainColorAdjustToggle") == 0)
                return "彩度控制需要启用 Poiyomi Color Adjust。请准备 Poiyomi 材质。";
            return null;
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
            var pending = Materials(settings).Where(m => MaterialSupport.Identify(m) == ShaderFamily.Poiyomi && !IsReady(m, settings)).ToArray();
            var skipped = GetSkippedPoiyomiMaterials(pending, settings);
            var sources = pending.Where(m => !skipped.Contains(m)).ToArray();
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
                    if (settings.saturation.enabled && prepared.HasProperty("_Saturation") && prepared.HasProperty("_MainColorAdjustToggle"))
                    {
                        prepared.SetFloat("_MainColorAdjustToggle", 1);
                        prepared.EnableKeyword("COLOR_GRADING_HDR");
                    }
                    foreach (string property in RequiredProperties(settings, prepared))
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
                var args = parameters.Select(p => p.HasDefaultValue ? p.DefaultValue :
                    p.ParameterType.IsValueType ? System.Activator.CreateInstance(p.ParameterType) : null).ToArray();
                // A batch can reuse a deleted shader if the previous owner is also in that batch.
                // Separate calls let Thry reuse only shaders that still exist.
                foreach (var material in sources)
                {
                    args[0] = new[] { material };
                    bool locked = (bool)lockMethod.Invoke(null, args) && MaterialSupport.IsLocked(material);
                    var issue = locked ? ReadinessIssue(material, settings) : "未生成或未应用锁定 Shader";
                    if (issue != null)
                        throw new InvalidOperationException("Poiyomi 重新锁定未完成，材质已恢复到操作前状态。\n" +
                            material.name + "：" + issue);
                }
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
                NeedsUpdate(settings, settings.environmentMenu, settings.monochrome.enabled, "/Monochrome") ||
                ExtraMenuNeedsSynchronization(settings);
        }

        internal static PrefabLightControl ExtraControl(ZincatLightingPrefab settings, LightChannel channel)
        {
            switch (channel)
            {
                case LightChannel.Emission: return settings.emission;
                case LightChannel.Temperature: return settings.temperature;
                case LightChannel.Saturation: return settings.saturation;
                default: throw new ArgumentOutOfRangeException(nameof(channel));
            }
        }

        internal static bool HasExtraControls(ZincatLightingPrefab settings) =>
            settings.emission.enabled || settings.temperature.enabled || settings.saturation.enabled;

        private static bool ExtraMenuNeedsSynchronization(ZincatLightingPrefab settings)
        {
            if (settings.extraMenu == null) return HasExtraControls(settings);
            var item = settings.extraMenu.GetComponent<ModularAvatarMenuItem>();
            if (!settings.extraMenu.transform.IsChildOf(settings.submenuObject.transform) ||
                settings.extraMenu.activeSelf != HasExtraControls(settings) || item == null ||
                item.Control == null || item.Control.type != VRCExpressionsMenu.Control.ControlType.SubMenu ||
                item.MenuSource != SubmenuSource.Children) return true;
            return ExtraToggleNeedsSynchronization(settings) ||
                OptionalNeedsUpdate(settings, settings.emissionMenu, settings.emission.enabled, "/Emission") ||
                OptionalNeedsUpdate(settings, settings.temperatureMenu, settings.temperature.enabled, "/Temperature") ||
                OptionalNeedsUpdate(settings, settings.saturationMenu, settings.saturation.enabled, "/Saturation");
        }

        private static bool ExtraToggleNeedsSynchronization(ZincatLightingPrefab settings)
        {
            var obj = settings.extraToggleMenu;
            var item = obj == null ? null : obj.GetComponent<ModularAvatarMenuItem>();
            return item == null || obj.transform.parent != settings.extraMenu.transform || !obj.activeSelf ||
                item.Control == null || item.Control.type != VRCExpressionsMenu.Control.ControlType.Toggle ||
                item.Control.parameter == null || item.Control.parameter.name != settings.parameterPrefix + "/ExtrasEnabled" ||
                item.Control.value != 1 || item.isDefault != settings.extrasEnabledByDefault ||
                item.isSaved != settings.rememberValues || item.isSynced != settings.networkSynced;
        }

        private static bool OptionalNeedsUpdate(ZincatLightingPrefab settings, GameObject obj, bool active, string suffix) =>
            obj == null ? active : NeedsUpdate(settings, obj, active, suffix) || obj.transform.parent != settings.extraMenu.transform;

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
            SynchronizeExtraMenu(settings, undo);
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

        private static void SynchronizeExtraMenu(ZincatLightingPrefab settings, bool undo)
        {
            if (settings.extraMenu == null)
            {
                if (!HasExtraControls(settings)) return;
                settings.extraMenu = Create("额外设置", settings.submenuObject.transform, undo);
            }
            if (!settings.extraMenu.transform.IsChildOf(settings.submenuObject.transform))
                throw new InvalidOperationException("额外设置菜单不在光照子菜单中。");
            var item = settings.extraMenu.GetComponent<ModularAvatarMenuItem>();
            if (item == null)
            {
                item = Add<ModularAvatarMenuItem>(settings.extraMenu, undo);
                item.Control = new VRCExpressionsMenu.Control { name = settings.extraMenu.name };
            }
            if (undo) Undo.RecordObjects(new Object[] { item, settings.extraMenu }, "Update Zincat Extra Menu");
            if (item.Control == null) item.Control = new VRCExpressionsMenu.Control { name = settings.extraMenu.name };
            item.Control.type = VRCExpressionsMenu.Control.ControlType.SubMenu;
            item.MenuSource = SubmenuSource.Children;
            settings.extraMenu.SetActive(HasExtraControls(settings));
            SynchronizeExtraToggle(settings, undo);
            settings.emissionMenu = UpdateOptionalItem(settings, settings.emissionMenu, "自发光强度", "/Emission", settings.emission.enabled, undo);
            settings.temperatureMenu = UpdateOptionalItem(settings, settings.temperatureMenu, "色温", "/Temperature", settings.temperature.enabled, undo);
            settings.saturationMenu = UpdateOptionalItem(settings, settings.saturationMenu, "彩度", "/Saturation", settings.saturation.enabled, undo);
            if (undo)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(item);
                PrefabUtility.RecordPrefabInstancePropertyModifications(settings.extraMenu);
            }
        }

        private static void SynchronizeExtraToggle(ZincatLightingPrefab settings, bool undo)
        {
            if (settings.extraToggleMenu == null)
            {
                settings.extraToggleMenu = Create("启用额外设置", settings.extraMenu.transform, undo);
                settings.extraToggleMenu.transform.SetAsFirstSibling();
            }
            var obj = settings.extraToggleMenu;
            if (obj.transform.parent != settings.extraMenu.transform)
                throw new InvalidOperationException("额外设置总开关不在额外设置菜单中。");
            var item = obj.GetComponent<ModularAvatarMenuItem>();
            if (item == null)
            {
                item = Add<ModularAvatarMenuItem>(obj, undo);
                item.Control = new VRCExpressionsMenu.Control { name = obj.name };
            }
            if (undo) Undo.RecordObjects(new Object[] { obj, item }, "Update Zincat Extra Toggle");
            if (item.Control == null) item.Control = new VRCExpressionsMenu.Control { name = obj.name };
            item.Control.type = VRCExpressionsMenu.Control.ControlType.Toggle;
            item.Control.parameter = new VRCExpressionsMenu.Control.Parameter { name = settings.parameterPrefix + "/ExtrasEnabled" };
            item.Control.subParameters = Array.Empty<VRCExpressionsMenu.Control.Parameter>();
            item.Control.value = 1;
            item.isDefault = settings.extrasEnabledByDefault;
            item.isSaved = settings.rememberValues;
            item.isSynced = settings.networkSynced;
            obj.SetActive(true);
            if (undo)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
                PrefabUtility.RecordPrefabInstancePropertyModifications(item);
            }
        }

        private static GameObject UpdateOptionalItem(ZincatLightingPrefab settings, GameObject obj, string label, string suffix, bool active, bool undo)
        {
            if (obj == null && !active) return null;
            return UpdateItem(settings, obj, label, suffix, active, undo, settings.extraMenu.transform);
        }

        private static T Add<T>(GameObject obj, bool undo) where T : Component => undo ? Undo.AddComponent<T>(obj) : obj.AddComponent<T>();
        private static GameObject Create(string name, Transform parent, bool undo)
        {
            var obj = new GameObject(name);
            if (undo) { Undo.RegisterCreatedObjectUndo(obj, "Create Zincat Menu"); Undo.SetTransformParent(obj.transform, parent, "Parent Zincat Menu"); }
            else obj.transform.SetParent(parent, false);
            return obj;
        }
        private static GameObject UpdateItem(ZincatLightingPrefab settings, GameObject obj, string label, string suffix, bool active, bool undo, Transform parent = null)
        {
            if (obj == null)
            {
                obj = Create(label, parent ?? settings.submenuObject.transform, undo);
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
            if (ExtraMenuNeedsSynchronization(settings)) throw new InvalidOperationException("额外设置菜单配置无效。");
            if (settings.emission.enabled) ValidateItem(settings.emissionMenu, true, settings.parameterPrefix + "/Emission");
            if (settings.temperature.enabled) ValidateItem(settings.temperatureMenu, true, settings.parameterPrefix + "/Temperature");
            if (settings.saturation.enabled) ValidateItem(settings.saturationMenu, true, settings.parameterPrefix + "/Saturation");
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
