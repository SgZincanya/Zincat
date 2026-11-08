// Assets/Editor/BlendshapeWeightsMergerWindow.cs
// BlendShape 权重合成工具（滑条 + 剪贴板极速解析 + 并排布局 + 预览折叠不撑窗）
// - 来源：SkinnedMeshRenderer 组件 / 剪贴板(GenericPropertyJSON 文本)
// - 合成：平均 / 加权(滑条)；滑条后仅 1 个数值框；实时显示 A/B 权重（≈整数比）
// - 预设：90:10 / 95:5 / 99:1 / 100:0 / 0:100
// - 预览：剪贴板原文“可折叠 + 固定高度滚动区”，默认折叠，不撑高窗口
// - 导出：GenericPropertyJSON（带前缀、字段顺序固定、size+data、紧凑无空格）
// - 操作：复制到剪贴板 / 导出文件 / 直接应用到目标 SMR
//
// 放到 Assets/Editor/ 下；兼容 Unity 2020+
//
// Copyright: Free to use.

using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Globalization;
using System.Diagnostics; // Stopwatch
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Text.RegularExpressions;

public class BlendshapeWeightsMergerWindow : EditorWindow
{
    // —— 数据渠道 —— //
    private enum SourceChannel { 组件, 剪贴板 }

    // —— A 源 —— //
    private SourceChannel channelA = SourceChannel.组件;
    private SkinnedMeshRenderer sourceA;
    private string clipboardTextA = "";
    private float[] clipWeightsA = null;
    private string clipInfoA = "(未读取)";
    private bool foldA = false;
    private Vector2 scrollA = Vector2.zero;

    // —— B 源 —— //
    private SourceChannel channelB = SourceChannel.组件;
    private SkinnedMeshRenderer sourceB;
    private string clipboardTextB = "";
    private float[] clipWeightsB = null;
    private string clipInfoB = "(未读取)";
    private bool foldB = false;
    private Vector2 scrollB = Vector2.zero;

    // —— 目标 —— //
    private SkinnedMeshRenderer applyTarget;

    // —— 合成设置 —— //
    private enum Mode { 平均, 加权 }
    private Mode mode = Mode.加权;

    [Range(0f, 1f)]
    private float aRatio01 = 0.9f;           // A 比例（B=1-A）
    private bool strictNameMatch = true;     // 校验有名称信息的来源与目标

    // —— 其它 —— //
    private string lastMsg = "";

