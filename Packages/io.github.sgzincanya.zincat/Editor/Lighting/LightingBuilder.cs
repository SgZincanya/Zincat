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
            public GameObject menu;
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
            ValidateRange(settings.emission, "自发光强度", 1);
            ValidateRange(settings.temperature, "色温", 1);
            ValidateRange(settings.saturation, "彩度", 2);
            if (!settings.minimum.enabled && !settings.maximum.enabled && !settings.monochrome.enabled && !LightingSetup.HasExtraControls(settings))
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
            LightingSetup.SynchronizeMenu(settings, false);
            LightingSetup.ValidateMenu(settings, avatar.transform);
            var channels = new[]
            {
                new ChannelSpec { channel = LightChannel.Minimum, control = settings.minimum, label = "最低亮度", parameter = settings.parameterPrefix + "/Minimum", menu = settings.minimumMenu },
                new ChannelSpec { channel = LightChannel.Maximum, control = settings.maximum, label = "最高亮度", parameter = settings.parameterPrefix + "/Maximum", menu = settings.maximumMenu },
                new ChannelSpec { channel = LightChannel.Monochrome, control = settings.monochrome, label = "环境光颜色影响", parameter = settings.parameterPrefix + "/Monochrome", menu = settings.environmentMenu },
                new ChannelSpec { channel = LightChannel.Emission, control = settings.emission, label = "自发光强度", parameter = settings.parameterPrefix + "/Emission", menu = settings.emissionMenu },
                new ChannelSpec { channel = LightChannel.Temperature, control = settings.temperature, label = "色温", parameter = settings.parameterPrefix + "/Temperature", menu = settings.temperatureMenu },
                new ChannelSpec { channel = LightChannel.Saturation, control = settings.saturation, label = "彩度", parameter = settings.parameterPrefix + "/Saturation", menu = settings.saturationMenu }
            }.Where(c => c.control.enabled).ToList();
            var renderers = avatar.GetComponentsInChildren<Renderer>(true)
                .Where(r => settings.excludedRenderers == null || !settings.excludedRenderers.Contains(r)).ToList();
            var skipped = LightingSetup.GetSkippedPoiyomiMaterials(
                renderers.SelectMany(renderer => renderer.sharedMaterials).Distinct(), settings);
            EnsureUniquePaths(avatar.transform, renderers, skipped);
            if (!settings.minimum.enabled && settings.minimumMenu != null) Object.DestroyImmediate(settings.minimumMenu);
            if (!settings.maximum.enabled && settings.maximumMenu != null) Object.DestroyImmediate(settings.maximumMenu);
            if (!settings.monochrome.enabled && settings.environmentMenu != null) Object.DestroyImmediate(settings.environmentMenu);
            if (!settings.emission.enabled && settings.emissionMenu != null) Object.DestroyImmediate(settings.emissionMenu);
            if (!settings.temperature.enabled && settings.temperatureMenu != null) Object.DestroyImmediate(settings.temperatureMenu);
            if (!settings.saturation.enabled && settings.saturationMenu != null) Object.DestroyImmediate(settings.saturationMenu);
            var controlled = new Dictionary<LightChannel, Dictionary<Renderer, Dictionary<string, float>>>();
            foreach (var channel in channels) controlled[channel.channel] = new Dictionary<Renderer, Dictionary<string, float>>();
            LightingSetup.PrepareForBuild(avatar);
            foreach (Renderer renderer in renderers)
            {
                foreach (Material source in renderer.sharedMaterials)
                {
                    ShaderFamily family = MaterialSupport.Identify(source);
                    if (family == ShaderFamily.Unsupported || skipped.Contains(source)) continue;
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
                    foreach (var channel in channels.Where(c => !LightingExtraControls.IsExtra(c.channel)))
                        foreach (string property in MaterialSupport.Properties(family, channel.channel, settings.followAdditiveLighting))
                        {
                            if (!source.HasProperty(property)) continue;
                            if (!controlled[channel.channel].TryGetValue(renderer, out var properties))
                            { properties = new Dictionary<string, float>(); controlled[channel.channel].Add(renderer, properties); }
                            properties[property] = 0;
                        }
                }
                foreach (var channel in channels.Where(c => LightingExtraControls.IsExtra(c.channel)))
                {
                    var properties = LightingExtraControls.Collect(renderer, channel.channel, skipped, out string issue);
                    if (issue != null) Debug.LogWarning("[Zincat 光照] " + issue, renderer);
                    if (properties.Count > 0) controlled[channel.channel].Add(renderer, properties);
                }
            }
            foreach (var channel in channels.Where(c => controlled[c.channel].Count == 0))
            {
                if (channel.menu != null) Object.DestroyImmediate(channel.menu);
            }
            channels = channels.Where(c => controlled[c.channel].Count > 0).ToList();
            if (settings.extraMenu != null && !channels.Any(c => LightingExtraControls.IsExtra(c.channel)))
                Object.DestroyImmediate(settings.extraMenu);
            if (channels.Count == 0)
            {
                if (skipped.Count == 0 && !LightingSetup.HasExtraControls(settings))
                    throw new InvalidOperationException("没有找到支持所选光照属性的 lilToon 或 Poiyomi 材质。");

                // Omit the empty menu from the build while keeping the prefab's other objects and components.
                var installer = settings.GetComponent<ModularAvatarMenuInstaller>();
                if (installer != null) Object.DestroyImmediate(installer);
                var submenu = settings.submenuObject.GetComponent<ModularAvatarMenuItem>();
                if (submenu != null) Object.DestroyImmediate(submenu);
                Object.DestroyImmediate(settings);
                return;
            }
            bool hasExtras = channels.Any(c => LightingExtraControls.IsExtra(c.channel));
            string extraToggleParameter = settings.parameterPrefix + "/ExtrasEnabled";
            var parameterNames = channels.Select(c => c.parameter);
            if (hasExtras) parameterNames = parameterNames.Append(extraToggleParameter);
            CheckParameterConflicts(avatar, parameterNames);
            var generated = new GameObject("Zincat Lighting Generated");
            generated.transform.SetParent(avatar.transform, false);
            var controller = new AnimatorController { name = "Zincat Lighting" };
            if (hasExtras)
                controller.AddParameter(new AnimatorControllerParameter
                {
                    name = extraToggleParameter,
                    type = AnimatorControllerParameterType.Bool,
                    defaultBool = settings.extrasEnabledByDefault
                });
            foreach (var channel in channels)
            {
                controller.AddParameter(new AnimatorControllerParameter
                {
                    name = channel.parameter,
                    type = AnimatorControllerParameterType.Float,
                    defaultFloat = channel.control.NormalizedInitial
                });
                var values = new List<float> { channel.control.lower };
                if (channel.channel == LightChannel.Temperature && channel.control.lower < 0.5f && channel.control.upper > 0.5f)
                    values.Add(0.5f);
                values.Add(channel.control.upper);
                var children = new List<ChildMotion>();
                foreach (float value in values)
                {
                    var clip = MakeEndpoint(avatar.transform, channel.label + " " + value, controlled[channel.channel], channel.channel, value);
                    saveAsset(clip);
                    children.Add(new ChildMotion { motion = clip, threshold = Mathf.InverseLerp(channel.control.lower, channel.control.upper, value), timeScale = 1 });
                }
                var blend = new BlendTree
                {
                    name = channel.label,
                    blendType = BlendTreeType.Simple1D,
                    blendParameter = channel.parameter,
                    useAutomaticThresholds = false,
                    children = children.ToArray()
                };
                saveAsset(blend);
                var machine = new AnimatorStateMachine { name = channel.label };
                var state = machine.AddState("Control");
                state.motion = blend;
                state.writeDefaultValues = false;
                machine.defaultState = state;
                if (LightingExtraControls.IsExtra(channel.channel))
                {
                    var original = MakeEndpoint(avatar.transform, channel.label + " Original",
                        controlled[channel.channel], channel.channel, 0, true);
                    saveAsset(original);
                    var disabled = machine.AddState("Original");
                    disabled.motion = original;
                    disabled.writeDefaultValues = false;
                    machine.defaultState = settings.extrasEnabledByDefault ? state : disabled;
                    var turnOff = state.AddTransition(disabled);
                    turnOff.hasExitTime = false;
                    turnOff.duration = 0;
                    turnOff.AddCondition(AnimatorConditionMode.IfNot, 0, extraToggleParameter);
                    var turnOn = disabled.AddTransition(state);
                    turnOn.hasExitTime = false;
                    turnOn.duration = 0;
                    turnOn.AddCondition(AnimatorConditionMode.If, 0, extraToggleParameter);
                    saveAsset(turnOff); saveAsset(turnOn); saveAsset(disabled);
                }
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
            if (hasExtras)
                parameters.parameters.Add(new ParameterConfig
                {
                    nameOrPrefix = extraToggleParameter,
                    syncType = ParameterSyncType.Bool,
                    localOnly = !settings.networkSynced,
                    saved = settings.rememberValues,
                    defaultValue = settings.extrasEnabledByDefault ? 1 : 0,
                    hasExplicitDefaultValue = true
                });
            foreach (var configuration in all) Object.DestroyImmediate(configuration);
        }

        private static AnimationClip MakeEndpoint(Transform root, string name, Dictionary<Renderer, Dictionary<string, float>> bindings, LightChannel channel, float value, bool original = false)
        {
            var clip = new AnimationClip { name = name, frameRate = 60 };
            foreach (var entry in bindings)
                foreach (var property in entry.Value.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var binding = EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(entry.Key.transform, root), entry.Key.GetType(), "material." + property.Key);
                    AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, original ? property.Value : LightingExtraControls.Evaluate(channel, property.Key, property.Value, value)));
                }
            return clip;
        }

        private static void EnsureUniquePaths(Transform root, List<Renderer> renderers, HashSet<Material> skipped)
        {
            var seen = new HashSet<string>();
            foreach (Renderer renderer in renderers)
            {
                if (!renderer.sharedMaterials.Any(m => MaterialSupport.Identify(m) != ShaderFamily.Unsupported && !skipped.Contains(m))) continue;
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

