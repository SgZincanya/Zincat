using System;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Zincat.PasswordLock.Editor
{
    public sealed class PasswordLockWindow : EditorWindow
    {
        private VRCAvatarDescriptor avatar;
        private ZincatPasswordLock config;
        private Vector2 scroll;
        private string error, feedback;
        private bool showPassword, scopeExpanded, optionsExpanded, helpExpanded;

        [MenuItem("Zincat/密码锁（MA）")]
        public static void Open()
        {
            var window = GetWindow<PasswordLockWindow>("密码锁");
            window.minSize = new Vector2(360, 440);
        }

        private void OnEnable()
        {
            minSize = new Vector2(360, 440);
            if (Selection.activeGameObject) avatar = Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>();
            FindConfig();
        }

        private void FindConfig()
        {
            config = avatar ? avatar.GetComponentInChildren<ZincatPasswordLock>(true) : null;
            error = feedback = null;
        }

        private void OnInspectorUpdate()
        {
            if (EditorApplication.isPlaying) Repaint();
        }

        private void OnGUI()
        {
            PasswordLockUi.Ensure();
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), PasswordLockUi.Background);
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Min(140, position.width * .35f);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.MaxWidth(660));
            GUILayout.Space(20);
            GUILayout.BeginHorizontal();
            GUILayout.Space(20);
            GUILayout.BeginVertical();
            GUILayout.Label("密码锁", PasswordLockUi.Title);
            GUILayout.Space(5);
            PasswordLockUi.Note(EditorApplication.isPlaying ? "输入密码，测试角色的显示与隐藏。" : "设置密码，解锁后可记住状态。");
            GUILayout.Space(20);

            if (EditorApplication.isPlaying)
            {
                PasswordLockPlayPreview.DrawPanel();
            }
            else
            {
                DrawAvatar();
                if (config) DrawConfiguration();
                DrawHelp();
            }

            GUILayout.Space(12);
            PasswordLockUi.Note("Zincat · Modular Avatar");
            GUILayout.Space(20);
            GUILayout.EndVertical();
            GUILayout.Space(20);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
            EditorGUIUtility.labelWidth = previousLabelWidth;
        }

        private void DrawAvatar()
        {
            PasswordLockUi.BeginPanel("目标角色");
            EditorGUI.BeginChangeCheck();
            avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(avatar, typeof(VRCAvatarDescriptor), true, GUILayout.Height(28));
            if (EditorGUI.EndChangeCheck()) FindConfig();
            PasswordLockUi.Space();
            if (!avatar)
                PasswordLockUi.Note("将层级中的角色拖到这里，或选中角色后重新打开窗口。");
            else if (!config)
            {
                PasswordLockUi.Note("还没有密码锁。先添加配置，再设置密码。");
                PasswordLockUi.Space();
                using (new EditorGUI.DisabledScope(EditorUtility.IsPersistent(avatar)))
                    if (GUILayout.Button("添加密码锁", PasswordLockUi.Primary))
                    {
                        var obj = new GameObject("Zincat Password Lock");
                        Undo.RegisterCreatedObjectUndo(obj, "添加密码锁");
                        obj.transform.SetParent(avatar.transform, false);
                        config = Undo.AddComponent<ZincatPasswordLock>(obj);
                        config.identifier = Guid.NewGuid().ToString("N");
                        Selection.activeGameObject = obj;
                    }
                if (EditorUtility.IsPersistent(avatar)) PasswordLockUi.Note("请选择场景中的角色实例。");
            }
            else
            {
                bool generated = config.transform.Find(PasswordLockGenerator.GeneratedName);
                PasswordLockUi.Note(generated ? "检测到旧版模块 · 应用设置后改为临时构建。" : "配置已添加 · 菜单和 FX 在 Play / 构建时生成。");
            }
            PasswordLockUi.EndPanel();
        }

        private void DrawConfiguration()
        {
            var serialized = new SerializedObject(config);
            serialized.Update();
            var password = serialized.FindProperty("password");
            PasswordLockUi.BeginPanel("解锁密码");
            GUI.SetNextControlName("ZincatUnlockPassword");
            password.stringValue = showPassword
                ? EditorGUILayout.TextField(password.stringValue, PasswordLockUi.Password)
                : EditorGUILayout.PasswordField(password.stringValue, PasswordLockUi.Password);
            PasswordLockUi.Space();
            GUILayout.BeginHorizontal();
            showPassword = GUILayout.Toggle(showPassword, "显示密码", GUILayout.Height(28));
            GUILayout.FlexibleSpace();
            GUILayout.Label((password.stringValue ?? "").Length + " / 20 位", PasswordLockUi.Caption);
            GUILayout.EndHorizontal();
            bool validPassword = PasswordSequence.Valid(password.stringValue);
            if (string.IsNullOrEmpty(password.stringValue)) PasswordLockUi.Note("输入 1–20 位数字，可用 0–9。");
            else if (!validPassword) PasswordLockUi.Message("请使用 0–9，长度保持在 1–20 位。", true);
            else PasswordLockUi.Note("密码格式正确。完整输入后自动解锁。");
            PasswordLockUi.Space();
            EditorGUILayout.PropertyField(serialized.FindProperty("rememberUnlock"), new GUIContent("记住解锁状态"));
            PasswordLockUi.Note("换角色后回来无需重新输入。点击清空会重新锁定并保存；关闭此项则每次加载重新输入。");
            PasswordLockUi.EndPanel();

            PasswordLockUi.BeginPanel("显示范围");
            var roots = serialized.FindProperty("hiddenRoots");
            if (roots.arraySize == 0)
            {
                GUILayout.Label("整个角色", PasswordLockUi.Body);
                PasswordLockUi.Space();
                PasswordLockUi.Note("自动扫描身体、衣服、配件与粒子，无需填写列表。");
            }
            else
            {
                GUILayout.Label("自定义范围 · " + roots.arraySize + " 项", PasswordLockUi.Body);
                PasswordLockUi.Space();
                PasswordLockUi.Note("将列表大小设为 0，即可扫描整个角色。");
                if (GUILayout.Button("改为整个角色", PasswordLockUi.Secondary)) roots.arraySize = 0;
            }
            PasswordLockUi.Space();
            scopeExpanded = EditorGUILayout.Foldout(scopeExpanded, "自定义范围与例外", true, PasswordLockUi.Foldout);
            if (scopeExpanded)
            {
                EditorGUILayout.PropertyField(roots, new GUIContent("隐藏对象"), true);
                EditorGUILayout.PropertyField(serialized.FindProperty("exclusions"), new GUIContent("保留显示"), true);
            }
            PasswordLockUi.EndPanel();

            PasswordLockUi.BeginPanel("游戏菜单");
            GUILayout.Label("密码锁", PasswordLockUi.Body);
            PasswordLockUi.Space();
            GUILayout.BeginHorizontal();
            GUILayout.Label("清空", PasswordLockUi.Secondary);
            GUILayout.Space(8);
            GUILayout.Label("密码  ›", PasswordLockUi.Secondary);
            GUILayout.EndHorizontal();
            PasswordLockUi.Space();
            PasswordLockUi.Note("密码 → 0–9，由 MA 自动分页：0–6 / More → 7–9。\n清空会清除输入并重新锁定。");
            PasswordLockUi.EndPanel();

            PasswordLockUi.BeginPanel("视野圆点 · 实验");
            var hudEnabled = serialized.FindProperty("hudEnabled");
            EditorGUILayout.PropertyField(hudEnabled, new GUIContent("启用 PC HUD"));
            if (hudEnabled.boolValue)
            {
                EditorGUILayout.Slider(serialized.FindProperty("hudVertical"), 0.1f, 0.8f, new GUIContent("垂直位置", "数值越大越靠上，0.5 为视野中央；默认 0.34。"));
                EditorGUILayout.Slider(serialized.FindProperty("hudDotRadius"), 0.004f, 0.015f, new GUIContent("圆点大小"));
                PasswordLockUi.Note("圆点总数自动取密码长度，亮起数量自动取输入进度。无需手动设置材质数值。");
                PasswordLockUi.Note("HUD 位于眼前 1.5 米的共同平面，两眼分别投影。垂直位置数值越大越靠上，0.5 为视野中央。");
                PasswordLockUi.Note("进入密码锁一级菜单时显示，子菜单内保持显示；返回上层或解锁后隐藏。整串输入完成后判断对错。\n直接收起轮盘后的隐藏、VR 双眼与第一人称效果需在游戏中验证。仅支持 PC。");
            }
            PasswordLockUi.EndPanel();

            optionsExpanded = EditorGUILayout.Foldout(optionsExpanded, "测试与其他设置", true, PasswordLockUi.Foldout);
            if (optionsExpanded)
            {
                PasswordLockUi.BeginPanel(null);
                EditorGUILayout.PropertyField(serialized.FindProperty("hideLights"), new GUIContent("锁定时隐藏灯光"));
                PasswordLockUi.Space();
                PasswordLockUi.Note("Unity Play 自动跳过密码锁，其他 MA 模块正常运行；上传构建时自动加入。没有额外开关，验证密码锁请使用 SDK Build & Test 或进入 VRChat。");
                PasswordLockUi.EndPanel();
            }
            if (serialized.ApplyModifiedProperties())
            {
                error = null;
                feedback = "设置已修改。保存场景后，下次 Play / 构建会自动使用新设置。";
            }
            GUILayout.Space(12);
            if (!string.IsNullOrEmpty(error))
            {
                PasswordLockUi.Message(error, true);
                PasswordLockUi.Space();
            }
            else if (!string.IsNullOrEmpty(feedback))
            {
                PasswordLockUi.Message(feedback);
                PasswordLockUi.Space();
            }
            using (new EditorGUI.DisabledScope(!validPassword || !config.enabled || !config.gameObject.activeInHierarchy))
                if (GUILayout.Button("应用密码锁设置", PasswordLockUi.Primary, GUILayout.Height(40))) ApplyConfiguration();
            PasswordLockUi.Space();
            PasswordLockUi.Note("工程只保存配置。菜单与内部 FX 在 Play / 构建时临时生成，不再保存逐位动画文件。");
            if (!config.enabled || !config.gameObject.activeInHierarchy)
            {
                PasswordLockUi.Space();
                PasswordLockUi.Message("密码锁配置已停用，请先在层级中启用它。", true);
            }
            PasswordLockUi.Space();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("在层级中查看", PasswordLockUi.Secondary)) Selection.activeGameObject = config.gameObject;
            GUILayout.Space(8);
            if (GUILayout.Button("角色自检", PasswordLockUi.Secondary))
                Zincat.Tools.ZincatAvatarConflictChecker.OpenForAvatar(avatar.gameObject);
            GUILayout.Space(8);
            if (GUILayout.Button("更多操作", PasswordLockUi.Secondary))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("移除密码锁（可撤销）"), false, () =>
                {
                    if (config) Undo.DestroyObjectImmediate(config.gameObject);
                    config = null;
                    feedback = error = null;
                    Repaint();
                });
                menu.ShowAsContext();
            }
            GUILayout.EndHorizontal();
        }

        private void ApplyConfiguration()
        {
            try
            {
                PasswordLockGenerator.Validate(config, avatar);
                Undo.RecordObject(config, "应用密码锁设置");
                if (string.IsNullOrEmpty(config.identifier)) config.identifier = Guid.NewGuid().ToString("N");
                var legacy = config.transform.Find(PasswordLockGenerator.GeneratedName);
                if (legacy) Undo.DestroyObjectImmediate(legacy.gameObject);
                EditorUtility.SetDirty(config);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                error = null;
                feedback = "设置已应用。保存场景后进入 Play；菜单与 FX 会临时生成。";
                EditorGUIUtility.PingObject(config.gameObject);
            }
            catch (Exception ex) { feedback = null; error = ex.Message; Debug.LogException(ex); }
        }

        private void DrawHelp()
        {
            GUILayout.Space(12);
            helpExpanded = EditorGUILayout.Foldout(helpExpanded, "使用说明", true, PasswordLockUi.Foldout);
            if (!helpExpanded) return;
            PasswordLockUi.BeginPanel(null);
            PasswordLockUi.Note("编辑时正常显示，VRChat 加载角色时由 FX 锁定。开启记住解锁状态后，切换回来会恢复已解锁状态；清空可重新锁定。数字逐个按下，每次等待按钮复位。密码允许 1–20 位，可使用 0–9。\n\n密码锁不会关闭骨架、声音或交互组件。Unity Play 自动跳过密码锁；上传构建时自动加入。Unity Play 轮盘不生成密码锁菜单，验证请使用 SDK Build & Test 或进入 VRChat。加载首帧与第三方插件兼容性仍需验证。");
            PasswordLockUi.EndPanel();
        }
    }
}
