using System;
using System.Collections.Generic;
using System.Linq;

namespace Zincat.ColorOverview
{
    public static class ColorOverviewPathProtocol
    {
        public static string AssetPath(string folder, string path)
        {
            path = (path ?? "").Trim().Replace('\\', '/');
            string value = path.StartsWith("Assets/", StringComparison.Ordinal) ? path : folder.TrimEnd('/') + "/" + path;
            var parts = value.Split('/');
            if (!value.StartsWith("Assets/", StringComparison.Ordinal) || parts.Any(s => s.Length == 0 || s == "." || s == "..") || value.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|', '\t', '\r', '\n' }) >= 0)
                throw new FormatException("无效资源路径：" + path);
            return value;
        }
        public static bool InScope(string path, IEnumerable<string> scopes)
        {
            return scopes.Any(s => string.Equals(path, s, StringComparison.OrdinalIgnoreCase) ||
                !s.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && path.StartsWith(s.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        }
        public static List<ColorOverviewProtocol.Group> Read(string text, string session, IEnumerable<string> scopes)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("剪贴板为空。");
            var result = new List<ColorOverviewProtocol.Group>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ColorOverviewProtocol.Group group = null;
            string folder = null;
            bool header = false;
            foreach (var raw in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("```")) continue;
                if (!header)
                {
                    if (line != "ZCO2 " + session) throw new FormatException("批次不匹配，请使用最新提示词的返回结果。");
                    header = true; continue;
                }
                string[] f = raw.TrimEnd('\r').Split('\t');
                if (f.Length == 3 && f[0] == "G")
                {
                    string name = f[1].Trim();
                    if (name.Length == 0 || name.Length > 100 || name.IndexOfAny(new[] {'/', '\\', ':', '*', '?', '"', '<', '>', '|'}) >= 0 || name == "." || name == ".." || !names.Add(name)) throw new FormatException("无效或重复分组名：" + name);
                    folder = AssetPath("Assets", f[2]);
                    group = new ColorOverviewProtocol.Group { Name = name }; result.Add(group);
                }
                else
                {
                    bool swap = f.Length >= 5 && (f.Length - 3) % 2 == 0 && f[0] == "V";
                    if (group == null || !(f.Length == 3 && f[0] == "P" || swap)) throw new FormatException("应先写 G 分组行，再写 P 预制体或 V 材质配色行。");
                    string prefab = AssetPath(folder, f[2]);
                    if (!prefab.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || !InScope(prefab, scopes)) throw new FormatException("预制体不在本批查找范围内：" + prefab);
                    string label = f[1].Trim();
                    if (label.Length == 0 || !keys.Add(group.Name + "|" + label)) throw new FormatException("配色名为空或重复：" + label);
                    var entry = new ColorOverviewProtocol.Entry { Name = label, Prefab = prefab };
                    if (swap)
                    for (int i = 3; i < f.Length; i += 2)
                    {
                        string from = AssetPath(folder, f[i]), to = AssetPath(folder, f[i + 1]);
                        if (!from.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) || !to.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) || from == to || entry.Replacements.ContainsKey(from)) throw new FormatException("无效或重复的材质替换。");
                        if (!InScope(to, scopes)) throw new FormatException("目标配色材质不在本批查找范围内：" + to);
                        entry.Replacements.Add(from, to);
                    }
                    group.Entries.Add(entry);
                }
            }
            if (!header || result.Count == 0 || result.Any(g => g.Entries.Count == 0)) throw new FormatException("没有有效分组，或存在空组。");
            return result;
        }
    }
}
