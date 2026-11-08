// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(Zincat.Lighting.Editor.LightingBuildPlugin))]

namespace Zincat.Lighting.Editor
{
    public sealed class LightingBuildPlugin : Plugin<LightingBuildPlugin>
    {
        public override string QualifiedName => "dev.zincat.lighting";
        public override string DisplayName => "Zincat 光照控制";

        protected override void Configure()
        {
            InPhase(BuildPhase.Generating).BeforePlugin("nadena.dev.modular-avatar")
                .Run("生成独立光照控制", context => LightingBuilder.Build(context.AvatarRootObject, context.AssetSaver.SaveAsset));
        }
    }

    public static class LightingBuilder
    {
        private sealed class ChannelSpec
        {
            public LightChannel channel;
            public PrefabLightControl control;
            public string label;
            public string parameter;
        }

        public static void Validate(ZincatLightingPrefab settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.menuTitle)) throw new InvalidOperationException("菜单名称不能为空。");
            if (settings.parameterPrefix == null || !Regex.IsMatch(settings.parameterPrefix, "^[A-Za-z][A-Za-z0-9_/-]{0,55}$"))
                throw new InvalidOperationException("参数前缀应以英文字母开头，只包含英文、数字、下划线、横线或斜杠，长度不超过 56。");
            ValidateRange(settings.minimum, "最低亮度", 1);
            ValidateRange(settings.maximum, "最高亮度", 10);
            ValidateRange(settings.monochrome, "环境光颜色影响", 1);
            if (!settings.minimum.enabled && !settings.maximum.enabled && !settings.monochrome.enabled)
                throw new InvalidOperationException("请至少启用一个光照控制项。");
        }

        private static void ValidateRange(PrefabLightControl range, string label, float ceiling)
        {
            if (range == null) throw new InvalidOperationException(label + "配置缺失。");
            if (!range.enabled) return;
            if (!Finite(range.lower) || !Finite(range.upper) || !Finite(range.initial) || range.lower < 0 ||
                range.upper > ceiling || range.upper <= range.lower || range.initial < range.lower || range.initial > range.upper)
                throw new InvalidOperationException(label + $"范围或默认值无效；范围需在 0～{ceiling} 内，且上限大于下限。");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // Called only on NDMF's build copy (or a dedicated test copy), never on the author's source Avatar.
        public static void Build(GameObject avatar, Action<Object> saveAsset)
        {
            ZincatLightingPrefab[] all = avatar.GetComponentsInChildren<ZincatLightingPrefab>(true);
            var configurations = all.Where(s => s.enabled && s.gameObject.activeInHierarchy).ToArray();
            if (configurations.Length > 1) throw new InvalidOperationException("一个 Avatar 只允许启用一套 Zincat 光照控制。请关闭重复组件。");
            foreach (var inactive in all.Except(configurations))
            {
                if (inactive == null) continue;
                if (configurations.Any(active => active.transform.IsChildOf(inactive.transform)))
                    throw new InvalidOperationException("光照配置不能嵌套在另一套光照配置中，请分别放在角色下面。");
                if (inactive.gameObject != avatar) Object.DestroyImmediate(inactive.gameObject);
                else Object.DestroyImmediate(inactive);
            }
            if (configurations.Length == 0) return;
            var settings = configurations[0];
            Validate(settings);
            if (avatar.GetComponent<VRCAvatarDescriptor>() == null) throw new InvalidOperationException("光照组件需要放在带 Avatar Descriptor 的角色层级中。");
            if (settings.gameObject == avatar) throw new InvalidOperationException("请将光照菜单预制体放在角色下面，不要给 Avatar 根对象添加光照配置组件。");
            var channels = new[]
            {
                new ChannelSpec { channel = LightChannel.Minimum, control = settings.minimum, label = "最低亮度", parameter = settings.parameterPrefix + "/Minimum" },
                new ChannelSpec { channel = LightChannel.Maximum, control = settings.maximum, label = "最高亮度", parameter = settings.parameterPrefix + "/Maximum" },
                new ChannelSpec { channel = LightChannel.Monochrome, control = settings.monochrome, label = "环境光颜色影响", parameter = settings.parameterPrefix + "/Monochrome" }
            }.Where(c => c.control.enabled).ToList();
            var renderers = avatar.GetComponentsInChildren<Renderer>(true)
                .Where(r => settings.excludedRenderers == null || !settings.excludedRenderers.Contains(r)).ToList();
            EnsureUniquePaths(avatar.transform, renderers);
            LightingSetup.SynchronizeMenu(settings, false);
            LightingSetup.ValidateMenu(settings, avatar.transform);
            if (!settings.minimum.enabled && settings.minimumMenu != null) Object.DestroyImmediate(settings.minimumMenu);
            if (!settings.maximum.enabled && settings.maximumMenu != null) Object.DestroyImmediate(settings.maximumMenu);
            if (!settings.monochrome.enabled && settings.environmentMenu != null) Object.DestroyImmediate(settings.environmentMenu);
            var controlled = new Dictionary<LightChannel, Dictionary<Renderer, HashSet<string>>>();
            foreach (var channel in channels) controlled[channel.channel] = new Dictionary<Renderer, HashSet<string>>();
            LightingSetup.PrepareForBuild(avatar);
            foreach (Renderer renderer in renderers)
            {
                foreach (Material source in renderer.sharedMaterials)
                {
                    ShaderFamily family = MaterialSupport.Identify(source);
                    if (family == ShaderFamily.Unsupported) continue;
                    if (family == ShaderFamily.Poiyomi)
                    {
                        string issue = LightingSetup.ReadinessIssue(source, settings);
                        if (issue != null)
                        {
                            string assetPath = AssetDatabase.GetAssetPath(source);
                            throw new InvalidOperationException("Poiyomi 材质检查失败：" + source.name + "。" + issue +
                                "\nRenderer：" + AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform) +
                                "\n材质：" + (string.IsNullOrEmpty(assetPath) ? "构建期间生成的临时材质" : assetPath) +
                                "\nShader：" + source.shader.name);
                        }
                    }
                    foreach (var channel in channels)
                        foreach (string property in MaterialSupport.Properties(family, channel.channel, settings.followAdditiveLighting))
                        {
                            if (!source.HasProperty(property)) continue;
                            if (!controlled[channel.channel].TryGetValue(renderer, out var properties))
                            { properties = new HashSet<string>(); controlled[channel.channel].Add(renderer, properties); }
                            properties.Add(property);
                        }
                }
            }
            foreach (var channel in channels.Where(c => controlled[c.channel].Count == 0))
            {
                GameObject item = channel.channel == LightChannel.Minimum ? settings.minimumMenu : channel.channel == LightChannel.Maximum ? settings.maximumMenu : settings.environmentMenu;
                if (item != null) Object.DestroyImmediate(item);
            }
            channels = channels.Where(c => controlled[c.channel].Count > 0).ToList();
            if (channels.Count == 0) throw new InvalidOperationException("没有找到支持所选光照属性的 lilToon 或 Poiyomi 材质。");
            CheckParameterConflicts(avatar, channels.Select(c => c.parameter));
            var generated = new GameObject("Zincat Lighting Generated");
            generated.transform.SetParent(avatar.transform, false);
            var controller = new AnimatorController { name = "Zincat Lighting" };
            foreach (var channel in channels)
            {
                controller.AddParameter(new AnimatorControllerParameter
                {
                    name = channel.parameter,
                    type = AnimatorControllerParameterType.Float,
                    defaultFloat = channel.control.NormalizedInitial
                });
                AnimationClip low = MakeEndpoint(avatar.transform, channel.label + " Low", controlled[channel.channel], channel.channel == LightChannel.Monochrome ? 1 - channel.control.lower : channel.control.lower);
                AnimationClip high = MakeEndpoint(avatar.transform, channel.label + " High", controlled[channel.channel], channel.channel == LightChannel.Monochrome ? 1 - channel.control.upper : channel.control.upper);
                saveAsset(low); saveAsset(high);
                var blend = new BlendTree
                {
                    name = channel.label,
                    blendType = BlendTreeType.Simple1D,
                    blendParameter = channel.parameter,
                    useAutomaticThresholds = false,
                    children = new[]
                    {
                        new ChildMotion { motion = low, threshold = 0, timeScale = 1 },
                        new ChildMotion { motion = high, threshold = 1, timeScale = 1 }
                    }
                };
                saveAsset(blend);
                var machine = new AnimatorStateMachine { name = channel.label };
                var state = machine.AddState("Control");
                state.motion = blend;
                state.writeDefaultValues = false;
                machine.defaultState = state;
                controller.AddLayer(new AnimatorControllerLayer { name = settings.parameterPrefix + " " + channel.label, defaultWeight = 1, stateMachine = machine });
                saveAsset(state); saveAsset(machine);
            }
            saveAsset(controller);
            var merge = generated.AddComponent<ModularAvatarMergeAnimator>();
            merge.animator = controller;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.matchAvatarWriteDefaults = true;
            merge.deleteAttachedAnimator = false;
            var parameters = generated.AddComponent<ModularAvatarParameters>();
            parameters.parameters = channels.Select(c => new ParameterConfig
            {
                nameOrPrefix = c.parameter,
                syncType = ParameterSyncType.Float,
                localOnly = !settings.networkSynced,
                saved = settings.rememberValues,
                defaultValue = c.control.NormalizedInitial,
                hasExplicitDefaultValue = true
            }).ToList();
            foreach (var configuration in all) Object.DestroyImmediate(configuration);
        }

        private static AnimationClip MakeEndpoint(Transform root, string name, Dictionary<Renderer, HashSet<string>> bindings, float value)
        {
            var clip = new AnimationClip { name = name, frameRate = 60 };
            foreach (var entry in bindings)
                foreach (string property in entry.Value.OrderBy(p => p, StringComparer.Ordinal))
                {
                    var binding = EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(entry.Key.transform, root), entry.Key.GetType(), "material." + property);
                    AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, value));
                }
            return clip;
        }

        private static void EnsureUniquePaths(Transform root, List<Renderer> renderers)
        {
            var seen = new HashSet<string>();
            foreach (Renderer renderer in renderers)
            {
                if (!renderer.sharedMaterials.Any(m => MaterialSupport.Identify(m) != ShaderFamily.Unsupported)) continue;
                for (var transform = renderer.transform; transform != null && transform != root; transform = transform.parent)
                    if (transform.name.Contains("/")) throw new InvalidOperationException("对象名称包含斜杠，无法生成可靠的动画路径：" + transform.name + "。请先重命名该对象。");
                string path = AnimationUtility.CalculateTransformPath(renderer.transform, root);
                string key = path + "|" + renderer.GetType().FullName;
                if (!seen.Add(key)) throw new InvalidOperationException("动画路径重复：" + path + "。请先重命名同级同名对象。");
            }
        }

        private static void CheckParameterConflicts(GameObject avatar, IEnumerable<string> names)
        {
            var wanted = new HashSet<string>(names);
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor.expressionParameters != null && descriptor.expressionParameters.parameters.Any(p => p != null && wanted.Contains(p.name)))
                throw new InvalidOperationException("参数前缀与现有 Expression Parameters 冲突，请使用另一个前缀。");
            foreach (var layer in descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
            {
                if (layer.type != VRCAvatarDescriptor.AnimLayerType.FX) continue;
                RuntimeAnimatorController source = layer.animatorController;
                while (source is AnimatorOverrideController overridden) source = overridden.runtimeAnimatorController;
                if (source is AnimatorController controller && controller.parameters.Any(p => wanted.Contains(p.name)))
                    throw new InvalidOperationException("参数前缀与角色现有 FX 参数冲突，请在高级设置中修改前缀。");
            }
            foreach (var parameters in avatar.GetComponentsInChildren<ModularAvatarParameters>(true))
                if (parameters.parameters.Any(p => wanted.Contains(p.nameOrPrefix) || (!string.IsNullOrEmpty(p.remapTo) && wanted.Contains(p.remapTo))))
                    throw new InvalidOperationException("参数前缀与已有 MA Parameters 冲突，请使用另一个前缀。");
        }
    }
}

