#if ZINCAT_STARMESH_PACKAGE && ZINCAT_HAIMESHLIB_PACKAGE
// Zincat Starmesh Mesh Matcher 0.2.8. Requires Starmesh 1.7.0.
// Uses the installed Starmesh API; no third-party source is included.
#pragma warning disable CS0612
using System;
using System.Collections.Generic;
using System.Linq;
using Hai.Starmesh.Editor;
using Hai.Starmesh.Runtime;
using HaiMeshLib.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zincat.StarmeshTools
{
    public enum MeshRule { Auto, Include, Exclude }

    [Serializable]
    public sealed class MatcherOptions
    {
        public SkinnedMeshRenderer body;
        public bool useBox;
        public Transform box;
        public int minimumVertices = 3;
        public bool useWhitelist;
        public List<Transform> whitelist = new List<Transform>();
        public List<Transform> blacklist = new List<Transform>();

        public bool IsBlacklisted(Transform item) => blacklist.Any(t => t && item.IsChildOf(t));
        public bool IsWhitelisted(Transform item) => !useWhitelist || whitelist.Any(t => t && item.IsChildOf(t));

        public void Validate(Transform root)
        {
            if (body && !body.transform.IsChildOf(root))
                throw new InvalidOperationException("Body 必须是当前 Avatar 内的 Skinned Mesh Renderer");
            if (body && IsBlacklisted(body.transform))
                throw new InvalidOperationException("Body 同时位于黑名单中。请先将身体从黑名单移除，避免身体漏选。");
            if (useBox && (!box || !box.IsChildOf(root)))
                throw new InvalidOperationException("启用匹配框后，请指定 Avatar 内的匹配框对象");
            if (useBox && (Mathf.Abs(box.lossyScale.x) < 0.000001f ||
                Mathf.Abs(box.lossyScale.y) < 0.000001f || Mathf.Abs(box.lossyScale.z) < 0.000001f))
                throw new InvalidOperationException("匹配框不能缩放为零");
            if (whitelist.Concat(blacklist).Any(t => t && !t.IsChildOf(root)))
                throw new InvalidOperationException("白名单和黑名单中的对象必须位于当前 Avatar 内");
        }
    }

    [Serializable]
    public sealed class SavedMeshRule
    {
        public string rendererId;
        public MeshRule mode;
    }

    [Serializable]
    public sealed class SavedTargetRules
    {
        public string bodyId;
        public string selectorId;
        public List<SavedMeshRule> rules = new List<SavedMeshRule>();
        public bool useBox;
        public string boxId;
        public int minimumVertices = 3;
        public bool useWhitelist;
        public List<string> whitelist = new List<string>();
        public List<string> blacklist = new List<string>();
    }

    [Serializable]
    public sealed class MatcherSettingsFileData
    {
        public int formatVersion = 1;
        public List<SavedTargetRules> targets = new List<SavedTargetRules>();
    }

    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Zincat.StarmeshTools", "Assembly-CSharp-Editor", "MatcherSettings")]
    public sealed class MatcherSettings : ScriptableSingleton<MatcherSettings>
    {
        private static bool loaded;
        private const string JsonPath = "ProjectSettings/ZincatStarmeshMatcher.json";
        private const string LegacyPath = "ProjectSettings/ZincatStarmeshMatcher.asset";

        public static MatcherSettings Current
        {
            get
            {
                var settings = instance;
                if (!loaded)
                {
                    if (System.IO.File.Exists(JsonPath))
                    {
                        var file = JsonUtility.FromJson<MatcherSettingsFileData>(System.IO.File.ReadAllText(JsonPath));
                        if (file == null || file.formatVersion != 1 || file.targets == null)
                            throw new InvalidOperationException("Starmesh 辅助配置无法读取，原配置已保留，请检查配置文件。");
                        settings.targets = file.targets;
                    }
                    else if (System.IO.File.Exists(LegacyPath))
                    {
                        settings.targets = ParseLegacy(System.IO.File.ReadAllText(LegacyPath));
                        settings.SaveConfig();
                    }
                    loaded = true;
                }
                return settings;
            }
        }

        [SerializeField] private List<SavedTargetRules> targets = new List<SavedTargetRules>();

        // Read only the known scalar/list schema written by 0.1.x-0.2.7.
        // The original asset stays untouched; the new format has no assembly identity.
        private static List<SavedTargetRules> ParseLegacy(string yaml)
        {
            var entries = new List<SavedTargetRules>();
            SavedTargetRules target = null;
            SavedMeshRule rule = null;
            string list = null;
            bool started = false;
            foreach (var raw in yaml.Replace("\r", "").Split('\n'))
            {
                if (raw.Trim() == "targets:" || raw.Trim() == "targets: []") { started = true; continue; }
                if (!started || string.IsNullOrWhiteSpace(raw)) continue;
                string line;
                if (raw.StartsWith("  - "))
                {
                    entries.Add(target = new SavedTargetRules()); rule = null; list = null;
                    line = raw.Substring(4);
                }
                else if (raw.StartsWith("    - "))
                {
                    line = raw.Substring(6);
                    if (list == "whitelist" || list == "blacklist")
                    {
                        (list == "whitelist" ? target.whitelist : target.blacklist).Add(line.Trim().Trim('"', '\''));
                        continue;
                    }
                    if (list != "rules") throw new InvalidOperationException("旧配置列表格式无法识别，未覆盖原文件。");
                    target.rules.Add(rule = new SavedMeshRule());
                }
                else line = raw.Trim();
                if (target == null) throw new InvalidOperationException("旧配置缺少目标分组，未覆盖原文件。");
                int split = line.IndexOf(':');
                if (split < 0) continue;
                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim().Trim('"', '\'');
                switch (key)
                {
                    case "bodyId": target.bodyId = value; break;
                    case "selectorId": target.selectorId = value; break;
                    case "boxId": target.boxId = value; break;
                    case "useBox": target.useBox = value == "1" || value == "true"; break;
                    case "useWhitelist": target.useWhitelist = value == "1" || value == "true"; break;
                    case "minimumVertices": target.minimumVertices = int.Parse(value); break;
                    case "rules": case "whitelist": case "blacklist": list = key; rule = null; break;
                    case "rendererId": if (rule == null) throw new InvalidOperationException("旧配置规则格式错误"); rule.rendererId = value; break;
                    case "mode": if (rule == null) throw new InvalidOperationException("旧配置规则格式错误"); rule.mode = (MeshRule)int.Parse(value); break;
                    default: throw new InvalidOperationException("旧配置包含无法识别的字段：" + key + "，未覆盖原文件。");
                }
            }
            if (!started || entries.Any(t => string.IsNullOrEmpty(t.selectorId)))
                throw new InvalidOperationException("旧配置缺少有效目标信息，未覆盖原文件。");
            return entries;
        }

        private void SaveConfig()
        {
            System.IO.File.WriteAllText(JsonPath, JsonUtility.ToJson(new MatcherSettingsFileData { targets = targets }, true));
        }

        public Dictionary<SkinnedMeshRenderer, MeshRule> Load(StarmeshSelectMeshes target)
        {
            var result = new Dictionary<SkinnedMeshRenderer, MeshRule>();
            if (!target || string.IsNullOrEmpty(target.gameObject.scene.path)) return result;
            var entry = targets.Find(t => t.selectorId == GlobalObjectId.GetGlobalObjectIdSlow(target).ToString());
            if (entry == null) return result;
            foreach (var rule in entry.rules)
                if (GlobalObjectId.TryParse(rule.rendererId, out var id) &&
                    GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) is SkinnedMeshRenderer renderer)
                    result[renderer] = rule.mode;
            return result;
        }

        private static string Id(Object value) => value ? GlobalObjectId.GetGlobalObjectIdSlow(value).ToString() : "";
        private static Transform Resolve(string id) => GlobalObjectId.TryParse(id ?? "", out var parsed)
            ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) as Transform : null;
        private static SkinnedMeshRenderer ResolveBody(string id) => GlobalObjectId.TryParse(id ?? "", out var parsed)
            ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) as SkinnedMeshRenderer : null;

        public MatcherOptions LoadOptions(StarmeshSelectMeshes target)
        {
            var entry = target && !string.IsNullOrEmpty(target.gameObject.scene.path)
                ? targets.Find(t => t.selectorId == Id(target)) : null;
            if (entry == null) return new MatcherOptions();
            return new MatcherOptions {
                body = ResolveBody(entry.bodyId),
                useBox = entry.useBox, box = Resolve(entry.boxId),
                minimumVertices = Mathf.Max(1, entry.minimumVertices), useWhitelist = entry.useWhitelist,
                whitelist = (entry.whitelist ?? new List<string>()).Select(Resolve).ToList(),
                blacklist = (entry.blacklist ?? new List<string>()).Select(Resolve).ToList()
            };
        }

        public void Store(StarmeshSelectMeshes target, Dictionary<SkinnedMeshRenderer, MeshRule> rules,
            MatcherOptions options = null)
        {
            if (!target || string.IsNullOrEmpty(target.gameObject.scene.path)) return;
            string id = GlobalObjectId.GetGlobalObjectIdSlow(target).ToString();
            var entry = targets.Find(t => t.selectorId == id);
            if (entry == null) targets.Add(entry = new SavedTargetRules { selectorId = id });
            entry.rules = rules.Where(r => r.Key && r.Value != MeshRule.Auto)
                .Select(r => new SavedMeshRule {
                    rendererId = GlobalObjectId.GetGlobalObjectIdSlow(r.Key).ToString(), mode = r.Value
                }).ToList();
            if (options != null)
            {
                entry.bodyId = Id(options.body);
                entry.useBox = options.useBox; entry.boxId = Id(options.box);
                entry.minimumVertices = Mathf.Max(1, options.minimumVertices);
                entry.useWhitelist = options.useWhitelist;
                entry.whitelist = options.whitelist.Select(t => Id(t)).ToList();
                entry.blacklist = options.blacklist.Select(t => Id(t)).ToList();
            }
            SaveConfig();
        }

        public void Forget(StarmeshSelectMeshes target)
        {
            if (target) { targets.RemoveAll(t => t.selectorId == Id(target)); SaveConfig(); }
        }
    }

}
#pragma warning restore CS0612

#endif
