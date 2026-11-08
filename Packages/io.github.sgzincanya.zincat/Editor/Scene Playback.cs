using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zincat.EditorTools;

public class SceneAnimationSetup : EditorWindow
{
    // =========================
    // 路径设置
    // =========================

    private static string DanceRootPath => ToolResources.Resolve("d00777e69695c30488325107f7f14f1b",
        "Assets/Zincat/Editor/ToolAssets/Dance");

    // =========================
    // 数据
    // =========================

    private class DanceData
    {
        public string folderPath;
        public string folderName;
        public string controllerPath;
        public string prefabPath;

        public RuntimeAnimatorController controller;
        public GameObject prefab;

        public bool IsValid =>
            controller != null && prefab != null;
    }

    private List<DanceData> dances = new List<DanceData>();

    // =========================
    // UI 状态
    // =========================

    [SerializeField] private GameObject selectedGameObject;
    private string selectedDancePath = "";

    private string searchText = "";
    private Vector2 scrollPosition;

    private bool showInvalidDances = false;

    // =========================
    // 实例化对象
    // =========================

    [SerializeField] private GameObject instantiatedPrefab;
    [SerializeField] private Animator previewAnimator;
    [SerializeField] private RuntimeAnimatorController originalController;
    [SerializeField] private bool addedAnimator;
    [SerializeField] private bool previewActive;
    private string previewMessage = "";

    // =========================
    // 菜单
    // =========================

    [MenuItem("Zincat/MMD 舞蹈测试")]
    public static void ShowWindow()
    {
        SceneAnimationSetup window =
            GetWindow<SceneAnimationSetup>("MMD 舞蹈测试");

        window.minSize = new Vector2(360, 500);
    }

    // =========================
    // 生命周期
    // =========================

    private void OnEnable()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
        EditorApplication.quitting += StopPreview;

