using System;
using System.Linq;
using Zincat.EditorTools;
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.IO;

public class CameraCaptureTool : EditorWindow
{
    // —— 截图相关字段 —— 
    [SerializeField] private List<Camera> targetCameras = new List<Camera>();
    private string saveFolderPath = "CapturedImages"; // 相对于 Assets 的路径
    private int width = 1080;
    private int height = 1440;

    private enum ResolutionPreset
    {
        自定义,
        _1080p_竖屏_1080x1440,
        _1080p_横屏_1440x1080,
        _2K_竖屏_1536x2048,
        _2K_横屏_2048x1536,
        _4K_竖屏_3072x4096,
        _4K_横屏_4096x3072
    }
    private ResolutionPreset preset = ResolutionPreset.自定义;

    [MenuItem("Zincat/照相机（仅截图）")]
    public static void ShowWindow()
    {
        CameraCaptureTool window = GetWindow<CameraCaptureTool>("照相机");
        window.LoadPreferences();
    }

    private void OnEnable() { LoadPreferences(); }
    private void OnDisable() { SavePreferences(); }

    private void OnGUI()
    {
        GUILayout.Label("截图设置", EditorStyles.boldLabel);

        // —— 一键选取场景里所有摄像机（排除 Main Camera）——
        if (GUILayout.Button("选择场景中的所有摄像机（除 Main Camera）"))
        {
            targetCameras.Clear();
            Camera[] allCams = FindObjectsOfType<Camera>();
            foreach (var cam in allCams)
            {
                if (cam != null && cam.name != "Main Camera")
                    targetCameras.Add(cam);
            }
        }

        GUILayout.Space(8);
        GUILayout.Label("目标摄像机列表", EditorStyles.boldLabel);
        if (GUILayout.Button("添加一个摄像机槽位")) targetCameras.Add(null);
        for (int i = 0; i < targetCameras.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                targetCameras[i] = (Camera)EditorGUILayout.ObjectField(targetCameras[i], typeof(Camera), true);
                if (GUILayout.Button("移除", GUILayout.Width(45))) { targetCameras.RemoveAt(i); i--; }
            }
        }

        GUILayout.Space(8);
        GUILayout.Label("分辨率预设（4:3）", EditorStyles.boldLabel);
        ResolutionPreset previous = preset;
        preset = (ResolutionPreset)EditorGUILayout.EnumPopup(preset);
        if (preset != previous)
        {
            switch (preset)
            {
                case ResolutionPreset._1080p_竖屏_1080x1440:
                    width = 1080; height = 1440;
                    break;
                case ResolutionPreset._1080p_横屏_1440x1080:
                    width = 1440; height = 1080;
                    break;
                case ResolutionPreset._2K_竖屏_1536x2048:
                    width = 1536; height = 2048;
                    break;
                case ResolutionPreset._2K_横屏_2048x1536:
                    width = 2048; height = 1536;
                    break;
                case ResolutionPreset._4K_竖屏_3072x4096:
                    width = 3072; height = 4096;
                    break;
                case ResolutionPreset._4K_横屏_4096x3072:
                    width = 4096; height = 3072;
                    break;
                case ResolutionPreset.自定义:
                    // 保持手动填写
                    break;
            }
        }

        GUILayout.Space(4);
        using (new EditorGUI.DisabledScope(preset != ResolutionPreset.自定义))
        {
            int newW = EditorGUILayout.IntField("宽度 (px)", width);
            int newH = EditorGUILayout.IntField("高度 (px)", height);
            if (newW != width || newH != height)
            {
                width = Mathf.Max(1, newW);
                height = Mathf.Max(1, newH);
            }
        }

        GUILayout.Space(8);
        saveFolderPath = EditorGUILayout.TextField("Assets 内输出文件夹", saveFolderPath);

        GUILayout.Space(12);
        // —— 截图按钮 —— 
        if (GUILayout.Button("捕捉截图"))
        {
            if (targetCameras.Count > 0)
            {
                CaptureCameras();
                SavePreferences();
            }
            else
            {
                Debug.LogError("请至少选择一个目标摄像机！");
            }
        }

