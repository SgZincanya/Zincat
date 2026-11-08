using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

public class BatchImportPackagesEditor : EditorWindow
{
    // ========== 字段定义 ==========
    private string folderPath = "Assets";            // 默认扫描路径
    private bool importRecursively = false;          // 是否递归子文件夹
    private List<string> scannedPackages = new List<string>();                // 扫描到的所有 .unitypackage 与 .zip 路径
    private Dictionary<string, bool> packageSelection = new Dictionary<string, bool>();  // 每个包是否被勾选导入
    private Vector2 scrollPos;                       // 列表滚动位置

    private bool deleteZipAfterImport = false;       // 导入后是否删除 .zip 文件
    private bool moveImportedPackages = false;       // 导入后是否移动原始包到指定文件夹
    private string importedPackagesFolder = "Assets/ImportedPackages";  // 移动目标文件夹（当 moveImportedPackages 为 true 时使用）

    private bool isImporting = false;                // 当前是否正在导入，防止重复点击
    private bool cancelRequested = false;            // 是否用户点击了“取消”进度条

    // ========== 菜单与窗口初始化 ==========
    [MenuItem("Zincat/批量导入包")]
    public static void ShowWindow()
    {
        GetWindow<BatchImportPackagesEditor>("批量导入包");
    }

    private void OnEnable()
    {
        // 窗口打开或重置时，清空扫描列表和选项
        scannedPackages.Clear();
        packageSelection.Clear();
    }

    // ========== 界面绘制 ==========
    private void OnGUI()
    {
        GUILayout.Label("批量导入 Unity Package 工具", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // -- 第一行：选择文件夹 --
        GUILayout.BeginHorizontal();
        GUILayout.Label("选择文件夹：", GUILayout.Width(80));
        folderPath = GUILayout.TextField(folderPath);
        if (GUILayout.Button("浏览", GUILayout.Width(60)))
        {
            string path = EditorUtility.OpenFolderPanel("选择文件夹", folderPath, "");
            if (!string.IsNullOrEmpty(path))
            {
                folderPath = path;
            }
        }
        GUILayout.EndHorizontal();

        // 递归子文件夹的开关
        importRecursively = GUILayout.Toggle(importRecursively, "递归子文件夹");
        EditorGUILayout.Space();

        // -- 扫描按钮 --
        if (GUILayout.Button("扫描包"))
        {
            ScanPackages();
        }
        EditorGUILayout.Space();

        // -- 拖拽区域：可以直接拖入 .unitypackage 和 .zip --
        Rect dropArea = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));
        GUI.Box(dropArea, "把 .unitypackage 或 .zip 文件拖到这里");
        HandleDragAndDrop(dropArea);

