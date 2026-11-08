#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
namespace Zincat.AvatarSplitTest
{
 public sealed class SplitPhotoWindow : EditorWindow
 {
  [SerializeField] VRCAvatarDescriptor source;
  [SerializeField] string sourceId;
  [SerializeField] int activeGroup;
  AvatarSplitPlan plan;
  List<OutfitMenuChoice> choices=new List<OutfitMenuChoice>();
  readonly Dictionary<string,SplitPhotoShot> shots=new Dictionary<string,SplitPhotoShot>();
  VisualElement body,sidebar,previewPanel; ScrollView mainScroll; bool pathValid,uploadPathValid;Image preview;Label notice,photoStatus,pathStatus,uploadPathStatus;Toggle approve;
  Button capture,all,approveAll,send,export;bool busy;string message,observed;IVisualElementScheduledItem timer;
  SplitGroup Current=>plan&&activeGroup>=0&&activeGroup<plan.groups.Count?plan.groups[activeGroup]:null;
  [MenuItem("Zincat/衣服菜单/分组拍照",false,1021)]
  public static void OpenMenu()=>Open(null);
  public static void Open(GameObject avatar)
  {
   var window=GetWindow<SplitPhotoWindow>("分组拍照");window.minSize=new Vector2(620,660);
   if(window.position.width<750){var r=window.position;r.width=980;r.height=820;window.position=r;}
   if(!avatar&&Selection.activeGameObject){var descriptor=Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>();if(descriptor)avatar=descriptor.gameObject;}
   if(avatar&&(!window.plan||window.source!=avatar.GetComponent<VRCAvatarDescriptor>()))window.Run(()=>window.Bind(avatar.GetComponent<VRCAvatarDescriptor>()));
   window.Show();
  }
  public static void Open(GameObject avatar,string groupId)
  {
   Open(avatar);var window=GetWindow<SplitPhotoWindow>();
   if(window.plan){int index=window.plan.groups.FindIndex(g=>g.id==groupId);if(index>=0){window.activeGroup=index;window.Build();}}
  }
  void OnEnable(){EditorApplication.playModeStateChanged+=PlayModeChanged;}
  void OnDisable(){EditorApplication.playModeStateChanged-=PlayModeChanged;timer?.Pause();ClearPhotos();if(SplitIds.Editing)SplitIds.Flush();}
  void PlayModeChanged(PlayModeStateChange state)
  {
   if(state==PlayModeStateChange.ExitingEditMode)ClearPhotos();
   if(state==PlayModeStateChange.EnteredEditMode)rootVisualElement.schedule.Execute(()=>Run(Reload));
  }
  public void CreateGUI()
  {
   if(SplitIds.Editing)
   {
    if(!source&&!string.IsNullOrEmpty(sourceId)){var go=SplitIds.Resolve(sourceId) as GameObject;if(go)source=go.GetComponent<VRCAvatarDescriptor>();}
    if(source)try{Load();}catch(Exception e){message=e.Message;}
   }
   Build();timer?.Pause();timer=rootVisualElement.schedule.Execute(Tick).Every(800);
  }
  internal void ConfigureForTest(AvatarSplitPlan value,VRCAvatarDescriptor avatar){plan=value;source=avatar;choices=AvatarOutfitMenus.Read(avatar.gameObject);Build();}
  internal void LayoutForTest(float width)=>Responsive(width);
  void Bind(VRCAvatarDescriptor avatar)
  {
   if(!SplitIds.Editing)throw new InvalidOperationException("请退出游戏模式后拍照。");
   if(!avatar||EditorUtility.IsPersistent(avatar))throw new InvalidOperationException("请拖入场景中的源 Avatar。");
   AvatarSplitBuilder.Collection(avatar.gameObject);ClearPhotos();source=avatar;sourceId=SplitIds.Of(avatar.gameObject);activeGroup=0;Load();message=null;Build();
  }
  void Load()
  {
   plan=SplitSourceStore.Find(source.gameObject)??SplitSourceStore.Create(source.gameObject);
   if(plan.photoSettings==null)plan.photoSettings=new SplitPhotoSettings();
   SplitPhotoStorage.Upgrade(plan);
   bool upgraded=SplitPhotoLook.Upgrade(plan.photoSettings);
   if(SplitPhotoPose.ConfigureDefault(plan.photoSettings)||upgraded)SplitIds.SaveLater(plan);
   foreach(var group in plan.groups)
    if(!shots.ContainsKey(group.id)&&!string.IsNullOrEmpty(group.photoCoverGuid)&&group.photoCoverSignature==SplitPhoto.Signature(plan,group))
    {
     try{var restored=SplitPhoto.Restore(plan,group);if(restored!=null)shots[group.id]=restored;}catch{/* Queue validation reports missing or modified covers. */}
    }
   choices=AvatarOutfitMenus.Read(source.gameObject);activeGroup=Mathf.Clamp(activeGroup,0,Mathf.Max(0,plan.groups.Count-1));
  }
  void Reload(){if(!source&&!string.IsNullOrEmpty(sourceId)){var go=SplitIds.Resolve(sourceId) as GameObject;if(go)source=go.GetComponent<VRCAvatarDescriptor>();}if(source){Load();Build();}else{plan=null;Build();}}
  void Run(Action action){try{action();}catch(Exception e){message=e is OperationCanceledException?"已取消拍摄，已拍照片仍可预览。":e.Message;}if(notice!=null)notice.text=message??"";UpdateState();}
  void ClearPhotos(){if(preview!=null)preview.image=null;foreach(var shot in shots.Values)shot.Dispose();shots.Clear();}
  void SaveSettings(){if(plan){SplitIds.SaveLater(plan);foreach(var shot in shots.Values)shot.approved=false;foreach(var group in plan.groups)group.photoCoverSignature="";}UpdatePreview();}
  void Build()
  {
   var root=rootVisualElement;root.Clear();body=sidebar=previewPanel=null;preview=null;approve=null;capture=all=approveAll=send=export=null;photoStatus=pathStatus=uploadPathStatus=null;root.style.backgroundColor=SplitUi.Background;root.style.paddingLeft=root.style.paddingRight=16;root.style.paddingTop=root.style.paddingBottom=14;
   var title=SplitUi.Label("分组拍照",22);root.Add(title);
   var intro=SplitUi.Label("每套衣服一个角色，排好队拍一张。先预览确认，再导出 PNG。",13,true);intro.style.marginTop=5;intro.style.marginBottom=12;root.Add(intro);
   var scroll=new ScrollView(ScrollViewMode.Vertical){name="photo-main-scroll",horizontalScrollerVisibility=ScrollerVisibility.Hidden};mainScroll=scroll;scroll.style.flexGrow=1;scroll.style.minWidth=0;
   scroll.contentContainer.style.width=Length.Percent(100);scroll.contentContainer.style.minWidth=0;root.Add(scroll);
   var sourceCard=new VisualElement();SplitUi.Box(sourceCard,12);
   var sourceRow=SplitUi.Row();var sourceField=new ObjectField("源 Avatar"){objectType=typeof(VRCAvatarDescriptor),allowSceneObjects=true,value=source};sourceField.style.flexGrow=1;sourceField.style.flexBasis=0;sourceField.style.minWidth=0;
   sourceField.RegisterValueChangedCallback(e=>Run(()=>Bind(e.newValue as VRCAvatarDescriptor)));sourceRow.Add(sourceField);sourceRow.Add(SplitUi.Button("刷新分组",()=>Run(Reload)));sourceCard.Add(sourceRow);scroll.Add(sourceCard);
   notice=SplitUi.Label(message??"",13,true);notice.style.marginTop=8;notice.style.marginBottom=8;scroll.Add(notice);
   if(!plan||!source)
   {
    var empty=new VisualElement();SplitUi.Box(empty);empty.Add(SplitUi.Label("先选择源 Avatar",18));empty.Add(SplitUi.Label("使用拆分插件已有的分组，也可以在拆分窗口点击“分组拍照…”直接打开。",13,true));scroll.Add(empty);SplitUi.Apply(root);return;
   }
   body=new VisualElement();body.name="photo-workspace";body.style.flexDirection=FlexDirection.Row;body.style.minHeight=490;
   sidebar=new VisualElement();SplitUi.Box(sidebar,12);sidebar.style.width=250;sidebar.style.flexShrink=0;sidebar.style.marginRight=10;
   sidebar.Add(SplitUi.Label("版本分组",14));
   var groups=LocalScroll("photo-groups");groups.style.height=150;groups.style.marginTop=6;
   for(int i=0;i<plan.groups.Count;i++)
   {
    int index=i;var g=plan.groups[i];
    var button=SplitUi.Button(g.name,()=>{activeGroup=index;Build();});button.tooltip=g.name;button.style.whiteSpace=WhiteSpace.Normal;button.style.minHeight=42;button.style.unityTextAlign=TextAnchor.MiddleLeft;
    button.name="photo-group-"+g.id;button.AddToClassList("wardrobe-state-button");
    Color idle=index==activeGroup?SplitUi.Selected:SplitUi.Surface;
    button.style.backgroundColor=idle;button.style.color=SplitUi.Text;button.style.paddingLeft=button.style.paddingRight=8;
    button.style.borderTopLeftRadius=button.style.borderTopRightRadius=6;button.style.borderBottomLeftRadius=button.style.borderBottomRightRadius=6;
    SplitUi.Outline(button,SplitUi.Border);
    button.RegisterCallback<MouseEnterEvent>(_=>button.style.backgroundColor=SplitUi.Selected);
    button.RegisterCallback<MouseLeaveEvent>(_=>button.style.backgroundColor=idle);
    groups.Add(button);
   }
   sidebar.Add(groups);var current=Current;
   if(current!=null)
   {
    var heading=SplitUi.Label("参与这张照片的衣服",14);heading.style.marginTop=14;sidebar.Add(heading);
    var note=SplitUi.Label("只影响拍照，不改变拆分内容或默认穿着。",12,true);note.style.marginTop=4;sidebar.Add(note);
    var tools=SplitUi.Row();tools.style.marginTop=6;tools.Add(SplitUi.Button("全选",()=>SelectAll(true)));tools.Add(SplitUi.Button("清空",()=>SelectAll(false)));sidebar.Add(tools);
    var clothes=LocalScroll("photo-clothes");clothes.style.minHeight=130;clothes.style.flexGrow=1;
    var selected=new HashSet<string>(SplitPhoto.Selected(current));
    foreach(var choice in choices.Where(c=>current.outfitIds.Contains(c.Id)))
    {
     var toggle=new Toggle{text=choice.Name,value=selected.Contains(choice.Id)};toggle.tooltip=choice.Name;toggle.style.whiteSpace=WhiteSpace.Normal;
     toggle.RegisterValueChangedCallback(e=>Run(()=>
     {
      Undo.RecordObject(plan,"选择拍照衣服");var ids=new HashSet<string>(SplitPhoto.Selected(current));if(e.newValue)ids.Add(choice.Id);else ids.Remove(choice.Id);
      current.photoSelectionConfigured=true;current.photoOutfitIds=choices.Where(c=>ids.Contains(c.Id)&&current.outfitIds.Contains(c.Id)).Select(c=>c.Id).ToList();
      current.photoCoverSignature="";if(shots.TryGetValue(current.id,out var old))old.approved=false;SplitIds.SaveLater(plan);observed=Structure();UpdatePreview();
     }));clothes.Add(toggle);
    }
    if(current.outfitIds.Count==0)clothes.Add(SplitUi.Label("先在拆分窗口为此组选择衣服。",13,true));
    sidebar.Add(clothes);
   }
   previewPanel=new VisualElement();SplitUi.Box(previewPanel,12);previewPanel.style.flexGrow=1;previewPanel.style.minWidth=0;
   var name=SplitUi.Label(current!=null?current.name:"请选择分组",16);name.tooltip=name.text;previewPanel.Add(name);
   preview=new Image{scaleMode=ScaleMode.ScaleToFit};preview.name="photo-preview";preview.style.height=285;preview.style.marginTop=10;preview.style.marginBottom=8;
   preview.style.backgroundColor=SplitUi.Dark?new Color(36f/255,36f/255,38f/255,1):new Color(225f/255,226f/255,230f/255,1);previewPanel.Add(preview);
   photoStatus=SplitUi.Label("",13,true);previewPanel.Add(photoStatus);
   var captureRow=SplitUi.Row();captureRow.style.flexWrap=Wrap.Wrap;captureRow.style.marginTop=10;
   capture=SplitUi.Button("拍摄当前分组",()=>Capture(false),true);all=SplitUi.Button("拍摄全部分组",()=>Capture(true));captureRow.Add(capture);captureRow.Add(all);previewPanel.Add(captureRow);
   approve=new Toggle{text="确认这张照片"};approve.style.marginTop=8;
   approve.RegisterValueChangedCallback(e=>{var g=Current;Run(()=>{if(g!=null&&shots.TryGetValue(g.id,out var shot)&&SplitPhoto.Valid(plan,g,shot)){if(e.newValue)shot.approved=true;else SplitPhoto.Revoke(plan,g,shot);}});UpdatePreview();});previewPanel.Add(approve);
   body.Add(sidebar);body.Add(previewPanel);scroll.Add(body);
   var poseCard=new VisualElement();SplitUi.Box(poseCard,12);poseCard.style.marginTop=12;poseCard.Add(SplitUi.Label("拍照姿势",15));
   var poseChoice=new DropdownField("姿势",SplitPhotoPose.Names,plan.photoSettings.posePreset==4?2:Mathf.Clamp(plan.photoSettings.posePreset,0,1));poseChoice.name="photo-pose-preset";poseChoice.style.marginTop=8;poseCard.Add(poseChoice);
   var customPose=new VisualElement();customPose.style.display=plan.photoSettings.posePreset==4?DisplayStyle.Flex:DisplayStyle.None;
   poseChoice.RegisterValueChangedCallback(e=>{int selected=SplitPhotoPose.Names.IndexOf(e.newValue);plan.photoSettings.posePreset=selected==2?4:selected;plan.photoSettings.poseConfigured=true;plan.photoSettings.poseLibraryVersion=3;customPose.style.display=plan.photoSettings.posePreset==4?DisplayStyle.Flex:DisplayStyle.None;SaveSettings();});
   var poseClip=SplitPhotoPose.Clip(plan.photoSettings);
   var poseField=new ObjectField("姿势动画"){objectType=typeof(AnimationClip),allowSceneObjects=false,value=poseClip};poseField.name="photo-pose-clip";poseField.style.marginTop=8;poseField.style.minWidth=0;
   var frame=new FloatField("取帧时间（秒）"){value=plan.photoSettings.poseTime,isDelayed=true};frame.name="photo-pose-time";frame.SetEnabled(poseClip);
   poseField.RegisterValueChangedCallback(e=>Run(()=>
   {
    var next=e.newValue as AnimationClip;
    try{SplitPhotoPose.Validate(next);}catch{poseField.SetValueWithoutNotify(SplitPhotoPose.Clip(plan.photoSettings));throw;}
    plan.photoSettings.poseGuid=next?AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(next)):"";plan.photoSettings.poseConfigured=true;plan.photoSettings.poseLibraryVersion=3;plan.photoSettings.poseTime=0;
    frame.SetValueWithoutNotify(0);frame.SetEnabled(next);SaveSettings();
   }));customPose.Add(poseField);
   frame.RegisterValueChangedCallback(e=>{var clip=SplitPhotoPose.Clip(plan.photoSettings);plan.photoSettings.poseTime=clip?Mathf.Clamp(e.newValue,0,clip.length):0;frame.SetValueWithoutNotify(plan.photoSettings.poseTime);SaveSettings();});customPose.Add(frame);poseCard.Add(customPose);
   var physics=new Toggle{text="拍摄前模拟 PhysBone（测试）",value=plan.photoSettings.simulatePhysBones};physics.name="photo-edit-physics";physics.style.marginTop=8;
   physics.tooltip="仅计算临时拍照分身，不进入运行模式。遵循模型原有的重力、限制与碰撞设置；暂不支持 DynamicBone。";
   var physicsTime=new FloatField("模拟时长（秒）"){value=plan.photoSettings.physicsSeconds,isDelayed=true};physicsTime.name="photo-physics-time";physicsTime.SetEnabled(plan.photoSettings.simulatePhysBones);
   physics.RegisterValueChangedCallback(e=>{plan.photoSettings.simulatePhysBones=e.newValue;physicsTime.SetEnabled(e.newValue);SaveSettings();});poseCard.Add(physics);
   physicsTime.RegisterValueChangedCallback(e=>{float duration=float.IsNaN(e.newValue)||float.IsInfinity(e.newValue)?2:Mathf.Clamp(e.newValue,.2f,5);plan.photoSettings.physicsSeconds=duration;physicsTime.SetValueWithoutNotify(duration);SaveSettings();});poseCard.Add(physicsTime);
   scroll.Add(poseCard);
   var look=new VisualElement();SplitUi.Box(look,12);look.style.marginTop=12;
   look.Add(SplitUi.Label("背景与描边",15));
   var bg=new ColorField("背景颜色"){value=plan.photoSettings.background,showAlpha=false};bg.style.marginTop=8;
   bg.RegisterValueChangedCallback(e=>{plan.photoSettings.background=e.newValue;SaveSettings();});look.Add(bg);
   var outline=new Toggle{text="人物细描边",value=plan.photoSettings.outlineEnabled};outline.RegisterValueChangedCallback(e=>{plan.photoSettings.outlineEnabled=e.newValue;SaveSettings();});look.Add(outline);
   var edge=new ColorField("描边颜色"){value=plan.photoSettings.outlineColor,showAlpha=true};edge.RegisterValueChangedCallback(e=>{plan.photoSettings.outlineColor=e.newValue;SaveSettings();});look.Add(edge);
   var thickness=new SliderInt("描边粗细",0,16){value=plan.photoSettings.outlineWidth,showInputField=true};thickness.style.minHeight=30;
   thickness.tooltip="单位为最终图片像素。默认 4 像素，设为 0 可关闭描边。";
   thickness.RegisterValueChangedCallback(e=>{plan.photoSettings.outlineWidth=e.newValue;SaveSettings();});look.Add(thickness);
   var scale=new Slider("人物大小",.5f,2.5f){value=1/plan.photoSettings.padding,showInputField=true};scale.style.minHeight=30;scale.tooltip="越大越贴近人物。构图基于实际轮廓，而非导入的包围盒。";
   scale.RegisterValueChangedCallback(e=>{plan.photoSettings.padding=1/Mathf.Clamp(e.newValue,.5f,2.5f);SaveSettings();});look.Add(scale);
   look.Add(SplitUi.Button("应用紧凑上半身构图（4:3）",()=>{SplitPhotoLook.Compact(plan.photoSettings);SaveSettings();Build();}));
   look.Add(SplitUi.Label("默认按参考图收紧到头饰至胯部附近。描边只用于拍照，不修改角色材质。",12,true));scroll.Add(look);
   var settings=new Foldout{text="更多拍摄设置",value=false};settings.style.marginTop=12;
   var spacing=new FloatField("中心间距（米）"){value=plan.photoSettings.spacing,isDelayed=true};spacing.tooltip="默认 0.3 米。角色或衣服重叠时，可调大间距后重新拍摄。";
   spacing.RegisterValueChangedCallback(e=>Run(()=>{if(e.newValue<0)throw new InvalidOperationException("间距不能为负数。");plan.photoSettings.spacing=e.newValue;SaveSettings();}));settings.Add(spacing);
   var upper=new Toggle{text="只拍上半身",value=plan.photoSettings.upperBody};upper.RegisterValueChangedCallback(e=>{plan.photoSettings.upperBody=e.newValue;SaveSettings();});settings.Add(upper);
   AddFloat(settings,"水平角度",plan.photoSettings.yaw,v=>plan.photoSettings.yaw=v);
   AddFloat(settings,"俯仰角度",plan.photoSettings.pitch,v=>plan.photoSettings.pitch=Mathf.Clamp(v,-75,75));
   AddFloat(settings,"上下偏移（米）",plan.photoSettings.verticalOffset,v=>plan.photoSettings.verticalOffset=v);
   var width=new IntegerField("图片宽度"){value=plan.photoSettings.width,isDelayed=true};width.RegisterValueChangedCallback(e=>{plan.photoSettings.width=Mathf.Clamp(e.newValue,256,4096);width.SetValueWithoutNotify(plan.photoSettings.width);SaveSettings();});settings.Add(width);
   var height=new IntegerField("图片高度"){value=plan.photoSettings.height,isDelayed=true};height.RegisterValueChangedCallback(e=>{plan.photoSettings.height=Mathf.Clamp(e.newValue,256,4096);height.SetValueWithoutNotify(plan.photoSettings.height);SaveSettings();});settings.Add(height);
   settings.Add(SplitUi.Label("发送给上传的封面会保留，其他预览关闭后释放。",12,true));scroll.Add(settings);
   var upload=new VisualElement();SplitUi.Box(upload,12);upload.style.marginTop=12;upload.Add(SplitUi.Label("发送给上传",15));
   var uploadRow=SplitUi.Row();uploadRow.style.marginTop=8;
   var cacheFolder=new TextField("上传缓存位置"){value=plan.photoSettings.uploadFolder,isDelayed=true};cacheFolder.style.flexGrow=1;cacheFolder.style.flexBasis=0;cacheFolder.style.minWidth=0;
   cacheFolder.tooltip="上传缓存需在工程 Assets 内，支持 Assets 相对路径或工程内 Windows 路径。每个 Avatar 独立子目录，按 V 编号覆盖。";
   Action<string> setCache=value=>Run(()=>{try{plan.photoSettings.uploadFolder=SplitPhotoStorage.UploadRoot(value);SplitPhotoStorage.RememberUpload(plan.photoSettings);SplitIds.SaveLater(plan);}finally{cacheFolder.SetValueWithoutNotify(plan.photoSettings.uploadFolder);UpdateUploadPath();}});
   cacheFolder.RegisterValueChangedCallback(e=>setCache(e.newValue));uploadRow.Add(cacheFolder);
   uploadRow.Add(SplitUi.Button("浏览…",()=>Run(()=>{var initial=SplitPhoto.ResolveFolder(plan.photoSettings.uploadFolder);var chosen=EditorUtility.OpenFolderPanel("上传缓存位置",Directory.Exists(initial)?initial:Application.dataPath,"");if(!string.IsNullOrEmpty(chosen))setCache(chosen);})));upload.Add(uploadRow);
   uploadPathStatus=SplitUi.Label("",12,true);uploadPathStatus.style.marginTop=5;upload.Add(uploadPathStatus);
   var sendRow=SplitUi.Row();sendRow.style.flexWrap=Wrap.Wrap;sendRow.style.marginTop=8;
   approveAll=SplitUi.Button("批量确认",()=>Run(ApproveAll));approveAll.name="photo-approve-all";sendRow.Add(approveAll);
   send=SplitUi.Button("发送已确认封面给上传",()=>Run(()=>{int count=SplitPhoto.SendToUpload(plan,shots.Values);message="已发送 "+count+" 张封面，按 V 编号覆盖缓存；尚未开始上传。";UpdatePreview();}),true);send.name="photo-send-upload";sendRow.Add(send);upload.Add(sendRow);scroll.Add(upload);
   var output=new VisualElement();SplitUi.Box(output,12);output.style.marginTop=12;output.Add(SplitUi.Label("额外导出",15));
   var outputRow=SplitUi.Row();outputRow.style.marginTop=8;
   var folder=new TextField("额外导出位置"){value=plan.photoSettings.outputFolder,isDelayed=true};folder.style.flexGrow=1;folder.style.flexBasis=0;folder.style.minWidth=0;
   folder.tooltip="支持 E:\\Photos、E:/Photos、Assets/照片 或工程相对路径。";
   folder.RegisterValueChangedCallback(e=>{plan.photoSettings.outputFolder=e.newValue;SplitIds.SaveLater(plan);UpdatePath();});outputRow.Add(folder);
   outputRow.Add(SplitUi.Button("浏览…",()=>Run(()=>
   {
    string initial;try{initial=SplitPhoto.ResolveFolder(plan.photoSettings.outputFolder);}catch{initial=Application.dataPath;}
    var chosen=EditorUtility.OpenFolderPanel("照片输出位置",Directory.Exists(initial)?initial:Application.dataPath,"");
    if(!string.IsNullOrEmpty(chosen)){plan.photoSettings.outputFolder=chosen;folder.SetValueWithoutNotify(chosen);SplitIds.SaveLater(plan);UpdatePath();}
   })));output.Add(outputRow);
   pathStatus=SplitUi.Label("",12,true);pathStatus.style.marginTop=5;output.Add(pathStatus);
   var exportRow=SplitUi.Row();exportRow.style.marginTop=10;exportRow.style.flexWrap=Wrap.Wrap;