        GUILayout.Space(10);
        // —— 添加绑定好的摄像头预制体按钮 —— 
        if (GUILayout.Button("添加摄像头"))
        {
            AddCenterCamera();
        }
    }

    #region 截图逻辑
    private void CaptureCameras()
    {
        int completed = 0, failed = 0;
        bool cancelled = false;
        try
        {
            var cameras = targetCameras.Where(c => c != null && !EditorUtility.IsPersistent(c)).Distinct().ToList();
            if (cameras.Count == 0) throw new ArgumentException("请指定至少一个场景摄像机。");
            if (width < 1 || height < 1 || width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize)
                throw new ArgumentException("分辨率超出当前设备支持的范围。");
            if (string.IsNullOrWhiteSpace(saveFolderPath) || Path.IsPathRooted(saveFolderPath))
                throw new ArgumentException("请填写相对于 Assets 的文件夹，例如 CapturedImages。");
            string assetsRoot = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, saveFolderPath));
            if (!output.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输出文件夹必须位于 Assets 内。");
            Directory.CreateDirectory(output);
            for (int i = 0; i < cameras.Count; i++)
            {
                Camera camera = cameras[i];
                if (EditorUtility.DisplayCancelableProgressBar("截图", camera.name, (float)i / cameras.Count))
                { cancelled = true; break; }
                try
                {
                    byte[] bytes = CaptureService.Capture(camera, width, height);
                    string fileName = CaptureService.SafeFileName(camera.name) + "_" +
                        DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png";
                    File.WriteAllBytes(Path.Combine(output, fileName), bytes);
                    completed++;
                }
                catch (Exception ex) { failed++; Debug.LogError("[Zincat截图] " + camera.name + "：" + ex.Message); }
            }
            EditorUtility.DisplayDialog(cancelled ? "截图已停止" : "截图完成", $"成功 {completed} 张，失败 {failed} 张。\n输出：{output}", "确定");
        }
        catch (Exception ex) { EditorUtility.DisplayDialog("截图失败", ex.Message, "确定"); }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
        }
    }
    #endregion

    #region 添加“中心展示摄像头”预制体
    private void AddCenterCamera()
    {
        // 尝试在当前场景里查找名为“中心展示摄像头”的对象
        GameObject existing = GameObject.Find("中心展示摄像头");
        if (existing != null)
        {
            Debug.Log("场景中已存在“中心展示摄像头”对象，无需重复添加。");
            return;
        }

        // 如果没有，就从指定路径加载预制体
        string prefabPath = ToolResources.Resolve("545303b1655e0f345a481cac0346e420",
            "Assets/Zincat/Editor/ToolAssets/Cam/中心展示摄像头.prefab");
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            Debug.LogError($"未能加载预制体：{prefabPath}，请确认该路径和文件名正确。");
            return;
        }

        // 实例化到场景中，并确保其名字是“中心展示摄像头”
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        if (instance != null)
        {
            Undo.RegisterCreatedObjectUndo(instance, "添加展示摄像头");
            instance.name = "中心展示摄像头";
            // 将其置于原点，也可根据需求调整
            instance.transform.position = Vector3.zero;
            Debug.Log("已在场景中添加“中心展示摄像头”预制体实例。");
        }
        else
        {
            Debug.LogError("实例化预制体失败。");
        }
    }
    #endregion

    #region 保存/加载 编辑器偏好
    private void SavePreferences()
    {
        EditorPrefs.SetString("CameraCaptureTool_SaveFolderPath", saveFolderPath);
        EditorPrefs.SetInt("CameraCaptureTool_Width", width);
        EditorPrefs.SetInt("CameraCaptureTool_Height", height);
        EditorPrefs.SetInt("CameraCaptureTool_PresetIndex", (int)preset);
    }

    private void LoadPreferences()
    {
        saveFolderPath = EditorPrefs.GetString("CameraCaptureTool_SaveFolderPath", "CapturedImages");
        width = EditorPrefs.GetInt("CameraCaptureTool_Width", 1080);
        height = EditorPrefs.GetInt("CameraCaptureTool_Height", 1440);
        preset = (ResolutionPreset)EditorPrefs.GetInt("CameraCaptureTool_PresetIndex", 0);
    }
    #endregion
}
