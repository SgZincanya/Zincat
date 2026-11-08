#if UNITY_EDITOR
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zincat.Clothing.Editor
{
    internal sealed class WardrobeVocabularyWindow : EditorWindow
    {
        private static WardrobeVocabularyWindow current;
        private WardrobeClassificationWindow owner;
        private string key, category = "帽子", search = "", filter = "所有部位";
        private GameObject clothing;
        private int mode;
        private bool manage;
        private bool advanced;
        private readonly HashSet<int> expandedGroups=new HashSet<int>();
        private Label context, notice;
        private VisualElement body;
        private WardrobeNameVocabulary store;
        private WardrobeNameVocabulary Store => store ?? (store = WardrobeNameVocabulary.Shared);

        [MenuItem("Zincat/衣服菜单/部件词库")]
        private static void OpenStandalone()
        {
            var main = Resources.FindObjectsOfTypeAll<WardrobeClassificationWindow>().FirstOrDefault();
            if (main != null){main.OpenSelectedVocabulary();if(current!=null)return;}
            var window=GetOrCreate();window.Build();window.ShowUtility();window.Focus();
        }
        private static WardrobeVocabularyWindow GetOrCreate()
        {
            var window=current!=null?current:CreateInstance<WardrobeVocabularyWindow>();
            window.titleContent=new GUIContent("部件词库");return window;
        }
        internal static void Open(WardrobeClassificationWindow source, GameObject root, string slot, string part)
        {
            var window = GetOrCreate();
            window.owner = source; window.clothing = root; window.key = slot; window.category = part;
            window.expandedGroups.Clear(); window.Build(); window.ShowUtility(); window.Focus();
        }
        internal static void Follow(WardrobeClassificationWindow source, GameObject root, string slot, string part)
        {
            if (current == null || current.owner != source) return;
            if (current.key == slot && current.clothing == root) return;
            current.key = slot; current.clothing = root; if (!string.IsNullOrEmpty(part)) current.category = part;
            current.expandedGroups.Clear(); current.Build();
        }
        private void OnEnable()
        {
            current = this;
            ApplyBackground();
            if(position.width<390||position.height<410)position=new Rect(position.x,position.y,440,580);
            minSize = new Vector2(390,410);
        }
        private void OnDisable() { if (current == this) current = null; }
        private void OnLostFocus()
        {
            EditorApplication.delayCall += CloseIfUnfocused;
        }
        private void CloseIfUnfocused()
        {
            if(this==null||focusedWindow==this)return;
            // Unity 的分类下拉框使用临时窗口，选择期间仍属于词库操作。
            string windowType=focusedWindow==null?"":focusedWindow.GetType().Name;
            if(windowType.IndexOf("Popup",StringComparison.OrdinalIgnoreCase)>=0||windowType.IndexOf("Dropdown",StringComparison.OrdinalIgnoreCase)>=0)return;
            Close();
        }
        public void CreateGUI() => Build();
        private void ApplyBackground()
        {
            rootVisualElement.style.backgroundColor=EditorGUIUtility.isProSkin?(Color)new Color32(52,52,52,255):(Color)new Color32(232,232,234,255);
            rootVisualElement.style.flexGrow=1;
        }
        private static VisualElement Row() { var row=new VisualElement();row.style.flexDirection=FlexDirection.Row;row.style.alignItems=Align.Center;return row; }
        private static void Gap(VisualElement element, int bottom) => element.style.marginBottom=bottom;
        private void Build()
        {
            var root=rootVisualElement;ApplyBackground();root.Clear();
            root.style.paddingLeft=root.style.paddingRight=16;root.style.paddingTop=root.style.paddingBottom=14;
            var title=new Label(advanced?"词库高级设置":manage?"管理词库":"部件词库");title.style.fontSize=19;Gap(title,8);root.Add(title);
            context=new Label("当前部位："+WardrobeNameVocabulary.Display(category));context.style.color=WardrobeUiStyle.Muted;Gap(context,12);root.Add(context);
            notice=new Label("");notice.style.whiteSpace=WhiteSpace.Normal;notice.style.color=WardrobeUiStyle.Link;notice.style.display=DisplayStyle.None;
            if (!manage&&!advanced)
            {
                var field=new TextField("搜索全部"){value=search,name="vocabulary-search"};field.labelElement.style.minWidth=60;field.labelElement.style.width=60;
                field.tooltip="搜索整个词库，不受当前部位限制";Gap(field,10);
                field.RegisterValueChangedCallback(e=>{search=e.newValue;Render();});root.Add(field);
                var tabs=Row();foreach(var pair in new[]{("推荐",0),("全部",1),("分类",2)}){int target=pair.Item2;var b=new Button(()=>{mode=target;Build();}){text=pair.Item1};b.style.flexGrow=1;b.style.flexBasis=0;if(mode==target)b.name="wardrobe-primary-action";tabs.Add(b);}Gap(tabs,10);root.Add(tabs);
                if(mode==2){var choices=new[]{"所有部位"}.Concat(WardrobeNameVocabulary.VocabularyCategories.Select(WardrobeNameVocabulary.Display)).ToList();var chooser=new PopupField<string>(choices,Math.Max(0,choices.IndexOf(filter)));chooser.RegisterValueChangedCallback(e=>{filter=e.newValue;Render();});Gap(chooser,8);root.Add(chooser);}
            }
            var scroll=new ScrollView(ScrollViewMode.Vertical);scroll.style.flexGrow=1;scroll.style.minHeight=0;scroll.horizontalScrollerVisibility=ScrollerVisibility.Hidden;
            body=scroll.contentContainer;body.style.width=Length.Percent(100);root.Add(scroll);root.Add(notice);
            var footer=Row();footer.style.marginTop=12;
            if(!manage&&!advanced){var add=new Button(()=>ShowAdd()){text="＋ 添加词条"};add.style.flexGrow=1;footer.Add(add);}
            var management=new Button(()=>{if(advanced||manage){advanced=false;manage=false;}else manage=true;Build();}){text=manage||advanced?"返回词库":"管理词库"};management.style.marginLeft=8;management.style.flexGrow=1;footer.Add(management);
            if(!advanced){var settings=new Button(()=>{advanced=true;manage=false;Build();}){text="高级设置"};settings.style.marginLeft=8;footer.Add(settings);}root.Add(footer);
            Render();
        }
        private void Feedback(string text, bool error=false){notice.text=text;notice.style.color=error?new Color(1,.55f,.45f):WardrobeUiStyle.Link;notice.style.display=DisplayStyle.Flex;}
        private void Render()
        {
            body.Clear();
            try
            {
                if(advanced){DrawUsageSettings();WardrobeUiStyle.Apply(rootVisualElement);return;}
                if(manage){DrawManagement();WardrobeUiStyle.Apply(rootVisualElement);return;}
                var words=Store.Query(category,search);
                if(mode==0)
                {
                    var localUsed=words.Where(w=>w.Rank==0).ToArray();
                    var otherUsed=words.Where(w=>w.Rank==1).ToArray();
                    Group("当前常用",localUsed,0);
                    Group("当前推荐",words.Where(w=>w.Rank>=2&&WardrobeNameVocabulary.BelongsTo(w,category)).ToArray(),2);
                    Group("其他部件常用",otherUsed,1);
                    Group("词库",words.Where(w=>w.Rank>=2&&!WardrobeNameVocabulary.BelongsTo(w,category)).ToArray(),3);
                }
                else {if(mode==2&&filter!="所有部位")words=words.Where(w=>w.Categories.Any(c=>WardrobeNameVocabulary.Display(c)==filter)).ToList();Group(string.IsNullOrWhiteSpace(search)?"词库":"搜索结果",words.ToArray(),4);}
                if(owner==null||string.IsNullOrEmpty(key))Feedback("先在分件窗口点击分类旁的「词库」，即可填入名称。");
            }
            catch(Exception e){Feedback("词库读取失败："+e.Message,true);}
            WardrobeUiStyle.Apply(rootVisualElement);
        }
        private void Group(string title,WardrobeNameVocabulary.Term[] words,int section)
        {
            var group=new VisualElement {name="vocabulary-group-"+section};group.style.marginBottom=18;
            var heading=Row();var label=new Label(title);label.style.fontSize=13;label.style.flexGrow=1;heading.Add(label);
            bool expanded=expandedGroups.Contains(section);
            int capacity=section==2?16:8;
            bool overflow=section<3&&words.Length>capacity;
            Gap(heading,7);group.Add(heading);
            if(words.Length==0){var hint=new Label(section<2?"还没有使用记录":"没有匹配的词条");hint.style.color=WardrobeUiStyle.Muted;group.Add(hint);}
            else
            {
                var grid=Row();grid.style.flexWrap=Wrap.Wrap;grid.style.alignItems=Align.Stretch;
                foreach(var term in words.Take(overflow&&!expanded?capacity-1:int.MaxValue))
                {
                    string word=term.Word;var button=new Button(()=>Pick(word)){text=word,tooltip=word,name="vocabulary-word"};button.userData=word;
                    button.style.width=Length.Percent(23.5f);button.style.marginLeft=0;button.style.marginRight=Length.Percent(1);button.style.marginBottom=5;button.style.minWidth=0;button.style.height=32;
                    button.style.whiteSpace=WhiteSpace.NoWrap;button.style.overflow=Overflow.Hidden;button.style.textOverflow=TextOverflow.Ellipsis;grid.Add(button);
                }
                if(overflow)
                {
                    var more=new Button(()=>{if(!expandedGroups.Add(section))expandedGroups.Remove(section);Render();}){text=expanded?"收起 ↑":"更多 ↓",name="vocabulary-more"};
                    more.style.width=Length.Percent(23.5f);more.style.marginLeft=0;more.style.marginRight=Length.Percent(1);more.style.marginBottom=5;more.style.height=32;
                    more.tooltip=expanded?(section==2?"收起为四行":"收起为两行"):"展开此栏的全部词条";grid.Add(more);
                }
                group.Add(grid);
            }
            body.Add(group);
        }
        internal void Pick(string word)
        {
            if(owner==null||string.IsNullOrEmpty(key)){Feedback("请先在分件窗口选择分类。",true);return;}
            try
            {
                if(!owner.ApplyVocabulary(clothing,key,word,out string name)){Feedback("名称未改变，或当前分类已切换。不会重复计次。");return;}
                try{Store.Record(word,category);}catch(Exception e){Feedback("名称已填入，但使用记录未保存："+e.Message,true);return;}
                Feedback("已填入："+name+"；生成／更新菜单后生效。");
                // Keep this list stable; re-rank only when browsing again or changing context.
            }
            catch(Exception e){Feedback(e.Message,true);}
        }
        private void ShowAdd()
        {
            advanced=false;manage=true;Build();rootVisualElement.Q<TextField>("custom-word-name")?.Focus();
        }
        private void DrawUsageSettings()
        {
            var hint=new Label("按使用次数从多到少排列。修改次数会影响推荐；设为 0 可清除该部位的记录。自定义名称可在「管理词库」编辑。");hint.style.whiteSpace=WhiteSpace.Normal;Gap(hint,12);body.Add(hint);
            var records=Store.UsageRecords;
            if(records.Length==0)body.Add(new Label("还没有使用记录。"));
            foreach(var record in records)
            {
                var row=Row();Gap(row,6);
                var label=new Label(record.Word+" · "+WardrobeNameVocabulary.Display(record.Category));label.style.flexGrow=1;label.style.minWidth=0;label.style.whiteSpace=WhiteSpace.Normal;row.Add(label);
                var count=new IntegerField {value=record.Count,isDelayed=true,tooltip="使用次数",name="vocabulary-usage-count"};count.style.width=72;
                count.RegisterValueChangedCallback(e=>{try{Store.SetUsageCount(record.Word,record.Category,e.newValue);Feedback("次数已保存，返回词库后按新次数推荐。");}catch(Exception ex){count.SetValueWithoutNotify(Store.UsageRecords.FirstOrDefault(u=>u.Word==record.Word&&u.Category==record.Category)?.Count??0);Feedback(ex.Message,true);}});row.Add(count);
                var clear=new Button(()=>{try{Store.SetUsageCount(record.Word,record.Category,0);Render();}catch(Exception ex){Feedback(ex.Message,true);}}){text="清除"};row.Add(clear);body.Add(row);
            }
        }
        private void DrawManagement()
        {
            var hint=new Label("自定义词条可在任何部位使用。重复添加到不同分类，可增加推荐归属。");hint.style.whiteSpace=WhiteSpace.Normal;hint.style.color=WardrobeUiStyle.Muted;Gap(hint,10);body.Add(hint);
            var text=new TextField("名称"){name="custom-word-name"};body.Add(text);
            var choices=WardrobeNameVocabulary.VocabularyCategories.Select(WardrobeNameVocabulary.Display).ToList();var cat=new PopupField<string>("推荐部位",choices,Math.Max(0,Array.IndexOf(WardrobeNameVocabulary.VocabularyCategories,WardrobeNameVocabulary.CategoryKey(category))));body.Add(cat);
            var add=new Button(()=>{try{Store.Add(text.value,WardrobeNameVocabulary.VocabularyCategories[choices.IndexOf(cat.value)]);Render();Feedback("词条已加入。");}catch(Exception e){Feedback(e.Message,true);}}){text="添加到词库"};Gap(add,15);body.Add(add);
            foreach(var entry in Store.Custom.ToArray())
            {
                string old=entry.Word;var row=Row();var field=new TextField{value=old,isDelayed=true};field.style.flexGrow=1;field.style.minWidth=0;field.tooltip=string.Join("、",entry.Categories.Select(WardrobeNameVocabulary.Display));
                field.RegisterValueChangedCallback(e=>{try{Store.Rename(old,e.newValue);Render();}catch(Exception ex){field.SetValueWithoutNotify(old);Feedback(ex.Message,true);}});row.Add(field);
                var remove=new Button(()=>{try{Store.Remove(old);Render();Feedback("已删除自定义词条；同名内置词仍保留。");}catch(Exception e){Feedback(e.Message,true);}}){text="移除"};row.Add(remove);body.Add(row);
            }
            var location=new Label("本机共享：自定义词条和使用次数可供其他工程使用。\n记录位置："+WardrobeNameVocabulary.SharedPath+"\n旧工程词库首次使用时合并，原文件保留。");location.style.whiteSpace=WhiteSpace.Normal;location.style.color=WardrobeUiStyle.Muted;location.style.marginTop=16;body.Add(location);
        }
    }
}
#endif

