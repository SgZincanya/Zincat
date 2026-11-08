// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Zincat.Lighting.Editor
{
    [CustomEditor(typeof(ZincatLightingPrefab))]
    public sealed class LightingInspector : UnityEditor.Editor
    {
        private string operationError;
        private bool showAdvanced;
        private bool showTest;
        private double materialScanTime = double.NegativeInfinity;
        private int lilCount, poiCount, pendingCount;
        private string pendingReason;

        private void OnEnable()
        {
            EditorApplication.projectChanged += InvalidateMaterials;
            EditorApplication.hierarchyChanged += InvalidateMaterials;
            Undo.undoRedoPerformed += InvalidateMaterials;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= InvalidateMaterials;
            EditorApplication.hierarchyChanged -= InvalidateMaterials;
            Undo.undoRedoPerformed -= InvalidateMaterials;
        }

        private void InvalidateMaterials()
        { materialScanTime = double.NegativeInfinity; Repaint(); }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawControl("minimum", "最低亮度");
            DrawControl("maximum", "最高亮度");
            DrawControl("monochrome", "环境光颜色影响");
            Property("installMenuAutomatically", "自动安装菜单", "关闭后可以通过自己的 MA 菜单引用此预制体。");
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "高级设置", true);
            if (showAdvanced)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    Property("parameterPrefix", "参数前缀", "通常保留默认值；发生参数冲突时再修改。");
                    Property("followAdditiveLighting", "Poiyomi 附加光跟随");
                    Property("networkSynced", "同步给其他玩家");
                    Property("rememberValues", "记住菜单数值");
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("excludedRenderers"), new GUIContent("排除的 Renderer", "这些 Renderer 的材质不会准备，也不会生成光照动画。"), true);
                }
            }
            bool changed = serializedObject.ApplyModifiedProperties();
            var settings = (ZincatLightingPrefab)target;
            if (changed) { operationError = null; InvalidateMaterials(); }
            bool valid = true;
            try { LightingBuilder.Validate(settings); }
            catch (Exception ex) { valid = false; EditorGUILayout.HelpBox(ex.Message, MessageType.Error); }
            if (valid && !EditorUtility.IsPersistent(settings) && !EditorApplication.isPlayingOrWillChangePlaymode && LightingSetup.MenuNeedsSynchronization(settings))
                Execute(() => LightingSetup.SynchronizeMenu(settings));

            var descriptor = settings.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                EditorGUILayout.HelpBox("将菜单预制体放到角色下即可使用。", MessageType.Info);
                DrawError();
                return;
            }
            RefreshMaterials(settings, valid);
            if (poiCount > 0 && valid)
            {
                if (pendingCount == 0) EditorGUILayout.LabelField($"Poiyomi 已就绪（{poiCount} 个材质）");
                else
                {
                    if (!string.IsNullOrEmpty(pendingReason)) EditorGUILayout.HelpBox(pendingReason, MessageType.Warning);
                    using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                        if (GUILayout.Button($"准备 Poiyomi 材质（{pendingCount} 个）", GUILayout.MinHeight(24)))
                            Execute(() => { LightingSetup.PrepareMaterials(settings); InvalidateMaterials(); });
                }
            }
            if (valid && lilCount + poiCount == 0) EditorGUILayout.HelpBox("未找到可控制的 lilToon 或 Poiyomi 材质。请检查 Renderer 和排除列表。", MessageType.Warning);
            if (showAdvanced)
            {
                EditorGUILayout.LabelField($"材质：lilToon {lilCount}，Poiyomi {poiCount}");
                int channels = new[] { settings.minimum, settings.maximum, settings.monochrome }.Count(c => c != null && c.enabled);
                EditorGUILayout.LabelField(settings.networkSynced ? $"同步参数：{channels * 8} bit" : "参数仅本地使用");
            }
            showTest = EditorGUILayout.Foldout(showTest, "测试", true);
            if (showTest)
            {
                using (new EditorGUI.DisabledScope(!valid || EditorApplication.isPlayingOrWillChangePlaymode))
                    if (GUILayout.Button(new GUIContent("生成测试副本", "通过 NDMF 完整构建副本，检查合并后的动画、菜单和参数。副本默认停用，避免进入播放时再次构建。源角色不变。")))
                        Execute(() =>
                        {
                            LightingSetup.PrepareForBuild(descriptor.gameObject);
                            var copy = AvatarProcessor.ManualProcessAvatar(descriptor.gameObject);
                            copy.SetActive(false);
                            Undo.RegisterCreatedObjectUndo(copy, "Generate Zincat Lighting Test Copy");
                            Selection.activeGameObject = copy;
                        });
            }
            DrawError();
        }

        private void RefreshMaterials(ZincatLightingPrefab settings, bool valid)
        {
            if (EditorApplication.timeSinceStartup - materialScanTime < 1) return;
            materialScanTime = EditorApplication.timeSinceStartup;
            lilCount = poiCount = pendingCount = 0;
            pendingReason = null;
            foreach (var material in LightingSetup.Materials(settings))
            {
                switch (MaterialSupport.Identify(material))
                {
                    case ShaderFamily.LilToon: lilCount++; break;
                    case ShaderFamily.Poiyomi:
                        poiCount++;
                        if (valid)
                        {
                            string issue = LightingSetup.ReadinessIssue(material, settings);
                            if (issue != null)
                            {
                                pendingCount++;
                                if (pendingReason == null) pendingReason = material.name + "：" + issue;
                            }
                        }
                        break;
                }
            }
        }

        private void Execute(Action action)
        {
            try { action(); operationError = null; }
            catch (Exception ex) { operationError = ex.GetBaseException().Message; Debug.LogException(ex); }
        }

        private void DrawError()
        { if (!string.IsNullOrEmpty(operationError)) EditorGUILayout.HelpBox(operationError, MessageType.Error); }

        private void Property(string name, string label, string tooltip = null)
        { EditorGUILayout.PropertyField(serializedObject.FindProperty(name), new GUIContent(label, tooltip)); }

        private void DrawControl(string name, string label)
        {
            var property = serializedObject.FindProperty(name);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                var enabled = property.FindPropertyRelative("enabled");
                EditorGUILayout.PropertyField(enabled, new GUIContent(label));
                using (new EditorGUI.DisabledScope(!enabled.boolValue))
                {
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("lower"), new GUIContent("下限（0%）"));
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("upper"), new GUIContent("上限（100%）"));
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("initial"), new GUIContent("默认值"));
                }
            }
        }
    }
}