    // 预览样式/高度/截断长度
    private const float PreviewFixedHeight = 64f;
    private const int   PreviewChars      = 260;
    private static GUIStyle _previewStyle;
    private static GUIStyle PreviewStyle
    {
        get
        {
            if (_previewStyle == null)
            {
                _previewStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true, richText = false };
            }
            return _previewStyle;
        }
    }

    [MenuItem("Zincat/面部权重合成与导出")]
    public static void Open() => GetWindow<BlendshapeWeightsMergerWindow>("BlendShape 合成导出");

    private void OnGUI()
    {
        DrawSourcesTwoColumns();
        DrawMergeSettings();
        DrawActions();
        if (channelA == SourceChannel.剪贴板 || channelB == SourceChannel.剪贴板)
            EditorGUILayout.HelpBox("剪贴板仅保存权重顺序，不包含 BlendShape 名称。请使用同一模型、同一顺序的数据。", MessageType.Warning);

        if (!string.IsNullOrEmpty(lastMsg))
            EditorGUILayout.HelpBox(lastMsg, MessageType.None);

        GUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "从其他工程复制权重：复制 GenericPropertyJSON 文本 → 渠道切到“剪贴板” → 点击“从剪贴板读取”。\n" +
            "极端比例（9:1/99:1）用预设按钮或在滑条右侧输入 0.9/0.99。",
            MessageType.Info);
    }

    // =================== 来源（并排） =================== //
    private void DrawSourcesTwoColumns()
    {
        GUILayout.Label("来源（SkinnedMeshRenderer / 剪贴板）", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawSourceCard(
                "来源 A", "A",
                ref channelA, ref sourceA,
                ref clipboardTextA, ref clipWeightsA, ref clipInfoA,
                ref foldA, ref scrollA,
                readButtonLabel: "从剪贴板读取 → A",
                clearButtonLabel: "清空 A"
            );

            DrawSourceCard(
                "来源 B", "B",
                ref channelB, ref sourceB,
                ref clipboardTextB, ref clipWeightsB, ref clipInfoB,
                ref foldB, ref scrollB,
                readButtonLabel: "从剪贴板读取 → B",
                clearButtonLabel: "清空 B"
            );
        }
    }

    private void DrawSourceCard(
        string title, string tag,
        ref SourceChannel channel, ref SkinnedMeshRenderer smr,
        ref string clipText, ref float[] clipWeights, ref string clipInfo,
        ref bool fold, ref Vector2 scroll,
        string readButtonLabel, string clearButtonLabel)
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            GUILayout.Label(title, EditorStyles.miniBoldLabel);
            channel = (SourceChannel)EditorGUILayout.EnumPopup(new GUIContent("数据渠道", "选择该来源来自组件还是剪贴板"), channel);

            if (channel == SourceChannel.组件)
            {
                smr = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                    new GUIContent($"{tag} 组件", "从该 SkinnedMeshRenderer 读取当前权重"),
                    smr, typeof(SkinnedMeshRenderer), true);

                if (GUILayout.Button(new GUIContent($"用当前选中对象填入 {tag}", "若选中物体带有 SkinnedMeshRenderer，则自动填入")))
                {
                    var sel = Selection.activeGameObject;
                    var t = sel ? sel.GetComponent<SkinnedMeshRenderer>() : null;
                    if (t) smr = t; else EditorUtility.DisplayDialog("提示", "请选择带 SkinnedMeshRenderer 的对象。", "好");
                }
                DrawBadge("当前使用：组件", new Color(0.27f, 0.52f, 0.27f));
            }
            else
            {
                EditorGUILayout.LabelField(new GUIContent($"{tag} 剪贴板（GenericPropertyJSON 文本）", "点击下方按钮从系统剪贴板读取"));
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(readButtonLabel))
                    {
                        clipText = EditorGUIUtility.systemCopyBuffer;
                        ParseClipboardToWeightsFast(clipText, out clipWeights, out clipInfo);
                    }
                    if (GUILayout.Button(clearButtonLabel))
                    {
                        clipText = ""; clipWeights = null; clipInfo = "(未读取)";
                    }
                }

                // 状态 + 折叠式预览（固定高度滚动）
                EditorGUILayout.LabelField($"{tag} 解析", clipInfo);
                fold = EditorGUILayout.Foldout(fold, $"{tag} 预览（点击展开/折叠）", true);
                if (fold && !string.IsNullOrEmpty(clipText))
                {
                    string head = PreviewHead(clipText, PreviewChars);
                    scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(PreviewFixedHeight));
                    EditorGUILayout.SelectableLabel(head, PreviewStyle, GUILayout.ExpandHeight(true));
                    EditorGUILayout.EndScrollView();
                }
                DrawBadge("当前使用：剪贴板", new Color(0.20f, 0.45f, 0.70f));
            }
        }
    }

    private void DrawBadge(string text, Color c)
    {
        var prev = GUI.color; GUI.color = c;
        GUILayout.Box(text, GUILayout.ExpandWidth(false));
        GUI.color = prev;
    }

    // =================== 合成设置 =================== //
    private void DrawMergeSettings()
    {
        GUILayout.Space(6);
        GUILayout.Label("合成设置", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            mode = (Mode)EditorGUILayout.EnumPopup(new GUIContent("合成模式", "平均：0.5/0.5；加权：使用下方滑条"), mode);

            if (mode == Mode.加权)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    if (GUILayout.Button("90:10", EditorStyles.toolbarButton)) aRatio01 = 0.90f;
                    if (GUILayout.Button("95:5",  EditorStyles.toolbarButton)) aRatio01 = 0.95f;
                    if (GUILayout.Button("99:1",  EditorStyles.toolbarButton)) aRatio01 = 0.99f;
                    if (GUILayout.Button("100:0", EditorStyles.toolbarButton)) aRatio01 = 1.00f;
                    if (GUILayout.Button("0:100", EditorStyles.toolbarButton))  aRatio01 = 0.00f;
                    GUILayout.FlexibleSpace();
                    GUILayout.Label("预设", EditorStyles.miniLabel);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent("A 比例", "B = 1 - A；0.9 表示 9:1，0.99 表示 99:1"), GUILayout.Width(90));
                    // 仅滑条 + 一个数值框
                    aRatio01 = GUILayout.HorizontalSlider(aRatio01, 0f, 1f, GUILayout.ExpandWidth(true));
                    aRatio01 = Mathf.Clamp01(EditorGUILayout.FloatField(aRatio01, GUILayout.Width(60)));
                }
            }

            strictNameMatch = EditorGUILayout.ToggleLeft(
                new GUIContent("严格检查 BlendShape 名称与顺序（推荐）", "检查组件来源和目标的名称与顺序；剪贴板没有名称信息，需使用同一模型的权重"),
                strictNameMatch);

            // —— 实时 A/B 权重（替代旧“当前比例”） —— //
            ComputeWeights(out var wa, out var wb);
            EditorGUILayout.LabelField(
                new GUIContent("A/B 权重", "平均就是1：1"),
                $"{Format01(wa)} / {Format01(wb)}（≈ {ApproxRatio(wa, wb)}）"
            );
        }
    }

    // =================== 操作（复制 / 导出 / 应用） =================== //
    private void DrawActions()
    {
        GUILayout.Space(6);
        GUILayout.Label("导出 / 复制 / 应用", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            if (GUILayout.Button(new GUIContent("合成并复制到剪贴板（GenericPropertyJSON）", "生成与示例完全一致的字符串并复制到系统剪贴板")))
                TryCopyToClipboard();

            if (GUILayout.Button(new GUIContent("合成并导出为.txt", "保存到磁盘；带前缀、字段顺序、size+data 紧凑无空格")))
                TryExport();

            applyTarget = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                new GUIContent("把合成结果应用到此目标", "仅写入目标 SMR 的权重，便于在场景中直接预览"),
                applyTarget, typeof(SkinnedMeshRenderer), true);

            if (GUILayout.Button(new GUIContent("只应用到目标（不导出）", "不会写文件，只改目标 SMR 的权重")))
                TryApplyToTarget();
        }
    }

    // =================== 复制 / 导出 / 应用 =================== //
    private void TryCopyToClipboard()
    {
        lastMsg = "";
        if (!ValidateSources(out string err)) { lastMsg = err; ShowNotification(new GUIContent("复制失败")); return; }
        if (!Merge(out var merged, out string mergeInfo)) { lastMsg = mergeInfo; ShowNotification(new GUIContent("复制失败")); return; }

        string txt = BuildGenericPropertyJson(merged);
        EditorGUIUtility.systemCopyBuffer = txt;
        lastMsg = "合成内容已复制到剪贴板！\n" + mergeInfo;
        ShowNotification(new GUIContent("已复制到剪贴板"));
    }

    private void TryExport()
    {
        lastMsg = "";
        if (!ValidateSources(out string err)) { lastMsg = err; ShowNotification(new GUIContent("合成失败")); return; }
        if (!Merge(out var merged, out string mergeInfo)) { lastMsg = mergeInfo; ShowNotification(new GUIContent("合成失败")); return; }

        var path = EditorUtility.SaveFilePanel("保存为 GenericPropertyJSON 文件", Application.dataPath, "blended_m_BlendShapeWeights", "txt");
        if (string.IsNullOrEmpty(path)) return;

        var txt = BuildGenericPropertyJson(merged);
        try
        {
            File.WriteAllText(path, txt, new UTF8Encoding(false));
            AssetDatabase.Refresh();
        }
        catch (Exception ex)
        {
            lastMsg = "导出失败：" + ex.Message;
            ShowNotification(new GUIContent("导出失败"));
            return;
        }

        lastMsg = $"导出成功：{path}\n{mergeInfo}";
        ShowNotification(new GUIContent("导出成功"));
    }

    private void TryApplyToTarget()
    {
        lastMsg = "";
        if (applyTarget == null) { lastMsg = "请先指定“目标” SkinnedMeshRenderer。"; return; }
        if (!ValidateSources(out string err)) { lastMsg = err; ShowNotification(new GUIContent("合成失败")); return; }
        if (!Merge(out var merged, out string mergeInfo)) { lastMsg = mergeInfo; ShowNotification(new GUIContent("合成失败")); return; }

        if (applyTarget.sharedMesh == null) { lastMsg = "目标 SMR 没有 Mesh。"; return; }
        if (applyTarget.sharedMesh.blendShapeCount != merged.Length)
        { lastMsg = $"目标 BlendShape 数量 {applyTarget.sharedMesh.blendShapeCount} 与合成结果 {merged.Length} 不一致。"; return; }

        if (EditorUtility.IsPersistent(applyTarget) || !applyTarget.gameObject.scene.IsValid())
        { lastMsg = "请把模型放入场景后再应用权重。"; return; }
        if (strictNameMatch)
        {
            SkinnedMeshRenderer[] sources = {
                channelA == SourceChannel.组件 ? sourceA : null,
                channelB == SourceChannel.组件 ? sourceB : null
            };
            foreach (var source in sources)
            {
                if (source == null) continue;
                for (int i = 0; i < merged.Length; i++)
                    if (!string.Equals(source.sharedMesh.GetBlendShapeName(i), applyTarget.sharedMesh.GetBlendShapeName(i), StringComparison.Ordinal))
                    { lastMsg = $"目标名称不匹配：索引 {i}，来源 '{source.sharedMesh.GetBlendShapeName(i)}'，目标 '{applyTarget.sharedMesh.GetBlendShapeName(i)}'。"; return; }
            }
        }
        Undo.RecordObject(applyTarget, "Apply Merged BlendShape Weights");
        for (int i = 0; i < merged.Length; i++) applyTarget.SetBlendShapeWeight(i, merged[i]);
        lastMsg = $"已将合成结果应用到目标：{applyTarget.name}\n{mergeInfo}";
        ShowNotification(new GUIContent("已应用到目标"));
        PrefabUtility.RecordPrefabInstancePropertyModifications(applyTarget);
        EditorSceneManager.MarkSceneDirty(applyTarget.gameObject.scene);
        EditorUtility.SetDirty(applyTarget);
    }

    // =================== 校验 / 合成 =================== //
    private bool ValidateSources(out string err)
    {
        if (channelA == SourceChannel.组件)
        {
            if (sourceA == null || sourceA.sharedMesh == null) { err = "来源 A：未指定组件或缺少 Mesh。"; return false; }
        }
        else
        {
            if (clipWeightsA == null || clipWeightsA.Length == 0) { err = "来源 A：剪贴板尚未读取或解析失败。"; return false; }
        }

        if (channelB == SourceChannel.组件)
        {
            if (sourceB == null || sourceB.sharedMesh == null) { err = "来源 B：未指定组件或缺少 Mesh。"; return false; }
        }
        else
        {
            if (clipWeightsB == null || clipWeightsB.Length == 0) { err = "来源 B：剪贴板尚未读取或解析失败。"; return false; }
        }

        err = null; return true;
    }

    private bool Merge(out float[] merged, out string info)
    {
        merged = null;

        // A
        float[] a; string[] namesA = null; int nA;
        if (channelA == SourceChannel.组件)
        {
            var mA = sourceA.sharedMesh; nA = mA.blendShapeCount;
            a = new float[nA]; for (int i = 0; i < nA; i++) a[i] = sourceA.GetBlendShapeWeight(i);
            namesA = Enumerable.Range(0, nA).Select(i => mA.GetBlendShapeName(i)).ToArray();
        }
        else { a = clipWeightsA; nA = a.Length; }

        // B
        float[] b; string[] namesB = null; int nB;
        if (channelB == SourceChannel.组件)
        {
            var mB = sourceB.sharedMesh; nB = mB.blendShapeCount;
            b = new float[nB]; for (int i = 0; i < nB; i++) b[i] = sourceB.GetBlendShapeWeight(i);
            namesB = Enumerable.Range(0, nB).Select(i => mB.GetBlendShapeName(i)).ToArray();
        }
        else { b = clipWeightsB; nB = b.Length; }

        if (nA != nB) { info = $"两侧条目数不一致：A={nA}, B={nB}。"; return false; }

        string nameCheckNote = "";
        if (strictNameMatch)
        {
            if (namesA != null && namesB != null)
            {
                for (int i = 0; i < nA; i++)
                {
                    if (!string.Equals(namesA[i], namesB[i], StringComparison.Ordinal))
                    { info = $"名称不匹配：索引 {i}，A='{namesA[i]}'，B='{namesB[i]}'。"; return false; }
                }
            }
            else nameCheckNote = "（含剪贴板来源，跳过名称校验）";
        }

        if (nA == 0) { info = "来源没有 BlendShape。"; return false; }
        if (float.IsNaN(aRatio01) || float.IsInfinity(aRatio01)) { info = "比例必须是有限数值。"; return false; }
        merged = new float[nA];
        ComputeWeights(out var wa, out var wb);
        for (int i = 0; i < nA; i++)
        {
            if (float.IsNaN(a[i]) || float.IsInfinity(a[i]) || float.IsNaN(b[i]) || float.IsInfinity(b[i]))
            { info = $"索引 {i} 的来源权重不是有限数值。"; merged = null; return false; }
            merged[i] = wa * a[i] + wb * b[i];
            if (float.IsNaN(merged[i]) || float.IsInfinity(merged[i]))
            { info = $"索引 {i} 的合成结果超出数值范围。"; merged = null; return false; }
        }

        int nonzero = merged.Count(x => Mathf.Abs(x) > 1e-6f);
        float minV = merged.Length > 0 ? merged.Min() : 0f;
        float maxV = merged.Length > 0 ? merged.Max() : 0f;
        info = $"条目数：{nA}；非零数：{nonzero}；范围：{minV:0.##}～{maxV:0.##}{nameCheckNote}";
        return true;
    }

    private void ComputeWeights(out float wa, out float wb)
    {
        if (mode == Mode.平均) { wa = 0.5f; wb = 0.5f; }
        else { wa = Mathf.Clamp01(aRatio01); wb = 1f - wa; }
    }

    // =================== JSON 生成（紧凑） =================== //
    private string BuildGenericPropertyJson(float[] values)
    {
        string F(float v) => v.ToString("0.##########", CultureInfo.InvariantCulture);
        int n = values.Length;
        var sb = new StringBuilder(64 + n * 24);
        sb.Append("GenericPropertyJSON:{");
        sb.Append("\"name\":\"m_BlendShapeWeights\"");
        sb.Append(",\"type\":-1");
        sb.Append(",\"arraySize\":").Append(n);
        sb.Append(",\"arrayType\":\"float\"");
        sb.Append(",\"children\":[{");
        sb.Append("\"name\":\"Array\"");
        sb.Append(",\"type\":-1");
        sb.Append(",\"arraySize\":").Append(n);
        sb.Append(",\"arrayType\":\"float\"");
        sb.Append(",\"children\":[");
        sb.Append("{\"name\":\"size\",\"type\":12,\"val\":").Append(n).Append("}");
        for (int i = 0; i < n; i++)
            sb.Append(",{\"name\":\"data\",\"type\":2,\"val\":").Append(F(values[i])).Append("}");
        sb.Append("]}]}");
        return sb.ToString();
    }

    [Serializable]
    private class ClipboardNode
    {
        // JsonUtility populates these fields from the property clipboard.
#pragma warning disable CS0649
        public string name;
        public int type;
        public int arraySize;
        public string arrayType;
        public double val;
        public ClipboardNode[] children;
#pragma warning restore CS0649
    }

    // Validate the complete property schema and declared count; never silently truncate.
    private bool ParseClipboardToWeightsFast(string text, out float[] weights, out string info)
    {
        weights = null;
        info = "未解析";
        if (string.IsNullOrWhiteSpace(text)) { info = "剪贴板为空"; return false; }
        if (text.Length > 2 * 1024 * 1024) { info = "剪贴板内容过大"; return false; }
        try
        {
            string raw = text.Trim();
            const string prefix = "GenericPropertyJSON:";
            if (raw.StartsWith(prefix, StringComparison.Ordinal)) raw = raw.Substring(prefix.Length).Trim();
            ClipboardNode property = JsonUtility.FromJson<ClipboardNode>(raw);
            if (property == null || property.name != "m_BlendShapeWeights" || property.type != -1 || property.arrayType != "float")
                throw new FormatException("内容不是 BlendShape 权重属性。");
            if (property.children == null || property.children.Length != 1 || property.children[0] == null)
                throw new FormatException("Array 节点无效。");
            ClipboardNode array = property.children[0];
            int count = property.arraySize;
            if (array.name != "Array" || array.type != -1 || array.arrayType != "float" || count < 1 || count > 10000 || array.arraySize != count ||
                array.children == null || array.children.Length != count + 1)
                throw new FormatException("声明的条目数与数组内容不一致，或数量超出范围。");
            ClipboardNode size = array.children[0];
            if (size == null || size.name != "size" || size.type != 12 || size.val != count)
                throw new FormatException("size 节点无效或数量不一致。");
            if (Regex.Matches(raw, "\"val\"\\s*:").Count != count + 1)
                throw new FormatException("权重值字段缺失或重复。");
            float[] parsed = new float[count];
            for (int i = 0; i < count; i++)
            {
                ClipboardNode item = array.children[i + 1];
                if (item == null || item.name != "data" || item.type != 2 || double.IsNaN(item.val) || double.IsInfinity(item.val) ||
                    item.val > float.MaxValue || item.val < -float.MaxValue)
                    throw new FormatException($"索引 {i} 的权重节点无效。");
                parsed[i] = (float)item.val;
            }
            weights = parsed;
            info = $"解析成功：{count} 项；范围 {parsed.Min():0.##}～{parsed.Max():0.##}";
            return true;
        }
        catch (Exception ex) { info = "解析失败：" + ex.Message; return false; }
    }

    private static string PreviewHead(string s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s.Substring(0, max) + " …";
    }
    private static string Format01(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ApproxRatio(float a, float b)
    {
        if (a <= 1e-6f && b <= 1e-6f) return "0:0";
        if (a <= 1e-6f) return "0:100";
        if (b <= 1e-6f) return "100:0";

        int ia = Mathf.RoundToInt(a * 100f);
        int ib = Mathf.RoundToInt(b * 100f);
        int g = GCD(Mathf.Max(1, ia), Mathf.Max(1, ib));
        ia /= g; ib /= g;
        return $"{ia}:{ib}";
    }
    private static int GCD(int x, int y)
    {
        while (y != 0) { int t = x % y; x = y; y = t; }
        return Math.Max(1, x);
    }
}