        RefreshDanceList();

    }

    private void OnDisable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload -= StopPreview;
        EditorApplication.quitting -= StopPreview;

        // 窗口关闭时恢复角色并清理测试对象。
        StopPreview();
    }

    private void OnFocus()
    {
        // Window focus is navigation, never an instruction to replace the preview target.
        Repaint();
    }

    // =========================
    // 播放模式状态
    // =========================

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode) StopPreview();
        if (state == PlayModeStateChange.EnteredEditMode)
        {
            selectedDancePath = "";
            ClearInstantiatedPrefab();

            Repaint();
        }
    }

    // =========================
    // GUI
    // =========================

    private void OnGUI()
    {
        DrawHeader();

        GUILayout.Space(8);

        DrawTargetSection();

        GUILayout.Space(10);

        DrawPlayModeSection();

        GUILayout.Space(10);

        DrawDanceSection();
    }

    // =========================
    // 标题
    // =========================

    private void DrawHeader()
    {
        GUILayout.Label(
            "MMD 舞蹈测试器",
            EditorStyles.boldLabel
        );

        GUILayout.Label(
            "作者：Zincanya",
            EditorStyles.miniLabel
        );
    }

    // =========================
    // 测试对象
    // =========================

    private void DrawTargetSection()
    {
        GUILayout.BeginVertical(EditorStyles.helpBox);

        GUILayout.Label(
            "测试对象",
            EditorStyles.boldLabel
        );

        GameObject requestedTarget =
            (GameObject)EditorGUILayout.ObjectField(
                "角色对象",
                selectedGameObject,
                typeof(GameObject),
                true
            );
        if (requestedTarget != selectedGameObject) SetTarget(requestedTarget);
        EditorGUILayout.HelpBox("测试角色保持固定。选择其他物件或切换窗口不会更换角色；需要更换时请拖入角色或点击下方按钮。", MessageType.Info);
        if (!string.IsNullOrEmpty(previewMessage)) EditorGUILayout.HelpBox(previewMessage, MessageType.Warning);

        GUILayout.Space(4);

        if (GUILayout.Button("将当前选中对象设为测试角色"))
        {
            UpdateSelectionFromHierarchy();
        }

        if (selectedGameObject != null)
        {
            Animator animator =
                selectedGameObject.GetComponent<Animator>();

            if (animator != null)
            {
                EditorGUILayout.HelpBox(
                    "已检测到 Animator。",
                    MessageType.Info
                );
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "该对象没有 Animator，选择舞蹈时会自动添加。",
                    MessageType.Warning
                );
            }
        }
        else
        {
            EditorGUILayout.HelpBox(
                "请先选择需要测试舞蹈的角色对象。",
                MessageType.Warning
            );
        }

        GUILayout.EndVertical();
    }

    // =========================
    // 播放控制
    // =========================

    private void DrawPlayModeSection()
    {
        GUILayout.BeginVertical(EditorStyles.helpBox);

        GUILayout.Label(
            "播放控制",
            EditorStyles.boldLabel
        );

        if (EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "当前处于播放模式，可以选择舞蹈。",
                MessageType.Info
            );
        }
        else
        {
            EditorGUILayout.HelpBox(
                "进入播放模式后才能测试舞蹈。",
                MessageType.None
            );
        }

        GUILayout.Space(4);

        EditorGUI.BeginDisabledGroup(
            EditorApplication.isPlaying
        );

        if (GUILayout.Button("▶ 进入播放模式", GUILayout.Height(28)))
        {
            if (selectedGameObject == null)
            {
                EditorUtility.DisplayDialog(
                    "无法进入播放模式",
                    "请先选择一个角色对象。",
                    "确定"
                );

                EditorGUI.EndDisabledGroup();
                GUILayout.EndVertical();
                return;
            }

            EditorApplication.EnterPlaymode();
        }

        EditorGUI.EndDisabledGroup();

        EditorGUI.BeginDisabledGroup(
            !EditorApplication.isPlaying
        );

        if (GUILayout.Button("■ 结束播放", GUILayout.Height(28)))
        {
            StopPreview();
            EditorApplication.ExitPlaymode();
        }

        EditorGUI.EndDisabledGroup();

        GUILayout.EndVertical();
    }

    // =========================
    // 舞蹈区域
    // =========================

    private void DrawDanceSection()
    {
        GUILayout.BeginVertical(EditorStyles.helpBox);

        GUILayout.Label(
            "舞蹈动画",
            EditorStyles.boldLabel
        );

        // 搜索框
        GUILayout.BeginHorizontal();

        searchText = EditorGUILayout.TextField(
            "搜索",
            searchText
        );

        if (GUILayout.Button(
            "×",
            GUILayout.Width(25)
        ))
        {
            searchText = "";
            GUI.FocusControl(null);
        }

        GUILayout.EndHorizontal();

        GUILayout.Space(4);

        // 刷新按钮
        GUILayout.BeginHorizontal();

        if (GUILayout.Button("刷新舞蹈列表"))
        {
            RefreshDanceList();
        }

        if (GUILayout.Button("结束测试并恢复角色"))
        {
            StopPreview();
        }

        GUILayout.EndHorizontal();

        GUILayout.Space(5);

        showInvalidDances =
            EditorGUILayout.ToggleLeft(
                "显示资源不完整的舞蹈",
                showInvalidDances
            );

        GUILayout.Space(5);

        // 非播放模式禁用
        EditorGUI.BeginDisabledGroup(
            !EditorApplication.isPlaying
        );

        DrawDanceList();

        EditorGUI.EndDisabledGroup();

        GUILayout.EndVertical();
    }

    // =========================
    // 舞蹈列表
    // =========================

    private void DrawDanceList()
    {
        List<DanceData> filteredDances =
            dances
                .Where(d =>
                    showInvalidDances || d.IsValid)
                .Where(d =>
                    string.IsNullOrWhiteSpace(searchText) ||
                    d.folderName.IndexOf(
                        searchText,
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0)
                .ToList();

        if (filteredDances.Count == 0)
        {
            if (dances.Count == 0)
            {
                GUILayout.Label(
                    "未找到任何舞蹈动画。",
                    EditorStyles.centeredGreyMiniLabel
                );
            }
            else
            {
                GUILayout.Label(
                    "没有符合搜索条件的舞蹈。",
                    EditorStyles.centeredGreyMiniLabel
                );
            }

            return;
        }

        scrollPosition =
            EditorGUILayout.BeginScrollView(
                scrollPosition,
                GUILayout.MinHeight(180)
            );

        foreach (DanceData dance in filteredDances)
        {
            DrawDanceButton(dance);
        }

        EditorGUILayout.EndScrollView();
    }

    // =========================
    // 舞蹈按钮
    // =========================

    private void DrawDanceButton(DanceData dance)
    {
        bool isSelected =
            selectedDancePath == dance.folderPath;

        bool isValid =
            dance.IsValid;

        GUIStyle buttonStyle =
            new GUIStyle(GUI.skin.button);

        if (isSelected)
        {
            buttonStyle.fontStyle =
                FontStyle.Bold;
        }

        string buttonText;

        if (isValid)
        {
            buttonText = isSelected
                ? "✓ " + dance.folderName
                : dance.folderName;
        }
        else
        {
            buttonText =
                "⚠ " + dance.folderName;
        }

        EditorGUI.BeginDisabledGroup(
            !isValid || isSelected
        );

        if (GUILayout.Button(
            buttonText,
            buttonStyle,
            GUILayout.Height(30)
        ))
        {
            SelectDance(dance);
        }

        EditorGUI.EndDisabledGroup();

        if (!isValid && showInvalidDances)
        {
            EditorGUILayout.HelpBox(
                GetInvalidReason(dance),
                MessageType.Warning
            );
        }
    }

    // =========================
    // 选择舞蹈
    // =========================

    private void SelectDance(DanceData dance)
    {
        if (selectedGameObject == null)
        {
            EditorUtility.DisplayDialog(
                "无法播放舞蹈",
                "请先选择一个角色对象。",
                "确定"
            );

            return;
        }

        if (!EditorApplication.isPlaying)
        {
            return;
        }

        if (!dance.IsValid || EditorUtility.IsPersistent(selectedGameObject) || !selectedGameObject.scene.IsValid())
        {
            previewMessage = "请选择场景中的角色，并确认舞蹈资源完整。";
            return;
        }
        StopPreview();
        try
        {
            AssignAnimatorController(dance.controller);
            InstantiatePrefab(dance.prefab);
            if (instantiatedPrefab == null) throw new InvalidOperationException("无法创建舞蹈测试对象。");
            selectedDancePath = dance.folderPath;
            previewMessage = "";
        }
        catch (Exception ex)
        {
            StopPreview();
            previewMessage = "测试失败：" + ex.Message;
            Debug.LogException(ex);
        }
        Repaint();
    }

    // =========================
    // Animator
    // =========================

    private void AssignAnimatorController(
        RuntimeAnimatorController controller)
    {
        if (selectedGameObject == null)
        {
            Debug.LogError(
                "MMD舞蹈测试：没有选择角色对象。"
            );

            return;
        }

        previewAnimator = selectedGameObject.GetComponent<Animator>();
        addedAnimator = previewAnimator == null;
        if (addedAnimator) previewAnimator = selectedGameObject.AddComponent<Animator>();
        originalController = previewAnimator.runtimeAnimatorController;
        previewActive = true;
        previewAnimator.runtimeAnimatorController = controller;
    }

    private void StopPreview()
    {
        ClearInstantiatedPrefab();
        if (previewActive && previewAnimator != null)
        {
            previewAnimator.runtimeAnimatorController = originalController;
            if (addedAnimator) DestroyImmediate(previewAnimator);
        }
        previewAnimator = null;
        originalController = null;
        addedAnimator = false;
        previewActive = false;
        selectedDancePath = "";
    }

    // =========================
    // 实例化 Prefab
    // =========================

    private void InstantiatePrefab(
        GameObject prefab)
    {
        ClearInstantiatedPrefab();

        if (prefab == null)
        {
            Debug.LogError(
                "MMD舞蹈测试：Prefab 不存在。"
            );

            return;
        }

        instantiatedPrefab =
            (GameObject)PrefabUtility.InstantiatePrefab(
                prefab
            );

        if (instantiatedPrefab != null)
        {
            Debug.Log(
                $"MMD舞蹈测试：已实例化 {prefab.name}"
            );
        }
    }

    // =========================
    // 清理实例
    // =========================

    private void ClearInstantiatedPrefab()
    {
        if (instantiatedPrefab == null)
            return;

        DestroyImmediate(
            instantiatedPrefab
        );

        instantiatedPrefab = null;
    }

    // =========================
    // 获取当前 Hierarchy 对象
    // =========================

    private void UpdateSelectionFromHierarchy()
    {
        GameObject target = Selection.activeGameObject;
        if (target == null)
        {
            previewMessage = "请先在 Hierarchy 中选中角色根对象，再点击此按钮。";
            return;
        }
        SetTarget(target);
    }

    private void SetTarget(GameObject target)
    {
        if (target != null && (EditorUtility.IsPersistent(target) || !target.scene.IsValid()))
        {
            previewMessage = "请选择场景中的角色对象。";
            return;
        }
        if (target != null && instantiatedPrefab != null &&
            (target == instantiatedPrefab || target.transform.IsChildOf(instantiatedPrefab.transform)))
        {
            previewMessage = "舞蹈的临时测试对象不能设为角色。";
            return;
        }
        if (target != selectedGameObject)
        {
            StopPreview();
            selectedGameObject = target;
        }
        previewMessage = "";
        Repaint();
    }

    // =========================
    // 扫描舞蹈
    // =========================

    private void RefreshDanceList()
    {
        dances.Clear();

        if (!AssetDatabase.IsValidFolder(DanceRootPath))
        {
            Debug.LogError(
                $"MMD舞蹈测试：路径不存在：{DanceRootPath}"
            );

            return;
        }

        string[] guids =
            AssetDatabase.FindAssets(
                "t:Folder",
                new[] { DanceRootPath }
            );

        HashSet<string> processed =
            new HashSet<string>();

        foreach (string guid in guids)
        {
            string folderPath =
                AssetDatabase.GUIDToAssetPath(guid);

            // 排除根目录本身
            if (folderPath == DanceRootPath)
                continue;

            if (!processed.Add(folderPath))
                continue;

            string folderName =
                Path.GetFileName(folderPath);

            string controllerPath =
                $"{folderPath}/mmd.controller";

            string prefabPath =
                $"{folderPath}/{folderName}.prefab";

            RuntimeAnimatorController controller =
                AssetDatabase.LoadAssetAtPath<
                    RuntimeAnimatorController>(
                        controllerPath
                    );

            GameObject prefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(
                    prefabPath
                );

            // 只记录一级/多级文件夹，
            // 但最终通过资源检查决定是否有效
            DanceData dance =
                new DanceData
                {
                    folderPath = folderPath,
                    folderName = folderName,
                    controllerPath = controllerPath,
                    prefabPath = prefabPath,
                    controller = controller,
                    prefab = prefab
                };

            dances.Add(dance);
        }

        dances = dances
            .OrderBy(d => d.folderName,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        Debug.Log(
            $"MMD舞蹈测试：扫描完成，共找到 {dances.Count} 个舞蹈文件夹。"
        );

        Repaint();
    }

    // =========================
    // 获取资源错误原因
    // =========================

    private string GetInvalidReason(
        DanceData dance)
    {
        List<string> reasons =
            new List<string>();

        if (dance.controller == null)
        {
            reasons.Add(
                $"缺少：{dance.controllerPath}"
            );
        }

        if (dance.prefab == null)
        {
            reasons.Add(
                $"缺少：{dance.prefabPath}"
            );
        }

        return string.Join(
            "\n",
            reasons
        );
    }
}
