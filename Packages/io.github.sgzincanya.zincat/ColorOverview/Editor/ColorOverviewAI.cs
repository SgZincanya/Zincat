#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Zincat.ColorOverview
{
    public sealed partial class ColorOverviewWindow
    {
        [Serializable] private sealed class MatSwap { public string From, To; }
        [Serializable] private sealed class AiPrefab { public string Id = "", Guid = "", Geometry = ""; public List<string> Materials = new List<string>(); }
        [Serializable] private sealed class AiMaterial { public string Id = "", Guid = ""; }
        [Serializable] private sealed class AiSnapshot
        {
            public string Session, Prompt, Accepted;
            public List<string> Roots = new List<string>();
            public bool HasSavedSets;
            public List<StoredSet> SavedSets = new List<StoredSet>();
            public List<AiPrefab> Prefabs = new List<AiPrefab>();
            public List<AiMaterial> Materials = new List<AiMaterial>();
        }
        [Serializable] private sealed class ImportHistory { public List<string> recentGuids = new List<string>(); }
        private const string AiCache = "Library/Zincat/ColorOverviewAI.json";
        private AiSnapshot aiSnapshot;
        private List<Set> aiPreview;
        private string aiFilter = "", aiStatus;
        private string aiPendingText;
        private string aiPasteText = "";
        private bool aiShowText, aiError;

        private void LoadAiSession()
        {
            try
            {
                if (!File.Exists(AiCache)) return;
                aiSnapshot = JsonUtility.FromJson<AiSnapshot>(File.ReadAllText(AiCache));
                if (aiSnapshot != null)
                {
                    aiStatus = null;
                    if (aiSnapshot.HasSavedSets)
                    {
                        RestoreList(aiSnapshot.SavedSets);
                    }
                    else if (!string.IsNullOrEmpty(aiSnapshot.Accepted))
                    {
                        var restored = ResolveAiResult(aiSnapshot.Accepted);
                        sets.Clear(); sets.AddRange(restored);
                        SaveCurrentList();
                        status = "当前 " + sets.Count + " 组，共 " + sets.Sum(s => s.Variants.Count) + " 个展示项。";
                        aiStatus = null;
                    }
                }
            }
            catch (Exception e) { aiError = true; aiStatus = "上次索引无法恢复：" + e.Message; }
        }
        private void SaveAiSession()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AiCache));
            File.WriteAllText(AiCache, JsonUtility.ToJson(aiSnapshot), new UTF8Encoding(false));
        }
        private void AiAction(Action action)
        {
            try { aiError = false; action(); }
            catch (Exception e) { aiError = true; aiStatus = e.Message; Debug.LogWarning("配色识别：" + e.Message); }
            finally { EditorUtility.ClearProgressBar(); }
        }
        private static string[] SelectedScopes()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (!AssetDatabase.IsValidFolder(path) && !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && obj is GameObject go && !EditorUtility.IsPersistent(go))
                {
                    var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
                    if (!source || !AssetDatabase.GetAssetPath(source).EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) { var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go); if (instanceRoot) source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(instanceRoot); }
                    path = AssetDatabase.GetAssetPath(source);
                }
                if (AssetDatabase.IsValidFolder(path) || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
            }
            if (paths.Count == 0) throw new InvalidOperationException("请先在 Project 选择资源文件夹或预制体，或在场景里选择有源预制体的衣服。");
            return paths.ToArray();
        }
        private static string[] RecentScopes()
        {
            const string file = "UserSettings/ZincatImportHistory.json";
            if (!File.Exists(file)) throw new InvalidOperationException("当前工程没有 Zincat 导入记录，请使用选中资源方式。");
            var history = JsonUtility.FromJson<ImportHistory>(File.ReadAllText(file));
            var guids = history.recentGuids ?? new List<string>();
            var paths = guids.Select(AssetDatabase.GUIDToAssetPath).Where(AssetDatabase.IsValidFolder).Distinct().ToArray();
            if (paths.Length == 0) throw new InvalidOperationException("最近导入目录不存在，请使用选中资源方式。");
            return paths;
        }
        private static string AssetPath(string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("索引资源已删除，请重新复制提示词。");
            return path;
        }
        private static Mesh SourceMesh(Renderer renderer)
        {
            if (!renderer) return null;
            if (renderer is SkinnedMeshRenderer skin) return skin.sharedMesh;
            if (renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter) && filter)
                return filter.sharedMesh;
            // Particle, trail and line renderers are not standalone mesh assets.
            return null;
        }
        private void BuildAiPrompt(string[] scopes)
        {
            var roots = scopes.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !scopes.Any(parent => !string.Equals(parent, p, StringComparison.OrdinalIgnoreCase) &&
                    AssetDatabase.IsValidFolder(parent) && p.StartsWith(parent.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (roots.Count == 0) throw new InvalidOperationException("没有有效查找范围。");
            var snapshot = new AiSnapshot { Session = Guid.NewGuid().ToString("N").Substring(0, 10), Roots = roots };
            var prompt = new StringBuilder();
            prompt.AppendLine("只读查本机Unity工程：" + Path.GetFullPath(".").Replace('\\', '/'));
            prompt.AppendLine("只读以下范围的衣服/头发全部配色，排除本体/工具/特效。一次批量列prefab路径，按名称分组；每套仅一种角色适配，优先Shinano。名称足够则不读内容；疑义或缺独立配色prefab才抽查代表prefab及对应材质/meta，保留材质配色。禁止逐文件读取、全量材质/依赖扫描及shader扩散；不联网、不改文件。仅输出配置，疑义用#短注。");
            if (!string.IsNullOrWhiteSpace(aiFilter)) prompt.AppendLine("预制体路径筛选：" + aiFilter.Trim());
            prompt.AppendLine("范围：");
            foreach (string root in roots) prompt.AppendLine(root);
            prompt.AppendLine("仅返回代码块，首行 ZCO2 " + snapshot.Session + "；以下列用真实TAB：");
            prompt.AppendLine("G<TAB>套装名<TAB>Assets/公共目录");
            prompt.AppendLine("P<TAB>配色名<TAB>相对.prefab");
            prompt.AppendLine("V<TAB>配色名<TAB>基础.prefab<TAB>原.mat<TAB>新.mat（可追加原/新对）");
            prompt.AppendLine("P/V归上一G，路径相对G或以Assets/开头，禁用..；有现成prefab用P，否则V。");
            snapshot.Prompt = prompt.ToString(); snapshot.HasSavedSets = true; snapshot.SavedSets = StoreList();
            aiSnapshot = snapshot; aiPreview = null; SaveAiSession();
            EditorGUIUtility.systemCopyBuffer = snapshot.Prompt;
            aiStatus = "已复制 · " + roots.Count + " 个限定范围 · " + snapshot.Prompt.Length.ToString("N0") + " 字符";
        }
        private List<Set> ResolvePathResult(string text)
        {
            var groups = ColorOverviewPathProtocol.Read(text, aiSnapshot.Session, aiSnapshot.Roots);
            var result = new List<Set>();
            foreach (var group in groups)
            {
                var set = new Set { Name = group.Name };
                foreach (var entry in group.Entries)
                {
                    if (!string.IsNullOrWhiteSpace(aiFilter) && entry.Prefab.IndexOf(aiFilter.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidOperationException("预制体不符合路径筛选：" + entry.Prefab);
                    var root = AssetDatabase.LoadAssetAtPath<GameObject>(entry.Prefab);
                    if (!root) throw new InvalidOperationException("预制体不存在：" + entry.Prefab);
                    var variant = new Variant { Name = entry.Name, Path = entry.Prefab, PrefabGuid = AssetDatabase.AssetPathToGUID(entry.Prefab) };
                    var used = new HashSet<Material>(root.GetComponentsInChildren<Renderer>(true).Where(r => SourceMesh(r)).SelectMany(r => r.sharedMaterials).Where(m => m));
                    foreach (var swap in entry.Replacements)
                    {
                        var from = AssetDatabase.LoadAssetAtPath<Material>(swap.Key);
                        var to = AssetDatabase.LoadAssetAtPath<Material>(swap.Value);
                        if (!from || !to || !used.Contains(from)) throw new InvalidOperationException("材质不存在或基础预制体未使用原材质：" + swap.Key);
                        variant.Swaps.Add(new MatSwap { From = AssetDatabase.AssetPathToGUID(swap.Key), To = AssetDatabase.AssetPathToGUID(swap.Value) });
                    }
                    set.Variants.Add(variant);
                }
                result.Add(set);
            }
            aiStatus = "识别 " + result.Count + " 组 · " + result.Sum(g => g.Variants.Count) + " 项";
            return result;
        }
        private List<Set> ResolveAiResult(string text)
        {
            if (aiSnapshot == null || string.IsNullOrEmpty(aiSnapshot.Session)) throw new InvalidOperationException("请先复制资源提示词。");
            if (aiSnapshot.Roots != null && aiSnapshot.Roots.Count > 0) return ResolvePathResult(text);
            var prefabRows = aiSnapshot.Prefabs.ToDictionary(p => p.Id);
            var matRows = aiSnapshot.Materials.ToDictionary(m => m.Id);
            var groups = ColorOverviewProtocol.Read(text, aiSnapshot.Session, new HashSet<string>(prefabRows.Keys), new HashSet<string>(matRows.Keys));
            var result = new List<Set>();
            var covered = new HashSet<string>();
            foreach (var group in groups)
            {
                var set = new Set { Name = group.Name };
                foreach (var entry in group.Entries)
                {
                    var p = prefabRows[entry.Prefab]; string path = AssetPath(p.Guid);
                    if (!AssetDatabase.LoadAssetAtPath<GameObject>(path)) throw new InvalidOperationException("预制体已失效：" + path);
                    var variant = new Variant { Name = entry.Name ?? Path.GetFileNameWithoutExtension(path), Path = path, PrefabGuid = p.Guid };
                    foreach (var swap in entry.Replacements)
                    {
                        string from = matRows[swap.Key].Guid, to = matRows[swap.Value].Guid;
                        if (!p.Materials.Contains(from)) throw new InvalidOperationException(entry.Prefab + " 未使用源材质 " + swap.Key + "，请让 AI 修正。");
                        if (!AssetDatabase.LoadAssetAtPath<Material>(AssetPath(from)) || !AssetDatabase.LoadAssetAtPath<Material>(AssetPath(to))) throw new InvalidOperationException("材质已失效，请重新扫描。");
                        variant.Swaps.Add(new MatSwap { From = from, To = to });
                    }
                    set.Variants.Add(variant); covered.Add(entry.Prefab);
                }
                result.Add(set);
            }
            aiStatus = "识别 " + result.Count + " 组；有 " + (prefabRows.Count - covered.Count) + " 个候选未归组（可能是重复适配/工具或不确定项）。";
            return result;
        }
    }
}
#endif



