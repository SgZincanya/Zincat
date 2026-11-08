using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Zincat.ColorOverview
{
    // No Unity dependency: clipboard protocol can be verified independently.
    public static class ColorOverviewProtocol
    {
        public sealed class Group
        {
            public string Name;
            public readonly List<Entry> Entries = new List<Entry>();
        }
        public sealed class Entry
        {
            public string Name, Prefab;
            public readonly Dictionary<string, string> Replacements = new Dictionary<string, string>();
        }
        public static List<Group> Read(string text, string session, ISet<string> prefabs, ISet<string> materials)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("剪贴板为空。");
            var groups = new List<Group>();
            var byName = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool header = false;
            int lineNumber = 0;
            foreach (var raw in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n'))
            {
                lineNumber++;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (!header)
                {
                    if (line != "ZCO1 " + session) throw new FormatException("识别批次不匹配，请使用最近一次复制的提示词及其完整返回结果。");
                    header = true; continue;
                }
                string[] f = raw.TrimEnd('\r').Split('\t');
                if (f.Length < 3 || f[0] != "G" && f[0] != "V") throw new FormatException("第 " + lineNumber + " 行格式错误，应为 G 或 V 制表符行。");
                string name = f[1].Trim();
                if (name.Length == 0 || name.Length > 100 || name.IndexOfAny(new[] {'/', '\\', ':', '*', '?', '"', '<', '>', '|'}) >= 0 || name == "." || name == "..")
                    throw new FormatException("分组名称无效：" + name);
                if (!byName.TryGetValue(name, out var group))
                { group = new Group { Name = name }; byName.Add(name, group); groups.Add(group); }
                if (f[0] == "G")
                {
                    if (f.Length != 3) throw new FormatException("G 行应有三列。");
                    foreach (string id in f[2].Split(',').Select(s => s.Trim()))
                    {
                        if (!prefabs.Contains(id)) throw new FormatException("未知预制体编号：" + id);
                        if (!variants.Add(name + "|" + id)) throw new FormatException("同组重复预制体：" + id);
                        group.Entries.Add(new Entry { Prefab = id });
                    }
                }
                else
                {
                    if (f.Length != 5 || !prefabs.Contains(f[3].Trim()) || string.IsNullOrWhiteSpace(f[2])) throw new FormatException("V 行应有五列，且预制体编号和配色名称有效。");
                    string label = f[2].Trim();
                    if (!variants.Add(name + "|V|" + label)) throw new FormatException("同组重复配色名：" + label);
                    var entry = new Entry { Name = label, Prefab = f[3].Trim() };
                    foreach (string pair in f[4].Split(','))
                    {
                        var ids = pair.Split('=');
                        if (ids.Length != 2 || !materials.Contains(ids[0].Trim()) || !materials.Contains(ids[1].Trim())) throw new FormatException("未知材质编号或替换格式：" + pair);
                        string from = ids[0].Trim(), to = ids[1].Trim();
                        if (from == to || entry.Replacements.ContainsKey(from)) throw new FormatException("重复或无效材质替换：" + pair);
                        entry.Replacements.Add(from, to);
                    }
                    if (entry.Replacements.Count == 0) throw new FormatException("配色没有材质替换。");
                    group.Entries.Add(entry);
                }
            }
            if (!header || groups.Count == 0 || groups.Any(g => g.Entries.Count == 0)) throw new FormatException("未找到有效分组。");
            return groups;
        }
    }
}
