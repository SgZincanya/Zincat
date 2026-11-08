using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Zincat.PasswordLock.Editor
{
    internal static class PasswordLockGenerator
    {
        internal const string GeneratedName = "ZincatPasswordLock_Generated";
        internal sealed class Target
        {
            internal Component component;
            internal bool initialEnabled;
            internal string path;
            internal Type type;
            internal void Set(bool value)
            {
                if (component is Renderer r) r.enabled = value;
                else if (component is Light l) l.enabled = value;
            }
        }

        internal static VRCAvatarDescriptor Avatar(ZincatPasswordLock config)
            => config.GetComponentInParent<VRCAvatarDescriptor>();

        internal static List<Target> Targets(ZincatPasswordLock config, Transform avatar)
        {
            bool Selected(Transform t)
            {
                if (t.IsChildOf(config.transform) && HasGeneratedAncestor(t, config.transform)) return false;
                bool Within(GameObject o) => o && (t == o.transform || t.IsChildOf(o.transform));
                return (config.hiddenRoots.Count == 0 || config.hiddenRoots.Any(Within))
                    && !config.exclusions.Any(Within);
            }
            var result = new List<Target>();
            foreach (var r in avatar.GetComponentsInChildren<Renderer>(true))
                if (Selected(r.transform)) result.Add(new Target { component = r, initialEnabled = r.enabled,
                    path = AnimationUtility.CalculateTransformPath(r.transform, avatar), type = r.GetType() });
            if (config.hideLights)
                foreach (var l in avatar.GetComponentsInChildren<Light>(true))
                    if (Selected(l.transform)) result.Add(new Target { component = l, initialEnabled = l.enabled,
                        path = AnimationUtility.CalculateTransformPath(l.transform, avatar), type = typeof(Light) });
            return result;
        }

        internal static void WarnConflictingTargets(List<Target> targets, ZincatPasswordLock config)
        {
            var conflicts = targets.GroupBy(t => new { t.path, t.type })
                .Where(group => group.Count() > 1).ToArray();
            if (conflicts.Length == 0) return;
            // Warn only during generation, not on each window validation / target scan.
            // Keep every target; do not rename, split, or exclude avatar objects.
            var details = conflicts.Select(group =>
                "• " + (string.IsNullOrEmpty(group.Key.path) ? "（角色根对象）" : group.Key.path)
                + " | " + group.Key.type.Name + " × " + group.Count());
            Debug.LogWarning("[Zincat 密码锁] 发现 " + conflicts.Length
                + " 组路径与组件类型重复，已继续生成，不会因这项检查中止构建。\n"
                + "重名物体或同一物体上的同类型组件可能无法分别控制，部分显隐或解锁恢复可能不完整。\n"
                + string.Join("\n", details), config);
        }

        internal static string Prefix(ZincatPasswordLock c) => "ZincatLock_" + c.identifier + "_";

        internal static bool HasGeneratedAncestor(Transform t, Transform config)
        {
            for (; t && t != config; t = t.parent) if (t.name == GeneratedName) return true;
            return false;
        }

        internal static void Validate(ZincatPasswordLock config, VRCAvatarDescriptor avatar)
        {
            if (!avatar) throw new InvalidOperationException("密码锁必须位于带 Avatar Descriptor 的角色下面。");
            if (!PasswordSequence.Valid(config.password)) throw new InvalidOperationException("请输入 1–20 位密码，只能使用数字 0–9。");
            if (avatar.GetComponentsInChildren<ZincatPasswordLock>(true).Length != 1)
                throw new InvalidOperationException("第一版每个角色仅支持一个密码锁。");
            // Selecting the avatar root already covers every child. Other root entries are
            // redundant, and build plugins may remove those helper objects before this pass.
            // Never let a redundant, destroyed reference abort the entire avatar build.
            bool wholeAvatar = config.hiddenRoots.Contains(avatar.gameObject);
            if (!wholeAvatar)
                foreach (var obj in config.hiddenRoots)
                {
                    if (!obj) continue;
                    if (!(obj.transform == avatar.transform || obj.transform.IsChildOf(avatar.transform)))
                        throw new InvalidOperationException("隐藏范围中的对象“" + obj.name + "”不属于当前角色。请移除该项或直接选择角色根对象。");
                }
            foreach (var obj in config.exclusions)
            {
                if (!obj) continue;
                if (!(obj.transform == avatar.transform || obj.transform.IsChildOf(avatar.transform)))
                    throw new InvalidOperationException("例外对象“" + obj.name + "”不属于当前角色。");
            }
            if (Targets(config, avatar.transform).Count == 0)
                throw new InvalidOperationException("隐藏范围中没有 Renderer 或 Light。");
        }

        internal static List<Target> Generate(ZincatPasswordLock config, Action<Object> save)
        {
            var avatar = Avatar(config);
            Validate(config, avatar);
            if (string.IsNullOrEmpty(config.identifier)) config.identifier = Guid.NewGuid().ToString("N");
            var targets = Targets(config, avatar.transform);
            WarnConflictingTargets(targets, config);
            var previous = config.transform.Find(GeneratedName);
            if (previous) Object.DestroyImmediate(previous.gameObject);
            var root = Child(config.transform, GeneratedName);
            var parameters = root.AddComponent<ModularAvatarParameters>();
            bool useHud = config.hudEnabled && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android
                && EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS;
            string input = Prefix(config) + "Input", unlocked = Prefix(config) + "Unlocked", menuOpen = Prefix(config) + "MenuOpen";
            parameters.parameters = new List<ParameterConfig>
            {
                new ParameterConfig { nameOrPrefix = input, syncType = ParameterSyncType.Int,
                    localOnly = true, saved = false, defaultValue = 0, hasExplicitDefaultValue = true },
                new ParameterConfig { nameOrPrefix = unlocked, syncType = ParameterSyncType.Bool,
                    localOnly = false, saved = config.rememberUnlock, defaultValue = 0, hasExplicitDefaultValue = true }
            };
            if (useHud) parameters.parameters.Add(new ParameterConfig { nameOrPrefix = menuOpen,
                syncType = ParameterSyncType.Bool, localOnly = true, saved = false, defaultValue = 0, hasExplicitDefaultValue = true });
            Menu(root.transform, input, useHud ? menuOpen : "");

            SkinnedMeshRenderer hud = useHud ? CreateHud(root.transform, avatar, config, save) : null;
            string hudPath = hud ? AnimationUtility.CalculateTransformPath(hud.transform, avatar.transform) : null;

            var baseline = Clip("OriginalVisibility", targets, false, true, save);
            var locked = Clip("LockedVisibility", targets, false, false, save);
            Motion empty = null;

            var baseController = Controller("ZincatLock_Baseline", save);
            var baseMachine = Machine(baseController, "ZincatLock Original Visibility", save);
            State(baseMachine, "Original", baseline, save);
            Merge(Child(root.transform, "MA Original Visibility"), baseController, -100000);

            var controller = Controller("ZincatLock_Password", save);
            controller.AddParameter(input, AnimatorControllerParameterType.Int);
            controller.AddParameter(unlocked, AnimatorControllerParameterType.Bool);
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            string progressParameter = useHud ? Prefix(config) + "Progress" : null;
            if (useHud) controller.AddParameter(progressParameter, AnimatorControllerParameterType.Float);
            var logic = Machine(controller, "ZincatLock Password", save);
            Logic(logic, config.password, input, unlocked, empty, progressParameter, save);
            var control = Machine(controller, "ZincatLock Visibility Control", save);
            var controlLocked = State(control, "Locked", empty, save);
            var controlUnlocked = State(control, "Unlocked", empty, save);
            ChangeWeight(controlLocked, 2, 1, save);
            ChangeWeight(controlUnlocked, 2, 0, save);
            Transition(controlLocked, controlUnlocked, unlocked, AnimatorConditionMode.If, 0, save);
            Transition(controlUnlocked, controlLocked, unlocked, AnimatorConditionMode.IfNot, 0, save);
            var gate = Machine(controller, "ZincatLock Visibility Gate", save);
            State(gate, "Hidden", locked, save);
            if (hud)
            {
                controller.AddParameter(menuOpen, AnimatorControllerParameterType.Bool);
                var hudMachine = Machine(controller, "ZincatLock HUD", save);
                var hidden = State(hudMachine, "HUD Hidden", HudClip("HUD_Hidden", hudPath, "_Visible", 0, save), save);
                var shown = State(hudMachine, "HUD Shown", HudClip("HUD_Shown", hudPath, "_Visible", 1, save), save);
                var show = Transition(hidden, shown, menuOpen, AnimatorConditionMode.If, 0, save);
                show.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
                show.AddCondition(AnimatorConditionMode.IfNot, 0, unlocked);
                Transition(shown, hidden, menuOpen, AnimatorConditionMode.IfNot, 0, save);
                Transition(shown, hidden, "IsLocal", AnimatorConditionMode.IfNot, 0, save);
                Transition(shown, hidden, unlocked, AnimatorConditionMode.If, 0, save);
                var progressTree = new BlendTree { name = "HUD Progress", blendType = BlendTreeType.Simple1D,
                    blendParameter = progressParameter, useAutomaticThresholds = false };
                progressTree.AddChild(HudClip("HUD_Progress_Empty", hudPath, "_Progress", 0, save), 0);
                progressTree.AddChild(HudClip("HUD_Progress_Full", hudPath, "_Progress", 20, save), 20);
                save(progressTree);
                var progressMachine = Machine(controller, "ZincatLock HUD Progress", save);
                State(progressMachine, "HUD Progress", progressTree, save);
            }
            Merge(Child(root.transform, "MA Password FX"), controller, 100000);
            return targets;
        }

        private static GameObject Child(Transform parent, string name)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private static ModularAvatarMenuItem Item(Transform parent, string name,
            VRCExpressionsMenu.Control.ControlType type, string parameter = "", int value = 0)
        {
            var item = Child(parent, name).AddComponent<ModularAvatarMenuItem>();
            item.Control = new VRCExpressionsMenu.Control { name = name, type = type,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter }, value = value };
            item.MenuSource = SubmenuSource.Children;
            item.isSaved = false;
            item.isSynced = false;
            item.automaticValue = false;
            return item;
        }

        private static void Menu(Transform root, string input, string menuOpen)
        {
            // Installer with a Menu Item uses the item as the installed submenu.
            var menu = Item(root, "密码锁", VRCExpressionsMenu.Control.ControlType.SubMenu, menuOpen, 1);
            menu.gameObject.AddComponent<ModularAvatarMenuInstaller>();
            Item(menu.transform, "清空", VRCExpressionsMenu.Control.ControlType.Button, input, PasswordSequence.ClearCommand);
            var digits = Item(menu.transform, "密码", VRCExpressionsMenu.Control.ControlType.SubMenu);
            // MA's menu installer keeps seven controls and appends its More submenu.
            for (int i = 0; i <= 9; i++) Item(digits.transform, i.ToString(),
                VRCExpressionsMenu.Control.ControlType.Button, input, i + 1);
        }

        internal static SkinnedMeshRenderer CreateHud(Transform parent, VRCAvatarDescriptor avatar,
            ZincatPasswordLock config, Action<Object> save)
            => CreateHud(parent, avatar, config.password.Length, config.hudVertical, config.hudDotRadius, save);

        internal static SkinnedMeshRenderer CreateHud(Transform parent, VRCAvatarDescriptor avatar,
            int passwordLength, float vertical, float radius, Action<Object> save)
        {
            var shader = Shader.Find("Zincat/PasswordLock/HUD Dots");
            if (!shader) throw new InvalidOperationException("找不到 HUD 着色器，请等待 Unity 导入完成后重新生成。");
            var obj = Child(parent, "Password HUD");
            obj.transform.position = avatar.transform.TransformPoint(avatar.ViewPosition);
            var mesh = new Mesh { name = "Zincat HUD Quad" };
            mesh.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) };
            mesh.uv = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
            mesh.triangles = new[] { 0,1,2,0,2,3 };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
            mesh.bindposes = new[] { avatar.transform.worldToLocalMatrix * obj.transform.localToWorldMatrix };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 20);
            save(mesh);
            var material = new Material(shader) { name = "Zincat HUD Dots" };
            material.SetFloat("_Length", passwordLength);
            material.SetFloat("_Vertical", vertical);
            material.SetFloat("_Radius", radius);
            material.SetFloat("_Visible", 0);
            save(material);
            var renderer = obj.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.sharedMaterial = material;
            renderer.bones = new[] { avatar.transform };
            renderer.rootBone = avatar.transform;
            renderer.localBounds = mesh.bounds;
            renderer.updateWhenOffscreen = true;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return renderer;
        }

        private static AnimationClip HudClip(string name, string path, string property, float value, Action<Object> save)
        {
            var clip = new AnimationClip { name = name, frameRate = 60 };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "material." + property),
                AnimationCurve.Constant(0, 1f / 60, value));
            save(clip);
            return clip;
        }

        private static AnimationClip Clip(string name, List<Target> targets, bool value,
            bool original, Action<Object> save)
        {
            var clip = new AnimationClip { name = name, frameRate = 60 };
            foreach (var target in targets)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(target.path, target.type, "m_Enabled"),
                    AnimationCurve.Constant(0, 1f / 60, (original ? target.initialEnabled : value) ? 1 : 0));
            save(clip);
            return clip;
        }

        private static AnimatorController Controller(string name, Action<Object> save)
        {
            var controller = new AnimatorController { name = name };
            save(controller);
            return controller;
        }

        private static AnimatorStateMachine Machine(AnimatorController controller, string name, Action<Object> save)
        {
            var machine = new AnimatorStateMachine { name = name };
            save(machine);
            controller.AddLayer(new AnimatorControllerLayer { name = name, stateMachine = machine,
                defaultWeight = 1, blendingMode = AnimatorLayerBlendingMode.Override });
            return machine;
        }

        private static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion, Action<Object> save)
        {
            var state = machine.AddState(name, new Vector3(260, 60 * machine.states.Length));
            state.writeDefaultValues = false;
            state.motion = motion;
            if (!machine.defaultState) machine.defaultState = state;
            save(state);
            return state;
        }

        private static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, string parameter,
            AnimatorConditionMode mode, float value, Action<Object> save)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.duration = 0;
            transition.canTransitionToSelf = false;
            transition.AddCondition(mode, value, parameter);
            save(transition);
            return transition;
        }

        private static void Driver(AnimatorState state, string parameter, float value, Action<Object> save)
        {
            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.localOnly = true;
            driver.parameters = new List<VRC_AvatarParameterDriver.Parameter>
            { new VRC_AvatarParameterDriver.Parameter { name = parameter, value = value,
                type = VRC_AvatarParameterDriver.ChangeType.Set } };
            save(driver);
        }

        private static void ChangeWeight(AnimatorState state, int layer, float weight, Action<Object> save)
        {
            var behaviour = state.AddStateMachineBehaviour<VRCAnimatorLayerControl>();
            // Use serialization for the inherited SDK enum to avoid SDK namespace variations.
            var serialized = new SerializedObject(behaviour);
            var playable = serialized.FindProperty("playable");
            int fx = Array.IndexOf(playable.enumNames, "FX");
            if (fx < 0) throw new InvalidOperationException("SDK 中找不到 FX Layer Control。");
            playable.enumValueIndex = fx;
            serialized.FindProperty("layer").intValue = layer;
            serialized.FindProperty("goalWeight").floatValue = weight;
            serialized.FindProperty("blendDuration").floatValue = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            save(behaviour);
        }

        private static void Logic(AnimatorStateMachine machine, string password, string input,
            string unlocked, Motion empty, string progressParameter, Action<Object> save)
        {
            AnimatorState ProgressState(string name, int progress)
            {
                var state = State(machine, name, empty, save);
                if (progressParameter != null) Driver(state, progressParameter, progress, save);
                return state;
            }
            var initialize = ProgressState("Initialize", 0);
            machine.defaultState = initialize;
            var reset = ProgressState("Reset_WaitRelease", 0);
            Driver(reset, unlocked, 0, save);
            var ready = new AnimatorState[password.Length];
            var invalid = new AnimatorState[password.Length];
            for (int i = 0; i < ready.Length; i++)
            {
                ready[i] = ProgressState("Ready_" + i, i);
                invalid[i] = ProgressState("Invalid_Ready_" + i, i);
            }
            var wrong = ProgressState("Wrong_WaitRelease", password.Length);
            var success = ProgressState("Correct_WaitRelease", password.Length);
            var done = ProgressState("Unlocked", password.Length);
            Transition(initialize, done, unlocked, AnimatorConditionMode.If, 0, save);
            Transition(initialize, ready[0], unlocked, AnimatorConditionMode.IfNot, 0, save);
            Driver(success, unlocked, 1, save);
            Transition(reset, ready[0], input, AnimatorConditionMode.Equals, 0, save);
            Transition(wrong, ready[0], input, AnimatorConditionMode.Equals, 0, save);
            Transition(success, done, input, AnimatorConditionMode.Equals, 0, save);
            for (int i = 0; i < ready.Length; i++)
            {
                int expected = PasswordSequence.DigitCommand(password[i]);
                var accepted = i == ready.Length - 1 ? success : ProgressState("Accepted_" + i + "_WaitRelease", i + 1);
                var rejected = i == ready.Length - 1 ? wrong : ProgressState("Invalid_Accepted_" + i + "_WaitRelease", i + 1);
                var correct = Transition(ready[i], accepted, input, AnimatorConditionMode.Equals, expected, save);
                correct.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
                var error = Transition(ready[i], rejected, input, AnimatorConditionMode.NotEqual, expected, save);
                error.AddCondition(AnimatorConditionMode.Greater, 0, input);
                error.AddCondition(AnimatorConditionMode.Less, 11, input);
                error.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
                var nextInvalid = Transition(invalid[i], rejected, input, AnimatorConditionMode.Greater, 0, save);
                nextInvalid.AddCondition(AnimatorConditionMode.Less, 11, input);
                nextInvalid.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
                if (i != ready.Length - 1)
                {
                    Transition(accepted, ready[i + 1], input, AnimatorConditionMode.Equals, 0, save);
                    Transition(rejected, invalid[i + 1], input, AnimatorConditionMode.Equals, 0, save);
                }
            }
            foreach (int command in new[] { PasswordSequence.ClearCommand, PasswordSequence.LockCommand })
            {
                var transition = machine.AddAnyStateTransition(reset);
                transition.hasExitTime = false;
                transition.duration = 0;
                transition.canTransitionToSelf = false;
                transition.AddCondition(AnimatorConditionMode.Equals, command, input);
                transition.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
                save(transition);
            }
            // Saved parameters can arrive after initialization. Explicit clear has priority.
            var restored = machine.AddAnyStateTransition(done);
            restored.hasExitTime = false;
            restored.duration = 0;
            restored.canTransitionToSelf = false;
            restored.AddCondition(AnimatorConditionMode.If, 0, unlocked);
            restored.AddCondition(AnimatorConditionMode.NotEqual, PasswordSequence.ClearCommand, input);
            restored.AddCondition(AnimatorConditionMode.NotEqual, PasswordSequence.LockCommand, input);
            save(restored);
        }

        private static void Merge(GameObject obj, AnimatorController controller, int priority)
        {
            var merge = obj.AddComponent<ModularAvatarMergeAnimator>();
            merge.animator = controller;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.layerPriority = priority;
            merge.matchAvatarWriteDefaults = false;
            merge.deleteAttachedAnimator = false;
        }
    }
}
