#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using nadena.dev.modular_avatar.core;
using Zincat.Clothing;
using Object=UnityEngine.Object;
namespace Zincat.AvatarSplitTest
{
 [InitializeOnLoad] internal static class SplitPhotoCheck
 {
  const string Request="Library/ZincatPhotoCheck.request";
  static SplitPhotoCheck(){if(File.Exists(Request))EditorApplication.update+=Run;}
  static void Run()
  {
   if(EditorApplication.isCompiling||EditorApplication.isUpdating||!SplitIds.Editing||CauBridge.Busy)return;
   EditorApplication.update-=Run;if(!File.Exists(Request))return;File.Delete(Request);
   var report=new List<string>();var previous=SceneManager.GetActiveScene();Scene fixture=default;AvatarSplitPlan plan=null;SplitPhotoShot shot=null;
   var folder=AvatarSplitBuilder.Root+"/_PhotoCheck_"+Guid.NewGuid().ToString("N");var output="Library/ZincatPhotoCheckOutput_"+Guid.NewGuid().ToString("N");
   var materials=new List<Material>();SplitPhotoWindow view=null;
   try
   {
    AvatarSplitBuilder.EnsureFolder(folder);fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
    var source=new GameObject("拍照测试");SceneManager.MoveGameObjectToScene(source,fixture);source.transform.position=new Vector3(2,1,3);
    var descriptor=source.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();source.AddComponent<Animator>();
    source.AddComponent<VRC.Core.PipelineManager>().blueprintId="avtr_66666666-6666-6666-6666-666666666666";
    var body=GameObject.CreatePrimitive(PrimitiveType.Capsule);body.transform.SetParent(source.transform,false);body.transform.localScale=new Vector3(.18f,.6f,.18f);
    var bodyMat=new Material(Shader.Find("Unlit/Color")){color=Color.white};materials.Add(bodyMat);body.GetComponent<Renderer>().sharedMaterial=bodyMat;
    var menu=Child(source,"衣服菜单");var c=menu.AddComponent<WardrobeGeneratedSwitch>();c.AvatarRoot=source;c.IsSeparateCollection=true;c.UsesInstallTargets=true;c.DirectOutfitToggles=true;c.ParameterName="Outfit";
    menu.AddComponent<ModularAvatarParameters>().parameters=new List<ParameterConfig>{new ParameterConfig{nameOrPrefix="Outfit",syncType=ParameterSyncType.Int}};
    for(int i=0;i<2;i++)
    {
     var outfit=Child(source,i==0?"红衣服":"蓝衣服");var mesh=GameObject.CreatePrimitive(PrimitiveType.Cube);mesh.transform.SetParent(outfit.transform,false);mesh.transform.localPosition=new Vector3(0,0,.15f);mesh.transform.localScale=new Vector3(.2f,.5f,.1f);
     var mat=new Material(Shader.Find("Unlit/Color")){color=i==0?Color.red:Color.blue};materials.Add(mat);mesh.GetComponent<Renderer>().sharedMaterial=mat;
     var part=Child(source,"菜单"+i);part.AddComponent<WardrobeGeneratedMenu>().ClothingRoot=outfit;part.AddComponent<ModularAvatarMenuItem>().Control=new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control();
     var wear=Child(part,"换衣");wear.AddComponent<ModularAvatarMenuItem>().Control=new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control();wear.AddComponent<ModularAvatarObjectToggle>();
     c.Entries.Add(new WardrobeSwitchLink{OutfitRoot=outfit,PartMenuRoot=part,WearNode=wear,PartsNode=Child(menu,"入口"+i)});
    }
    // A lower garment must not change the height of the avatar wearing it.
    var hem=GameObject.CreatePrimitive(PrimitiveType.Cube);hem.transform.SetParent(c.Entries[1].OutfitRoot.transform,false);hem.transform.localPosition=new Vector3(.12f,-.9f,0);hem.transform.localScale=Vector3.one*.08f;hem.GetComponent<Renderer>().sharedMaterial=materials[2];
    body.GetComponent<Renderer>().bounds=new Bounds(source.transform.position,Vector3.one*100);
    Assert(SplitPhotoLook.GeometryBounds(body.GetComponent<Renderer>()).size.y<2,"构图使用真实网格尺寸，不受被放大的 Renderer 包围盒影响");
    c.DefaultOutfit=c.Entries[0].OutfitRoot;EditorSceneManager.SaveScene(fixture,folder+"/Fixture.unity");
    plan=ScriptableObject.CreateInstance<AvatarSplitPlan>();plan.sourceId=SplitIds.Of(source);plan.sourceScene=fixture.path;plan.photoSettings.width=320;plan.photoSettings.height=256;plan.photoSettings.outputFolder=output;
    var group=new SplitGroup{version=1,name="拍照测试V1",outfitIds=c.Entries.Select(e=>SplitIds.Of(e.OutfitRoot)).ToList(),outfitNames=new List<string>{"红衣服","蓝衣服"}};plan.groups.Add(group);
    var position=source.transform.position;var oldRender=RenderTexture.active;
    using(var stage=new SplitPhotoStage())
    {
     shot=stage.Capture(plan,group); File.WriteAllBytes("Library/ZincatPhotoCheckPreview.png",shot.texture.EncodeToPNG());
     Assert(stage.Actors.Count==2&&Mathf.Abs(stage.Actors[1].transform.position.x-stage.Actors[0].transform.position.x-.3f)<.0001f,"每套衣服一个临时角色，中心间距默认 0.3 米");
     Assert(stage.Actors.All(a=>Mathf.Abs(a.transform.position.y)<.0001f&&Mathf.Abs(a.transform.position.z)<.0001f),"不同长度衣服不再抬高或前后移动角色，共用角色原点对齐");
     Assert(stage.Actors.All(a=>AvatarSplitBuilder.Collection(a).Entries.Count==1)&&shot.texture.width==320&&shot.texture.height==256,"各角色只保留对应衣服，并实际渲染指定尺寸的照片");
     var pixels=shot.texture.GetPixels32();Assert(pixels.Any(p=>p.r>180&&p.b<80)&&pixels.Any(p=>p.b>180&&p.r<80),"照片同时拍到红色和蓝色衣服，渲染内容有效");
     Color32 bg=plan.photoSettings.background;int low=shot.texture.height,high=-1;
     for(int y=0;y<shot.texture.height;y++)for(int x=0;x<shot.texture.width;x++){var px=pixels[y*shot.texture.width+x];if(Mathf.Max(Mathf.Abs(px.r-bg.r),Mathf.Abs(px.g-bg.g),Mathf.Abs(px.b-bg.b))>12){low=Mathf.Min(low,y);high=Mathf.Max(high,y);}}
     Assert(high-low+1>=shot.texture.height*.8f,"紧凑上半身构图的人物高度占画面至少 80%，减少上下留白");
     Assert(pixels.All(px=>px.a==255),"照片输出为不透明纯色背景，不遗留透明空白");
    }
    Assert(source.transform.position==position&&source.activeSelf&&c.Entries.All(e=>e.OutfitRoot.activeSelf)&&source.GetComponent<VRC.Core.PipelineManager>().blueprintId.EndsWith("666666666666"),"拍照不移动源角色、不更改源衣服开关或蓝图");
    Assert(RenderTexture.active==oldRender,"渲染后恢复 RenderTexture 状态并释放临时场景");
    bool blocked=false;try{SplitPhoto.Export(plan,new[]{shot});}catch(InvalidOperationException){blocked=true;}
    Assert(blocked&&!Directory.Exists(output),"未经确认不创建输出文件或目录");
    CheckOutline(Assert);
    CheckPose(Assert,source);
    var defaults=new SplitPhotoSettings();Assert(defaults.width==1200&&defaults.height==900&&defaults.upperBody,"默认采用 VRChat 4:3 紧凑上半身照片");
    var oldSettings=new SplitPhotoSettings{width=1200,height=900,upperBody=false,padding=1.12f,outputFolder="keep-folder"};
    Assert(SplitPhotoLook.Upgrade(oldSettings)&&oldSettings.height==900&&oldSettings.upperBody&&oldSettings.outputFolder=="keep-folder","旧构图自动迁移，保留自定义输出目录");oldSettings.padding=1.4f;
    Assert(!SplitPhotoLook.Upgrade(oldSettings)&&oldSettings.padding==1.4f,"升级只执行一次，之后保留用户自己调整的构图");
    var wideSettings=new SplitPhotoSettings{width=1200,height=675,lookVersion=1,padding=1.4f,verticalOffset=.1f};
    Assert(SplitPhotoLook.Upgrade(wideSettings)&&wideSettings.height==900&&wideSettings.padding==1.4f&&wideSettings.verticalOffset==.1f,"原默认宽屏输出迁移为 VRChat 4:3，保留已调整的人物大小和偏移");
    string original=SplitPhoto.Signature(plan,group);plan.photoSettings.outputFolder="Assets/其他文件夹";
    Assert(SplitPhoto.Signature(plan,group)==original,"修改输出位置不会使已拍照片失效");plan.photoSettings.outputFolder=output;
    plan.photoSettings.spacing=.6f;Assert(!SplitPhoto.Valid(plan,group,shot),"修改站位或相机设置后旧照片不能导出");plan.photoSettings.spacing=.3f;
    plan.photoSettings.poseTime=.5f;Assert(!SplitPhoto.Valid(plan,group,shot),"修改姿势取帧时间后旧照片不能导出");plan.photoSettings.poseTime=0;
    plan.photoSettings.outlineWidth++;Assert(!SplitPhoto.Valid(plan,group,shot),"调整描边粗细后旧照片需要重拍");plan.photoSettings.outlineWidth--;
    shot.approved=true;var first=SplitPhoto.Export(plan,new[]{shot});var second=SplitPhoto.Export(plan,new[]{shot});
    Assert(first.Count==1&&File.Exists(first[0])&&first[0]!=second[0],"确认后导出 PNG，同名文件自动编号且不覆盖");
    var loaded=new Texture2D(2,2);try{Assert(loaded.LoadImage(File.ReadAllBytes(first[0]))&&loaded.width==320&&loaded.height==256,"导出的 PNG 能重新读取且尺寸正确");}finally{Object.DestroyImmediate(loaded);}
    var project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
    Assert(SplitPhoto.ResolveFolder("Assets/照片")==Path.GetFullPath(Path.Combine(project,"Assets/照片"))&&SplitPhoto.ResolveFolder("Assets\\照片")==SplitPhoto.ResolveFolder("Assets/照片"),"Unity 路径兼容正斜杠和反斜杠");
    Assert(SplitPhoto.ResolveFolder(Path.GetFullPath(output))==Path.GetFullPath(output),"兼容 Windows 绝对路径");
    blocked=false;try{SplitPhoto.ResolveFolder("../escape");}catch(InvalidOperationException){blocked=true;}Assert(blocked,"越出工程的相对路径会被拒绝，外部目录需明确绝对路径");
    plan.photoSettings.posePreset=1;plan.photoSettings.poseLibraryVersion=2;plan.photoSettings.poseConfigured=true;
    var attached=SplitConfigStore.Attach(source,plan);group=attached.groups[0];group.photoSelectionConfigured=true;group.photoOutfitIds=new List<string>{group.outfitIds[0]};SplitIds.Save(attached);
    var restored=SplitConfigStore.Read(attached.config,true);Assert(restored.groups[0].photoSelectionConfigured&&SplitPhoto.Selected(restored.groups[0]).Length==1&&attached.config.References[0].PhotoOutfits.Count==1,"每组拍照衣服选择保存到 Avatar 配置和直接引用");
    Assert(!attached.config.PhotoPoseClip&&restored.photoSettings.posePreset==1&&string.IsNullOrEmpty(restored.photoSettings.poseGuid),"内置姿势只保存选择，不挂载任何外部动画依赖");
    view=ScriptableObject.CreateInstance<SplitPhotoWindow>();view.ConfigureForTest(restored,descriptor);view.LayoutForTest(1000);
    Assert(view.rootVisualElement.Q("photo-workspace").style.flexDirection.value==FlexDirection.Row&&view.rootVisualElement.Q("photo-preview")!=null,"宽窗口提供衣服选择与大图预览两列");
    Assert(view.rootVisualElement.Q<ScrollView>("photo-groups").nestedInteractionKind==ScrollView.NestedInteractionKind.StopScrolling&&view.rootVisualElement.Q<ScrollView>("photo-clothes").nestedInteractionKind==ScrollView.NestedInteractionKind.StopScrolling,"分组和衣服列表都禁止到边界后接力滚动外层");
    Assert(view.rootVisualElement.Q<ScrollView>("photo-main-scroll").horizontalScrollerVisibility==ScrollerVisibility.Hidden,"主窗口仅纵向滚动，移除多余横向滚动条");
    view.LayoutForTest(620);Assert(view.rootVisualElement.Q("photo-workspace").style.flexDirection.value==FlexDirection.Column,"窄窗口改为上下布局，保留全部操作");
    foreach(var actual in SplitConfigStore.LoadedPlans().ToArray())
    {
     var real=SplitIds.Resolve(actual.sourceId) as GameObject;
     if(!real||!real.activeInHierarchy||real.scene.path.StartsWith(folder,StringComparison.Ordinal))continue;
     var candidate=actual.groups.Where(g=>SplitPhoto.Selected(g).Length>0).OrderBy(g=>Mathf.Abs(SplitPhoto.Selected(g).Length-3)).FirstOrDefault();if(candidate==null)continue;
     bool changed=SplitPhotoLook.Upgrade(actual.photoSettings);if(SplitPhotoPose.ConfigureDefault(actual.photoSettings)||changed)SplitIds.SaveLater(actual);
     var savedSettings=actual.photoSettings;var qa=new SplitPhotoSettings();JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(savedSettings),qa);SplitPhotoLook.Compact(qa);actual.photoSettings=qa;
     var originalPose=real.GetComponentsInChildren<Transform>(true).ToDictionary(t=>t,t=>(t.localPosition,t.localRotation,t.localScale));
     var sourceAnimator=real.GetComponentsInChildren<Animator>(true).FirstOrDefault(a=>a.avatar&&a.avatar.isHuman);
     var sourceArm=sourceAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm).localRotation;
     try
     {
     var customClip=Object.Instantiate(SplitPhotoPose.BuiltIn(1));customClip.hideFlags=HideFlags.None;AssetDatabase.CreateAsset(customClip,folder+"/CustomPose.anim");qa.poseGuid=AssetDatabase.AssetPathToGUID(folder+"/CustomPose.anim");
     Vector3? fixedCamera=null;float fixedSize=0;
     foreach(int preset in new[]{0,1,4})
     {
      qa.posePreset=preset;
     using(var realStage=new SplitPhotoStage())
     using(var actualShot=realStage.Capture(actual,candidate))
     {
      File.WriteAllBytes("Library/ZincatPhotoOriginalPose"+preset+".png",actualShot.texture.EncodeToPNG());
      if(fixedCamera.HasValue)Assert(Vector3.Distance(fixedCamera.Value,realStage.CameraPosition)<.00001f&&Mathf.Abs(fixedSize-realStage.CameraSize)<.00001f,"切换 T/A、自选动画后机位和缩放保持一致");
      else{fixedCamera=realStage.CameraPosition;fixedSize=realStage.CameraSize;}
      var posed=realStage.Actors[0].GetComponent<Animator>();
      if(preset<2)foreach(bool left in new[]{true,false})
      {
       var shoulder=posed.GetBoneTransform(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm);var elbow=posed.GetBoneTransform(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm);var wrist=posed.GetBoneTransform(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
       float angle=preset==0?90:135;
       Assert(Mathf.Abs(Vector3.Angle(elbow.position-shoulder.position,Vector3.up)-angle)<.1f&&Mathf.Abs(Vector3.Angle(wrist.position-elbow.position,Vector3.up)-angle)<.1f,preset==0?"T Pose 双臂严格水平展开":"A Pose 双臂严格斜下 45 度展开");
      }

      Assert(realStage.PoseFollowCounts.All(n=>n>0),"实际 Avatar 各套衣服的 MA 骨架已同步跟随姿势");
      var heads=realStage.Actors.Select(a=>a.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head).position).ToArray();
      Assert(heads.All(h=>Mathf.Abs(h.y-heads[0].y)<.0001f&&Mathf.Abs(h.z-heads[0].z)<.0001f),"应用同一站姿后各分身头部高度和前后位置保持一致");
      Assert(originalPose.All(v=>Vector3.Distance(v.Key.localPosition,v.Value.Item1)<.000001f&&Quaternion.Angle(v.Key.localRotation,v.Value.Item2)<.001f&&Vector3.Distance(v.Key.localScale,v.Value.Item3)<.000001f),"站姿拍摄未修改源角色的任何层级变换");
      report.Add("PASS: 原创姿势实拍预览已生成，仅写入 Library 临时预览。");
     }
     }
     }
     finally{actual.photoSettings=savedSettings;}
     break;
    }
    report.Add("PASS: 分组拍照测试通过；未上传，未操作 Unity 主窗口。");
   }
   catch(Exception e){report.Add("FAIL: "+e);Debug.LogException(e);}
   finally
   {
    if(view)Object.DestroyImmediate(view);shot?.Dispose();if(plan){SplitIds.Flush();Object.DestroyImmediate(plan);}
    if(fixture.IsValid()&&fixture.isLoaded)EditorSceneManager.CloseScene(fixture,true);
    if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);
    foreach(var material in materials)if(material)Object.DestroyImmediate(material);
    if(AssetDatabase.IsValidFolder(folder))AssetDatabase.DeleteAsset(folder);
    File.WriteAllLines("Library/ZincatPhotoCheck.result.txt",report);
   }
   void Assert(bool condition,string text){if(!condition)throw new Exception(text);report.Add("PASS: "+text);}
  }
  static void CheckPose(Action<bool,string> assert,GameObject source)
  {
   for(int preset=0;preset<=1;preset++){var clip=SplitPhotoPose.BuiltIn(preset);SplitPhotoPose.Validate(clip);assert(clip&&clip.humanMotion&&!AssetDatabase.Contains(clip),"原创姿势 "+SplitPhotoPose.Names[preset]+" 为内存中人形动画，无外部资源依赖");}
   var defaults=new SplitPhotoSettings();assert(SplitPhotoPose.ConfigureDefault(defaults)&&defaults.posePreset==0&&string.IsNullOrEmpty(defaults.poseGuid),"默认启用 T Pose，不引用商品动画");
   defaults.posePreset=4;defaults.poseGuid="my-custom-selection";assert(!SplitPhotoPose.ConfigureDefault(defaults)&&defaults.posePreset==4&&defaults.poseGuid=="my-custom-selection","新配置的自选动画不被后续默认迁移覆盖");
   var settings=new SplitPhotoSettings();assert(SplitPhotoPose.Apply(source,settings)==0,"非人形测试模型的 T Pose 保留原有骨骼");
   settings.poseConfigured=true;SplitPhotoPose.ConfigureDefault(settings);assert(settings.posePreset==0&&string.IsNullOrEmpty(settings.poseGuid),"旧姿势配置迁移为 T Pose");
   var mixed=new AnimationClip();try
   {
    AnimationUtility.SetEditorCurve(mixed,EditorCurveBinding.FloatCurve("",typeof(GameObject),"m_IsActive"),AnimationCurve.Constant(0,1,1));
    bool rejected=false;try{SplitPhotoPose.Validate(mixed);}catch(InvalidOperationException){rejected=true;}assert(rejected,"含物体开关的动画会被拒绝，避免姿势改变衣服选择");
   }finally{Object.DestroyImmediate(mixed);}
   var target=Child(source,"PoseTarget");var follower=Child(source,"PoseFollower");follower.transform.localPosition=new Vector3(.2f,.4f,.1f);
   var rest=new Dictionary<Transform,SplitPhotoPose.Rest>{{target.transform,new SplitPhotoPose.Rest(target.transform)},{follower.transform,new SplitPhotoPose.Rest(follower.transform)}};
   var offset=target.transform.InverseTransformPoint(follower.transform.position);target.transform.localRotation=Quaternion.Euler(0,0,40);
   SplitPhotoPose.Follow(new Dictionary<Transform,Transform>{{follower.transform,target.transform}},rest);
   assert(Vector3.Distance(follower.transform.position,target.transform.TransformPoint(offset))<.00001f,"衣服骨骼跟随保持原始绑定偏移，不强制贴合或重复旋转");Object.DestroyImmediate(target);Object.DestroyImmediate(follower);
  }
  static void CheckOutline(Action<bool,string> assert)
  {
   var settings=new SplitPhotoSettings{background=new Color(.1f,.2f,.3f,1),outlineColor=Color.red,outlineWidth=1};
   var colors=new Color32[64];Color32 clear=settings.background;clear.a=0;for(int i=0;i<64;i++)colors[i]=clear;
   for(int y=3;y<=4;y++)for(int x=3;x<=4;x++)colors[y*8+x]=new Color32(255,255,255,255);
   Texture2D Raw(){var texture=new Texture2D(8,8,TextureFormat.RGBA32,false);texture.SetPixels32(colors);texture.Apply();return texture;}
   var off=Raw();var thin=Raw();var thick=Raw();
   try
   {
    settings.outlineEnabled=false;SplitPhotoLook.Composite(off,settings);settings.outlineEnabled=true;SplitPhotoLook.Composite(thin,settings);settings.outlineWidth=3;SplitPhotoLook.Composite(thick,settings);
    var a=off.GetPixels32();var b=thin.GetPixels32();var c=thick.GetPixels32();
    assert(b[3*8+2].r>a[3*8+2].r+30&&b[3*8+3].r==255&&b[3*8+3].g==255,"描边使用指定颜色，只作用于轮廓外侧，保留人物内部颜色");
    assert(c[3*8].r>b[3*8].r+30,"描边像素粗细可调，较粗描边覆盖范围更大");
    assert(b[0].r==a[0].r&&b[0].g==a[0].g&&b[0].b==a[0].b&&b.All(px=>px.a==255),"远离人物的位置保持纯色背景，抗锯齿描边不污染整张图");
   }
   finally{Object.DestroyImmediate(off);Object.DestroyImmediate(thin);Object.DestroyImmediate(thick);}
  }
  static GameObject Child(GameObject parent,string name){var go=new GameObject(name);go.transform.SetParent(parent.transform,false);return go;}
 }
}
#endif


