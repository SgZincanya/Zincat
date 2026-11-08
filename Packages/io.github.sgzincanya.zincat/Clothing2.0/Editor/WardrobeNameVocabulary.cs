#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Zincat.Clothing.Editor
{
    internal sealed class WardrobeNameVocabulary
    {
        [Serializable] internal sealed class CustomWord { public string Word; public List<string> Categories = new List<string>(); }
        [Serializable] internal sealed class Usage { public string Word, Category; public int Count; }
        [Serializable] private sealed class Data { public int Version = 1; public List<CustomWord> Custom = new List<CustomWord>(); public List<Usage> Uses = new List<Usage>(); public List<string> ImportedProjects=new List<string>(); }
        internal sealed class Term { public string Word; public List<string> Categories; public bool Custom; public int Current, Other, Rank; }
        internal static readonly string[] Categories = { "外套", "内衬", "内衣", "裤_裙", "胖次", "袜子", "鞋子", "袖子_手套", "帽子", "其他1", "其他2", "其他3" };
        internal static string CategoryKey(string category) => category=="其他2"||category=="其他3"?"其他1":category;
        internal static string[] VocabularyCategories => Categories.Select(CategoryKey).Distinct().ToArray();
        internal static bool BelongsTo(Term term,string category) => term.Categories.Contains(CategoryKey(category));
        private static readonly string[] Defaults = {
            "外套|外套,夹克,大衣,开衫,披风,斗篷,风衣,马甲,羽织",
            "内衬|上衣,T恤,衬衫,毛衣,背心,吊带,内搭,围裙",
            "内衣|内衣,胸罩,文胸,胸贴,束胸,内衣套装",
            "裤_裙|裤子,长裤,短裤,裙子,短裙,长裙,连衣裙,安全裤",
            "胖次|内裤,胖次,丁字裤,平角裤",
            "袜子|丝袜,长筒袜,过膝袜,短袜,连裤袜,网袜,腿环,腿套",
            "鞋子|鞋子,靴子,长靴,短靴,高跟鞋,皮鞋,运动鞋,凉鞋",
            "袖子_手套|袖子,手套,袖套,长手套,短手套,手环,腕带,护腕",
            "帽子|帽子,兔耳朵,眼镜,单片眼镜,发饰,蝴蝶结,发箍,兽耳,头纱,耳罩,贝雷帽,护目镜",
            "其他1|项圈,颈环,项链,领带,围巾,胸针,领结,耳环",
            "其他2|腰带,腰饰,尾巴,翅膀,背包,挎包,挂件",
            "其他3|饰品,配件,装饰,武器,道具"
        };
        internal static string Display(string category) => CategoryKey(category)=="其他1"?"其他（1／2／3 共用）":category == "裤_裙" ? "裤子 · 裙子" : category == "胖次" ? "内裤" : category == "袖子_手套" ? "袖子 · 手套" : category;
        private static WardrobeNameVocabulary shared;
        internal static string SharedPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Zincat","ClothingMenu","ClothingVocabulary.json");
        internal static WardrobeNameVocabulary Shared => shared ?? (shared = OpenShared(SharedPath,"ProjectSettings/Zincat/ClothingVocabulary.json"));
        internal static WardrobeNameVocabulary OpenShared(string file,string legacyPath)
        {
            var result=new WardrobeNameVocabulary(file);
            if(!File.Exists(legacyPath))return result;
            string identity=Hash128.Compute(Path.GetFullPath(legacyPath).ToUpperInvariant()).ToString();
            if(result.data.ImportedProjects.Contains(identity))return result;
            var legacy=new WardrobeNameVocabulary(legacyPath);
            result.Change(()=>
            {
                if(result.data.ImportedProjects.Contains(identity))return;
                foreach(var word in legacy.data.Custom)
                {
                    var existing=result.data.Custom.FirstOrDefault(w=>w.Word==word.Word);
                    if(existing==null)result.data.Custom.Add(new CustomWord {Word=word.Word,Categories=word.Categories.ToList()});
                    else existing.Categories=existing.Categories.Concat(word.Categories).Distinct().ToList();
                }
                foreach(var use in legacy.data.Uses)
                {
                    var existing=result.data.Uses.FirstOrDefault(u=>u.Word==use.Word&&u.Category==use.Category);
                    if(existing==null)result.data.Uses.Add(new Usage {Word=use.Word,Category=use.Category,Count=use.Count});
                    else existing.Count=(int)Math.Min(int.MaxValue,(long)existing.Count+use.Count);
                }
                result.data.ImportedProjects.Add(identity);
            });
            return result;
        }
        private readonly string path;
        private Data data;
        internal WardrobeNameVocabulary(string file)
        {
            path = file;
            Reload();
        }
        private void Reload()
        {
            if(File.Exists(path))
            {
                using(var input=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)))
                    data=JsonUtility.FromJson<Data>(input.ReadToEnd());
            }
            else data=new Data();
            if (data == null || data.Custom == null || data.Uses == null || data.Version != 1)
                throw new InvalidOperationException("词库配置无法读取，原文件已保留：" + path);
            if(data.ImportedProjects==null)data.ImportedProjects=new List<string>();
            foreach(var entry in data.Custom)entry.Categories=entry.Categories.Select(CategoryKey).Distinct().ToList();
            data.Uses=data.Uses.GroupBy(u=>(u.Word,Category:CategoryKey(u.Category)))
                .Select(g=>new Usage {Word=g.Key.Word,Category=g.Key.Category,Count=(int)Math.Min(int.MaxValue,g.Sum(u=>(long)u.Count))}).ToList();
        }
        internal IReadOnlyList<CustomWord> Custom {get{Reload();return data.Custom;}}
        internal Usage[] UsageRecords {get{Reload();return data.Uses.OrderByDescending(u=>u.Count).ThenBy(u=>u.Word,StringComparer.Ordinal)
            .Select(u=>new Usage {Word=u.Word,Category=u.Category,Count=u.Count}).ToArray();}}
        internal void SetUsageCount(string word,string category,int count)
        {
            word=Clean(word);
            category=CategoryKey(category);
            if(!Categories.Contains(category)||count<0)throw new InvalidOperationException("使用次数须为 0 或正整数。");
            Change(()=>
            {
                data.Uses.RemoveAll(u=>u.Word==word&&u.Category==category);
                if(count>0)data.Uses.Add(new Usage {Word=word,Category=category,Count=count});
            });
        }
        private void Change(Action action)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using(var guard=AcquireWriteLock())
            {
            Reload();
            string original = JsonUtility.ToJson(data);
            try
            {
                action(); Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(data, true), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, path + ".previous"); else File.Move(temporary, path);
            }
            catch { data = JsonUtility.FromJson<Data>(original); throw; }
            }
        }
        private FileStream AcquireWriteLock()
        {
            for(int attempt=0;;attempt++)
            {
                try{return new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException){if(attempt>=19)throw new IOException("另一工程正在保存词库，请稍后重试。");System.Threading.Thread.Sleep(25);}
            }
        }
        internal static string Clean(string word)
        {
            word = (word ?? "").Trim();
            if (word.Length == 0 || word.Length > 40 || word.Any(char.IsControl)) throw new InvalidOperationException("名称请填写 1～40 个字，不要换行。");
            return word;
        }
        internal void Add(string word, string category)
        {
            word = Clean(word);
            category=CategoryKey(category);
            Change(() => { var entry = data.Custom.FirstOrDefault(w => w.Word == word); if (entry == null) { entry = new CustomWord { Word = word }; data.Custom.Add(entry); } if (!entry.Categories.Contains(category)) entry.Categories.Add(category); });
        }
        internal void Rename(string word, string replacement)
        {
            replacement = Clean(replacement); if (word == replacement) return;
            Change(() => {
                var entry = data.Custom.First(w => w.Word == word); var existing = data.Custom.FirstOrDefault(w => w.Word == replacement);
                if (existing == null) entry.Word = replacement;
                else { existing.Categories = existing.Categories.Concat(entry.Categories).Distinct().ToList(); data.Custom.Remove(entry); }
                foreach (var usage in data.Uses.Where(u => u.Word == word)) usage.Word = replacement;
                data.Uses = data.Uses.GroupBy(u => (u.Word, u.Category)).Select(g => new Usage {Word=g.Key.Word,Category=g.Key.Category,Count=(int)Math.Min(int.MaxValue,g.Sum(u=>(long)u.Count))}).ToList();
            });
        }
        internal void Remove(string word) => Change(() => data.Custom.RemoveAll(w => w.Word == word));
        internal void Record(string word, string category)
        {
            word = Clean(word);
            category=CategoryKey(category);
            Change(() => { var entry = data.Uses.FirstOrDefault(u => u.Word == word && u.Category == category); if (entry == null) data.Uses.Add(new Usage { Word=word, Category=category, Count=1 }); else if (entry.Count < int.MaxValue) entry.Count++; });
        }
        internal List<Term> Query(string category, string search = "")
        {
            Reload();
            category=CategoryKey(category);
            var words = new Dictionary<string, Term>();
            foreach (string line in Defaults)
            {
                var parts = line.Split('|'); foreach (string word in parts[1].Split(','))
                { if (!words.TryGetValue(word, out var term)) words[word] = term = new Term { Word=word, Categories=new List<string>() }; term.Categories.Add(CategoryKey(parts[0])); }
            }
            foreach (var entry in data.Custom)
            {
                if (!words.TryGetValue(entry.Word, out var term)) words[entry.Word] = term = new Term {Word=entry.Word, Categories=new List<string>()};
                term.Custom = true; term.Categories = term.Categories.Concat(entry.Categories).Distinct().ToList();
            }
            foreach (var term in words.Values)
            {
                term.Current = (int)Math.Min(int.MaxValue,data.Uses.Where(u=>u.Word==term.Word&&u.Category==category).Sum(u=>(long)u.Count));
                term.Other = (int)Math.Min(int.MaxValue,data.Uses.Where(u=>u.Word==term.Word&&u.Category!=category).Sum(u=>(long)u.Count));
                bool local = term.Categories.Contains(category);
                term.Rank = term.Current > 0 ? 0 : term.Other > 0 ? 1 : term.Custom ? local ? 2 : 3 : local ? 4 : 5;
            }
            return words.Values.Where(t=>t.Word.IndexOf(search??"",StringComparison.OrdinalIgnoreCase)>=0)
                .OrderBy(t=>t.Rank).ThenByDescending(t=>t.Rank==0?t.Current:t.Rank==1?t.Other:0).ThenBy(t=>t.Word,StringComparer.Ordinal).ToList();
        }
    }
}
#endif