   export=SplitUi.Button("额外导出已确认照片",()=>Run(()=>{var saved=SplitPhoto.Export(plan,shots.Values);message="已导出 "+saved.Count+" 张 PNG。同名文件会自动编号。";UpdatePreview();}));exportRow.Add(export);
   exportRow.Add(SplitUi.Button("打开文件夹",()=>Run(()=>{var path=SplitPhoto.ResolveFolder(plan.photoSettings.outputFolder);if(!Directory.Exists(path))throw new InvalidOperationException("输出文件夹尚不存在，请先导出照片。");EditorUtility.RevealInFinder(path);})));output.Add(exportRow);scroll.Add(output);
   SplitUi.Apply(root);
   foreach(var field in root.Query<FloatField>().ToList().Cast<VisualElement>().Concat(root.Query<IntegerField>().ToList()).Concat(root.Query<ColorField>().ToList()))
   {
    field.style.minHeight=30;field.style.fontSize=13;
    var input=field.Q<VisualElement>(className:"unity-base-field__input");if(input!=null){input.style.minHeight=28;input.style.unityTextAlign=TextAnchor.MiddleLeft;}
   }
   for(int i=0;i<plan.groups.Count;i++)if(i==activeGroup){var selectedButton=root.Q<Button>("photo-group-"+plan.groups[i].id);selectedButton.style.backgroundColor=SplitUi.Selected;}
   root.UnregisterCallback<GeometryChangedEvent>(Resize);root.RegisterCallback<GeometryChangedEvent>(Resize);Responsive(position.width);observed=Structure();UpdatePath();UpdateUploadPath();UpdatePreview();
  }
  static ScrollView LocalScroll(string name)
  {
   var scroll=new ScrollView(ScrollViewMode.Vertical){name=name,nestedInteractionKind=ScrollView.NestedInteractionKind.StopScrolling,horizontalScrollerVisibility=ScrollerVisibility.Hidden};
   // Keep the wheel in the list, including at its limits. Do not prevent the
   // ScrollView's own default action, scrollbar drag or keyboard interaction.
   scroll.RegisterCallback<WheelEvent>(evt=>evt.StopPropagation());
   scroll.style.minWidth=0;return scroll;
  }
  void AddFloat(VisualElement parent,string text,float value,Action<float> apply)
  {var field=new FloatField(text){value=value,isDelayed=true};field.RegisterValueChangedCallback(e=>{apply(e.newValue);SaveSettings();});parent.Add(field);}
  void SelectAll(bool selected)=>Run(()=>{var g=Current;if(g==null)return;Undo.RecordObject(plan,"选择拍照衣服");g.photoSelectionConfigured=true;g.photoOutfitIds=selected?new List<string>(g.outfitIds):new List<string>();g.photoCoverSignature="";if(shots.TryGetValue(g.id,out var p))p.approved=false;SplitIds.SaveLater(plan);Build();});
  void ApproveAll()
  {
   if(busy||!SplitIds.Editing||!plan||!source)return;
   int count=0;
   foreach(var group in plan.groups)
    if(shots.TryGetValue(group.id,out var shot)&&!shot.approved&&SplitPhoto.Valid(plan,group,shot)){shot.approved=true;count++;}
   message="已批量确认 "+count+" 张照片，可发送给上传或额外导出。";UpdatePreview();
  }
  void Resize(GeometryChangedEvent e)=>Responsive(e.newRect.width);
  void Responsive(float width)
  {
   if(body==null||sidebar==null)return;bool compact=width<800;body.style.flexDirection=compact?FlexDirection.Column:FlexDirection.Row;
   sidebar.style.width=compact?new StyleLength(Length.Percent(100)):new StyleLength(250);sidebar.style.height=compact?new StyleLength(320):new StyleLength(StyleKeyword.Auto);
   sidebar.style.marginRight=compact?0:10;sidebar.style.marginBottom=compact?10:0;
  }
  void Capture(bool batch)
  {
   if(busy)return;busy=true;UpdateState();
   rootVisualElement.schedule.Execute(()=>
   {
    try
    {
     var groups=(batch?plan.groups:Current!=null?new List<SplitGroup>{Current}:new List<SplitGroup>()).ToArray();
     if(groups.Length==0)throw new InvalidOperationException("先选择一个分组。");
     foreach(var group in groups)
     {
      using(var stage=new SplitPhotoStage())
      {
       var photo=stage.Capture(plan,group,(index,total)=>{if(EditorUtility.DisplayCancelableProgressBar("分组拍照",group.name+" · "+(index+1)+" / "+total,(float)index/total))throw new OperationCanceledException();});
       SplitPhoto.Revoke(plan,group,null);if(shots.TryGetValue(group.id,out var old))old.Dispose();shots[group.id]=photo;
      }
     }
     message="拍摄完成。确认后可发送给上传或额外导出。";
    }
    catch(Exception e){message=e is OperationCanceledException?"已取消，已拍照片仍可预览。":e.Message;}
    finally{EditorUtility.ClearProgressBar();busy=false;UpdatePreview();}
   }).StartingIn(30);
  }
  void UpdatePreview()
  {
   if(preview==null)return;var g=Current;SplitPhotoShot shot=null;if(g!=null)shots.TryGetValue(g.id,out shot);
   preview.image=shot?.texture;bool valid=g!=null&&SplitPhoto.Valid(plan,g,shot);
   photoStatus.text=shot==null?"尚未拍摄。勾选衣服后点击拍摄。":!valid?"衣服或拍摄设置已变化，请重新拍摄。":!string.IsNullOrEmpty(shot.exportedPath)?"已导出："+Path.GetFileName(shot.exportedPath):shot.approved?(!string.IsNullOrEmpty(g.photoCoverGuid)&&g.photoCoverSignature==shot.signature?"已发送给上传 · V"+g.version+".png":"已确认，可发送给上传或额外导出。"):"待确认 · "+shot.texture.width+" × "+shot.texture.height;
   if(valid&&plan.photoSettings.simulatePhysBones)photoStatus.text+=shot.physicsCount==0?" · 未找到启用的 PhysBone":shot.physicsChanged==0?" · 动骨未产生明显变化":" · 已模拟动骨";
   approve.SetValueWithoutNotify(valid&&shot.approved);approve.SetEnabled(valid&&!busy&&!CauBridge.Busy&&!CauExtraUploads.Busy);UpdateState();
  }
  void UpdatePath()
  {if(pathStatus==null||!plan)return;try{pathStatus.text=SplitPhoto.ResolveFolder(plan.photoSettings.outputFolder);SplitPhotoStorage.RememberExport(plan.photoSettings);pathValid=true;}catch(Exception e){pathStatus.text=e.Message;pathValid=false;}pathStatus.tooltip=pathStatus.text;UpdateState();}
  void UpdateUploadPath()
  {if(uploadPathStatus==null||!plan)return;try{uploadPathStatus.text=SplitPhotoStorage.UploadRoot(plan.photoSettings.uploadFolder)+" / 当前 Avatar / V编号.png（覆盖）";uploadPathValid=true;}catch(Exception e){uploadPathStatus.text=e.Message;uploadPathValid=false;}UpdateState();}
  void UpdateState()
  {
   if(notice!=null)notice.text=message??"";bool ready=!busy&&SplitIds.Editing&&plan&&source;
   if(mainScroll!=null)mainScroll.SetEnabled(!busy&&SplitIds.Editing);
   if(approve!=null)approve.SetEnabled(ready&&!CauBridge.Busy&&!CauExtraUploads.Busy&&Current!=null&&shots.TryGetValue(Current.id,out var currentShot)&&SplitPhoto.Valid(plan,Current,currentShot));
   if(capture!=null)capture.SetEnabled(ready&&Current!=null&&SplitPhoto.Selected(Current).Length>0);
   if(all!=null)all.SetEnabled(ready&&plan.groups.Count>0);
   int pending=plan?plan.groups.Count(g=>shots.TryGetValue(g.id,out var shot)&&!shot.approved&&SplitPhoto.Valid(plan,g,shot)):0;
   if(approveAll!=null){approveAll.text="批量确认（"+pending+"）";approveAll.SetEnabled(ready&&pending>0&&!CauBridge.Busy&&!CauExtraUploads.Busy);}
   int count=plan?shots.Values.Count(s=>s.approved&&plan.groups.Any(g=>SplitPhoto.Valid(plan,g,s))):0;
   if(send!=null){send.text="发送给上传（"+count+"）";send.SetEnabled(ready&&count>0&&uploadPathValid&&!CauBridge.Busy&&!CauExtraUploads.Busy);}
   if(export!=null){export.text="额外导出（"+count+"）";export.SetEnabled(ready&&count>0&&pathValid);}
  }
  string Structure()=>plan?string.Join(";",plan.groups.Select(g=>g.id+":"+g.name+":"+g.revision+":"+string.Join(",",g.outfitIds)+":"+g.photoSelectionConfigured+":"+string.Join(",",g.photoOutfitIds??new List<string>()))):"";
  void Tick(){if(!busy&&plan&&SplitIds.Editing&&observed!=Structure()){Run(()=>{choices=AvatarOutfitMenus.Read(source.gameObject);Build();});}UpdateState();}
 }
}
#endif



