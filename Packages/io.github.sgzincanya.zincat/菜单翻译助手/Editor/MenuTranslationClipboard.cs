using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zincat.MenuTranslation
{
    // Plain text transport. Asset identity and source text travel with each row.
    public static class MenuTranslationClipboard
    {
        public sealed class Row
        {
            public string guid;
            public string objectId;
            public long localId;
            public int index;
            public string original;
            public string translation;
        }
        static string Escape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }
        static string Unescape(string value)
        {
            var result = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\') { result.Append(value[i]); continue; }
                if (++i == value.Length) throw new FormatException("行末有不完整的转义符。");
                switch (value[i])
                {
                    case '\\': result.Append('\\'); break;
                    case 't': result.Append('\t'); break;
                    case 'r': result.Append('\r'); break;
                    case 'n': result.Append('\n'); break;
                    default: throw new FormatException("包含无法识别的转义符：\\" + value[i]);
                }
            }
            return result.ToString();
        }
        public static string Write(IEnumerable<Row> rows)
        {
            var text = new StringBuilder();
            text.AppendLine("ZMT2");
            text.AppendLine("# 请把以下菜单名称翻译为简体中文，只填写第三列；保留第一列标识和第二列原文，逐行完整返回一个代码块。");
            text.AppendLine("# 返回的代码块必须以 ZMT2 单独一行开头，完整保留这一首行；不要省略、翻译或修改 ZMT2，否则插件无法识别。");
            text.AppendLine("# 每行三列，用真正的制表符分隔。保留反斜杠转义；已有中文可原样填写。不要输出 Markdown 表格。");
            text.AppendLine("# 标识\t原文\t中文译文");
            foreach (var row in rows)
            {
                string identity = string.IsNullOrEmpty(row.objectId)
                    ? row.guid + ":" + row.localId.ToString(CultureInfo.InvariantCulture) + ":" + row.index.ToString(CultureInfo.InvariantCulture)
                    : "ma@" + row.objectId;
                text.Append(identity).Append('\t').Append(Escape(row.original)).Append('\t').Append(Escape(row.translation)).AppendLine();
            }
            return text.ToString();
        }
        public static List<Row> Read(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("剪贴板为空。请先复制聊天中返回的翻译内容。");
            var result = new List<Row>();
            var keys = new HashSet<string>();
            bool header = false;
            int lineNumber = 0;
            foreach (var raw in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n'))
            {
                lineNumber++;
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("```", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (!header)
                {
                    if (line.Trim() != "ZMT2") throw new FormatException("内容不是菜单翻译文本。请复制完整代码块（包含 ZMT2 首行）。");
                    header = true; continue;
                }
                var fields = line.Split('\t');
                if (fields.Length != 3) throw new FormatException("第 " + lineNumber + " 行不是三列。请保留制表符并复制完整内容。");
                string identity = fields[0].Trim();
                if (identity.StartsWith("ma@", StringComparison.Ordinal))
                {
                    string objectId = identity.Substring(3);
                    if (!Regex.IsMatch(objectId, @"\AGlobalObjectId_V1-[123]-[0-9a-f]{32}-[0-9]+-[0-9]+\z"))
                        throw new FormatException("第 " + lineNumber + " 行的 MA 对象标识已损坏。");
                    if (!keys.Add(identity)) throw new FormatException("内容含有重复 MA 条目，请只复制一份翻译结果。");
                    result.Add(new Row { objectId = objectId, original = Unescape(fields[1]), translation = Unescape(fields[2]) });
                    continue;
                }
                var parts = fields[0].Trim().Split(':');
                Guid guid; long localId; int index;
                if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out guid)
                    || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out localId)
                    || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0)
                    throw new FormatException("第 " + lineNumber + " 行的条目标识已损坏，请重新复制原内容。");
                string normalized = guid.ToString("N") + ":" + localId + ":" + index;
                if (!keys.Add(normalized)) throw new FormatException("内容含有重复条目，未读取。请只复制一份翻译结果。");
                result.Add(new Row { guid = guid.ToString("N"), localId = localId, index = index, original = Unescape(fields[1]), translation = Unescape(fields[2]) });
            }
            if (!header || result.Count == 0) throw new FormatException("没有找到菜单条目。请复制完整的翻译内容。");
            return result;
        }
    }
}
