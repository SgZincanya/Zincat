#if UNITY_EDITOR
using System;
using System.Linq;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

[assembly: ExportsPlugin(typeof(Zincat.Clothing.Editor.WardrobeBatchPlugin))]

namespace Zincat.Clothing.Editor
{
    // 只在构建副本上生成控制器，不留下动画文件，不修改原场景。
    public sealed class WardrobeBatchPlugin : Plugin<WardrobeBatchPlugin>
    {
        public override string QualifiedName => "zincat.clothing.batch-control";
        public override string DisplayName => "衣服菜单 · 批量控制";
        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving).BeforePlugin("nadena.dev.modular-avatar")
                .Run("生成分件批量控制", context =>
                {
                    foreach (var marker in context.AvatarRootObject.GetComponentsInChildren<WardrobeGeneratedMenu>(true))
                        Prepare(marker);
                });
        }

        public static void Prepare(WardrobeGeneratedMenu marker)
        {
            if (marker.BatchControlNode == null) return;
            if (string.IsNullOrEmpty(marker.PartParameterPrefix))
                throw new InvalidOperationException("批量控制缺少参数关联，请重新保存分件菜单。");
            var controller = BuildController(marker);
            var merge = marker.BatchControlNode.AddComponent<ModularAvatarMergeAnimator>();
            merge.animator = controller;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
        }

        public static AnimatorController BuildController(WardrobeGeneratedMenu marker)
        {
            var controller = new AnimatorController { name = "衣服批量控制" };
            var command = marker.PartParameterPrefix + "Batch";
            controller.AddParameter(command, AnimatorControllerParameterType.Int);
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            foreach (var slot in marker.Slots)
                controller.AddParameter(marker.PartParameterPrefix + slot.Key, AnimatorControllerParameterType.Bool);
            var machine = new AnimatorStateMachine { name = "批量控制" };
            controller.AddLayer(new AnimatorControllerLayer { name = marker.PartParameterPrefix + "批量控制",
                defaultWeight = 1, stateMachine = machine });
            var idle = machine.AddState("等待");
            idle.writeDefaultValues = false;
            machine.defaultState = idle;
            var count = 2 + (marker.PartPresets?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                var state = machine.AddState(i == 0 ? "显示全部分件" : i == 1 ? "隐藏全部分件" : marker.PartPresets[i - 2].Name);
                state.writeDefaultValues = false;
                var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                driver.localOnly = true;
                foreach (var slot in marker.Slots)
                {
                    bool visible = i == 0 || i >= 2 && marker.PartPresets[i - 2].VisibleKeys.Contains(slot.Key);
                    driver.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter
                    {
                        name = marker.PartParameterPrefix + slot.Key,
                        type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set,
                        value = visible ? 0 : 1
                    });
                }
                var enter = idle.AddTransition(state);
                enter.hasExitTime = false; enter.duration = 0;
                enter.AddCondition(AnimatorConditionMode.Equals, i + 1, command);
                enter.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
                // 按钮松开后返回等待，避免按钮保持期间反复覆盖用户操作。
                var exit = state.AddTransition(idle);
                exit.hasExitTime = false; exit.duration = 0;
                exit.AddCondition(AnimatorConditionMode.Equals, 0, command);
            }
            return controller;
        }
    }
}
#endif