        // -- 如果扫描或拖拽到了包，显示列表及操作项 --
        if (scannedPackages.Count > 0)
        {
            EditorGUILayout.Space();
            GUILayout.Label($"共找到 {scannedPackages.Count} 个包，请勾选要导入的项目：", EditorStyles.label);

            // 滚动列表：显示所有扫描到的路径，前面有勾选框
            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(200));
            foreach (var path in scannedPackages)
            {
                if (!packageSelection.ContainsKey(path))
                    packageSelection[path] = true;

                // 用文件名做显示，勾选状态保存到 packageSelection
                packageSelection[path] = GUILayout.Toggle(packageSelection[path], Path.GetFileName(path));
            }
            GUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(isImporting || !packageSelection.ContainsValue(true)))
            {
                if (GUILayout.Button("移除选中项")) RemoveSelectedPackagesFromList();
            }

            EditorGUILayout.Space();

            // -- 选项：导入后是否删除 .zip，是否移动到指定文件夹 --
            deleteZipAfterImport = GUILayout.Toggle(deleteZipAfterImport, "导入后删除 .zip 文件");
            moveImportedPackages = GUILayout.Toggle(moveImportedPackages, "导入后移动原始文件到指定文件夹");
            if (moveImportedPackages)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("移动到：", GUILayout.Width(40));
                importedPackagesFolder = GUILayout.TextField(importedPackagesFolder);
                if (GUILayout.Button("创建文件夹", GUILayout.Width(80)))
                {
                    // 如果指定目录不存在，先创建
                    if (!Directory.Exists(importedPackagesFolder))
                    {
                        Directory.CreateDirectory(importedPackagesFolder);
                        AssetDatabase.Refresh();  // 刷新以便在 Project 视图看到新文件夹
                    }
                }
                GUILayout.EndHorizontal();
            }

            EditorGUILayout.Space();

            // -- 左侧“模拟运行”，右侧“开始导入”，只有当不在导入中时才可点击 --
            GUILayout.BeginHorizontal();
            GUI.enabled = !isImporting;
            if (GUILayout.Button("模拟运行（仅列出）"))
            {
                DryRun();
            }
            if (GUILayout.Button("开始导入"))
            {
                StartImport();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
    }

    private int RemoveSelectedPackagesFromList()
    {
        if (isImporting) return 0;
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in packageSelection)
            if (item.Value) selected.Add(item.Key);
        int removed = scannedPackages.RemoveAll(path => selected.Contains(path));
        foreach (string path in selected) packageSelection.Remove(path);
        Repaint();
        return removed;
    }

    // ========== 拖拽逻辑 ==========
    private void HandleDragAndDrop(Rect dropArea)
    {
        Event ev = Event.current;
        if ((ev.type == EventType.DragUpdated || ev.type == EventType.DragPerform) && dropArea.Contains(ev.mousePosition))
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (ev.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var path in DragAndDrop.paths)
                {
                    if (path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!scannedPackages.Contains(path))
                        {
                            scannedPackages.Add(path);
                            packageSelection[path] = true;
                        }
                    }
                }
            }
            ev.Use();
        }
    }

    // ========== 扫描文件夹 ==========
    private void ScanPackages()
    {
        scannedPackages.Clear();
        packageSelection.Clear();

        if (!Directory.Exists(folderPath))
        {
            EditorUtility.DisplayDialog("错误", "所选路径不存在！", "确定");
            return;
        }

        SearchOption option = importRecursively ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        try
        {
            // 查找 .unitypackage
            var unityPackages = Directory.GetFiles(folderPath, "*.unitypackage", option);
            // 查找 .zip
            var zipFiles = Directory.GetFiles(folderPath, "*.zip", option);

            foreach (var path in unityPackages)
            {
                scannedPackages.Add(path);
                packageSelection[path] = true;
            }
            foreach (var path in zipFiles)
            {
                scannedPackages.Add(path);
                packageSelection[path] = true;
            }

            EditorUtility.DisplayDialog("扫描完成", $"共找到 {scannedPackages.Count} 个包。", "好的");
        }
        catch (Exception ex)
        {
            Debug.LogError($"扫描时出错：{ex.Message}");
            EditorUtility.DisplayDialog("错误", $"扫描时出错：{ex.Message}", "确定");
        }
    }

    // ========== 模拟运行（Dry Run）：仅在控制台列出 ==========
    private void DryRun()
    {
        int count = 0;
        Debug.Log("===== 模拟运行：以下为将要导入的包列表 =====");
        foreach (var kv in packageSelection)
        {
            if (kv.Value)
            {
                Debug.Log(kv.Key);
                count++;
            }
        }
        Debug.Log($"===== 模拟运行完成，共 {count} 个包 =====");
        EditorUtility.DisplayDialog("模拟运行", $"共列出 {count} 个待导入包（请查看控制台）。", "好的");
    }

    // ========== 启动导入流程 ==========
    private void StartImport()
    {
        if (isImporting)
            return;

        // 开始导入前，标记状态并阻止外部刷新
        isImporting = true;
        cancelRequested = false;
        AssetDatabase.StartAssetEditing();

        try
        {
            ImportSelectedPackages();
        }
        finally
        {
            // 不论导入过程是否出错，都要停止编辑并刷新
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
            EditorUtility.ClearProgressBar();
            isImporting = false;
        }
    }

    // ========== 主导入逻辑（同步实现） ==========
    private void ImportSelectedPackages()
    {
        // 计算总共要导入的包数
        int total = 0;
        foreach (var kv in packageSelection)
            if (kv.Value) total++;

        if (total == 0)
        {
            EditorUtility.DisplayDialog("提示", "未选择任何包进行导入。", "好的");
            return;
        }

        int processedItems = 0;   // 已处理的项目数（成功 + 失败）
        int successCount = 0;     // 成功导入计数
        int failCount = 0;        // 导入失败计数

        foreach (var kv in packageSelection)
        {
            if (cancelRequested)
                break;

            if (!kv.Value)
                continue;

            string path = kv.Key;
            // 计算进度和当前索引
            float progress = (float)processedItems / total;
            bool userCanceled = EditorUtility.DisplayCancelableProgressBar(
                "批量导入中",
                $"正在处理：{Path.GetFileName(path)} （{processedItems + 1}/{total}）",
                progress);
            if (userCanceled)
            {
                cancelRequested = true;
                Debug.LogWarning("用户取消了导入操作");
                break;
            }

            // 如果是 .zip 文件，先解压，再导入内部所有 .unitypackage
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string tempFolder = CreateUniqueTempFolder(path);
                try
                {
                    // 解压到临时目录
                    ZipFile.ExtractToDirectory(path, tempFolder);
                    var innerPackages = Directory.GetFiles(tempFolder, "*.unitypackage", SearchOption.AllDirectories);
                    foreach (var inner in innerPackages)
                    {
                        try
                        {
                            AssetDatabase.ImportPackage(inner, false);
                            successCount++;
                            Debug.Log($"导入成功：{inner}");
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            Debug.LogError($"导入包失败：{inner}，原因：{ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failCount++;
                    Debug.LogError($"ZIP 解压失败：{path}，原因：{ex.Message}");
                }
                finally
                {
                    TryDeleteFolder(tempFolder);
                }

                // 处理原始 ZIP 文件（删除或移动）
                HandleOriginalFile(path);
            }
            // 如果是 .unitypackage 文件，直接导入
            else if (path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    AssetDatabase.ImportPackage(path, false);
                    successCount++;
                    Debug.Log($"导入成功：{path}");
                }
                catch (Exception ex)
                {
                    failCount++;
                    Debug.LogError($"导入包失败：{path}，原因：{ex.Message}");
                }

                // 处理原始 UnityPackage（移动到指定文件夹）
                HandleOriginalFile(path);
            }

            processedItems++;
        }

        // 导入结束后，弹窗提示
        if (cancelRequested)
        {
            EditorUtility.DisplayDialog("已取消", $"导入已取消，成功 {successCount} 个，失败 {failCount} 个。", "好的");
        }
        else
        {
            EditorUtility.DisplayDialog("导入完成", $"成功导入 {successCount} 个，失败 {failCount} 个。", "好的");
        }
    }

    // ========== 创建临时解压文件夹（带 GUID 保证唯一） ==========
    private string CreateUniqueTempFolder(string originalPath)
    {
        string baseName = Path.GetFileNameWithoutExtension(originalPath);
        string guid = Guid.NewGuid().ToString("N");
        string tempFolder = Path.Combine(Path.GetTempPath(), $"{baseName}_{guid}");
        try
        {
            if (Directory.Exists(tempFolder))
                Directory.Delete(tempFolder, true);
            Directory.CreateDirectory(tempFolder);
        }
        catch (Exception ex)
        {
            Debug.LogError($"创建临时目录失败：{ex.Message}");
        }
        return tempFolder;
    }

    // ========== 尝试删除临时解压目录（如失败，延迟到下次编辑器空闲时再删） ==========
    private void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
        catch (IOException ex)
        {
            Debug.LogWarning($"删除临时目录时出错：{ex.Message}，将在下次尝试删除。");
            EditorApplication.delayCall += () =>
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            };
        }
    }

    // ========== 处理原始文件（删除 ZIP、移动到指定文件夹） ==========
    private void HandleOriginalFile(string originalPath)
    {
        // —— 删除 .zip 文件 —— //
        if (deleteZipAfterImport && originalPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(originalPath);
                Debug.Log($"已删除 ZIP：{originalPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"删除 ZIP 时出错：{ex.Message}");
            }
        }

        // —— 移动原始文件到指定文件夹 —— //
        if (moveImportedPackages)
        {
            try
            {
                if (!Directory.Exists(importedPackagesFolder))
                {
                    Directory.CreateDirectory(importedPackagesFolder);
                }
                string fileName = Path.GetFileName(originalPath);
                string destPath = Path.Combine(importedPackagesFolder, fileName);

                // 如果目标已存在，则在文件名后加上时间戳
                if (File.Exists(destPath))
                {
                    string nameNoExt = Path.GetFileNameWithoutExtension(fileName);
                    string ext = Path.GetExtension(fileName);
                    string newName = $"{nameNoExt}_{DateTime.Now.ToString("yyyyMMddHHmmss")}{ext}";
                    destPath = Path.Combine(importedPackagesFolder, newName);
                }

                File.Move(originalPath, destPath);
                Debug.Log($"已移动文件：{originalPath} → {destPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"移动文件时出错：{ex.Message}");
            }
        }
    }
}
