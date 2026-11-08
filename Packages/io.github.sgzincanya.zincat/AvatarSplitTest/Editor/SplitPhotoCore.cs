#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Playables;
using UnityEngine.Animations;
using nadena.dev.modular_avatar.core;
using Object=UnityEngine.Object;
namespace Zincat.AvatarSplitTest
{
 [Serializable] public sealed class SplitPhotoSettings
 {
  public string outputFolder=SplitPhotoStorage.DefaultExportFolder;
  public string uploadFolder=SplitPhotoStorage.DefaultUploadFolder;
  public int storageVersion;
  public float spacing=.3f, yaw, pitch, padding=1.03f, verticalOffset;
  public int width=1200,height=900;
  public int lookVersion;
  public bool poseConfigured;
  public int posePreset,poseLibraryVersion;
  public string poseGuid="";
  public float poseTime;
  public bool simulatePhysBones;
  public float physicsSeconds=2;
  public bool outlineEnabled=true;
  public int outlineWidth=4;
  public Color outlineColor=new Color(.78f,.83f,.9f,1);
  public bool upperBody=true;
  public Color background=new Color(48f/255,56f/255,70f/255,1);
 }
 internal static class SplitPhotoRemovedLiveCleanup
 {
  [Serializable] sealed class OldJob{public string root;public bool applyOnPlay;}
  [InitializeOnLoadMethod] static void Schedule()
  {
   const string key="Zincat.Photo.LiveJob";string json=SessionState.GetString(key,"");if(string.IsNullOrEmpty(json))return;
   var job=JsonUtility.FromJson<OldJob>(json);if(job==null||string.IsNullOrEmpty(job.root)||!job.root.StartsWith("ZincatPhotoLive_",StringComparison.Ordinal))return;
   void Finish()
   {
    if(EditorApplication.isPlayingOrWillChangePlaymode){EditorApplication.isPlaying=false;return;}
    EditorApplication.update-=Finish;
    if(json.Contains("\"applyOnPlay\""))nadena.dev.ndmf.config.Config.ApplyOnPlay=job.applyOnPlay;
    var root=Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g=>g&&g.name==job.root&&g.scene.IsValid()&&!EditorUtility.IsPersistent(g));
    if(root)
    {
     var scene=root.scene;var roots=scene.GetRootGameObjects();
     if(string.IsNullOrEmpty(scene.path)&&roots.Length==1&&roots[0]==root)EditorSceneManager.CloseScene(scene,true);else Object.DestroyImmediate(root);
    }
    SessionState.EraseString(key);
   }
   EditorApplication.update+=Finish;
  }
 }
 internal sealed class SplitPhotoShot : IDisposable
 {
  public string groupId,signature; public int physicsCount,physicsChanged; public Texture2D texture; public bool approved; public string exportedPath;
  public void Dispose(){if(texture)Object.DestroyImmediate(texture);texture=null;}
 }
 internal static class SplitPhoto
 {
  public static string[] Selected(SplitGroup group)=> (group.photoSelectionConfigured?(group.photoOutfitIds??new List<string>()).Where(id=>group.outfitIds.Contains(id)):group.outfitIds).Distinct().ToArray();
  public static string Signature(AvatarSplitPlan plan,SplitGroup group)
  {
   var s=plan.photoSettings;string F(float v)=>v.ToString("R",CultureInfo.InvariantCulture);
   return string.Join("|",s.simulatePhysBones.ToString(),F(s.physicsSeconds),s.posePreset.ToString(),s.poseGuid??"",F(s.poseTime),SplitPhotoPose.ClipHash(s),plan.sourceId,group.id,group.revision.ToString(),string.Join(",",Selected(group)),s.width.ToString(),s.height.ToString(),F(s.spacing),F(s.yaw),F(s.pitch),F(s.padding),F(s.verticalOffset),s.upperBody.ToString(),F(s.background.r),F(s.background.g),F(s.background.b),F(s.background.a),s.outlineEnabled.ToString(),s.outlineWidth.ToString(),F(s.outlineColor.r),F(s.outlineColor.g),F(s.outlineColor.b),F(s.outlineColor.a));
  }
  public static bool Valid(AvatarSplitPlan plan,SplitGroup group,SplitPhotoShot shot)=>shot!=null&&shot.texture&&shot.groupId==group.id&&shot.signature==Signature(plan,group);
  public static string CoverHash(string path)
  {using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();}
  public static Texture2D CoverTexture(SplitGroup group)=>AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(group.photoCoverGuid??""));
  public static void Confirm(AvatarSplitPlan plan,SplitGroup group,SplitPhotoShot shot)
  {
   if(!SplitIds.Editing||CauBridge.Busy||CauExtraUploads.Busy)throw new InvalidOperationException("请等待 CAU 上传结束并退出运行模式后发送封面。");
   if(!Valid(plan,group,shot))throw new InvalidOperationException("照片已失效，请重新拍摄。");
   string path=SplitPhotoStorage.PreparePath(plan,group);
   string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{File.WriteAllBytes(temp,shot.texture.EncodeToPNG());AssetDatabase.ReleaseCachedFileHandles();if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
   finally{if(File.Exists(temp))File.Delete(temp);}
   AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
   SplitPhotoStorage.Configure(plan,group,path);
   var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(path);if(!texture)throw new IOException("封面导入失败："+path);
   group.photoCoverGuid=AssetDatabase.AssetPathToGUID(path);group.photoCoverSignature=shot.signature;group.photoCoverHash=CoverHash(path);
   shot.approved=true;SplitIds.SaveLater(plan);
  }
  public static int SendToUpload(AvatarSplitPlan plan,IEnumerable<SplitPhotoShot> shots)
  {
   var ready=shots.Where(p=>p!=null&&p.approved&&plan.groups.Any(g=>Valid(plan,g,p))).ToArray();
   if(ready.Length==0)throw new InvalidOperationException("先拍摄并确认至少一张照片。");
   foreach(var shot in ready){var group=plan.groups.First(g=>g.id==shot.groupId);SplitPhotoStorage.ValidateTarget(plan,group,SplitPhotoStorage.PathFor(plan,group));}
   foreach(var shot in ready)Confirm(plan,plan.groups.First(g=>g.id==shot.groupId),shot);
   SplitPhotoStorage.RememberUpload(plan.photoSettings);SplitIds.SaveLater(plan);return ready.Length;
  }
  public static void Revoke(AvatarSplitPlan plan,SplitGroup group,SplitPhotoShot shot)
  {if(shot!=null)shot.approved=false;group.photoCoverGuid=group.photoCoverSignature=group.photoCoverHash="";SplitIds.SaveLater(plan);}
  public static Texture2D UploadCover(AvatarSplitPlan plan,SplitGroup group)
  {
   if(string.IsNullOrEmpty(group.photoCoverGuid))
   {
    if(string.IsNullOrEmpty(group.blueprintId))throw new InvalidOperationException(group.name+" 是新模型，请先拍摄、确认并发送给上传。");
    return null;
   }
   if(group.photoCoverSignature!=Signature(plan,group))throw new InvalidOperationException(group.name+" 的衣服或拍摄设置已变化，请重新拍摄、确认并发送给上传。");
   var texture=CoverTexture(group);string path=texture?AssetDatabase.GetAssetPath(texture):"";
   if(!texture||!File.Exists(path)||CoverHash(path)!=group.photoCoverHash)throw new InvalidOperationException(group.name+" 的封面缺失或文件已变化，请重新拍摄并确认。");
   return texture;
  }
  public static SplitPhotoShot Restore(AvatarSplitPlan plan,SplitGroup group)
  {
   var cover=UploadCover(plan,group);if(!cover)return null;
   var texture=new Texture2D(2,2,TextureFormat.RGBA32,false){hideFlags=HideFlags.HideAndDontSave};
   try{if(!texture.LoadImage(File.ReadAllBytes(AssetDatabase.GetAssetPath(cover))))throw new IOException("无法读取已保存的封面。");
    return new SplitPhotoShot{groupId=group.id,signature=group.photoCoverSignature,texture=texture,approved=true};}
   catch{Object.DestroyImmediate(texture);throw;}
  }
  public static string ResolveFolder(string input)
  {
   if(string.IsNullOrWhiteSpace(input))throw new InvalidOperationException("先设置照片输出文件夹。");
   string value=Environment.ExpandEnvironmentVariables(input.Trim().Trim('"')).Replace('\\',Path.DirectorySeparatorChar).Replace('/',Path.DirectorySeparatorChar);
   string project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
   bool absolute=Path.IsPathRooted(value);string full=Path.GetFullPath(absolute?value:Path.Combine(project,value));
   if(!absolute&&!full.Equals(project,StringComparison.OrdinalIgnoreCase)&&!full.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("工程相对路径不能越出工程；导出到其他位置请填写 Windows 绝对路径。");
   return full;
  }
  public static string SafeFile(string value)
  {
   foreach(char c in Path.GetInvalidFileNameChars())value=value.Replace(c,'_');
   value=value.Trim().TrimEnd('.');if(value.Length>90)value=value.Substring(0,90);
   return string.IsNullOrWhiteSpace(value)?"Avatar":value;
  }
  public static List<string> Export(AvatarSplitPlan plan,IEnumerable<SplitPhotoShot> shots)
  {
   var ready=shots.Where(p=>p!=null&&p.approved&&plan.groups.Any(g=>Valid(plan,g,p))).ToArray();
   if(ready.Length==0)throw new InvalidOperationException("先拍摄并确认至少一张有效照片；修改衣服或相机后需要重拍。");
   string folder=ResolveFolder(plan.photoSettings.outputFolder);Directory.CreateDirectory(folder);
   var source=SplitIds.Resolve(plan.sourceId) as GameObject;var paths=new List<string>();
   foreach(var shot in ready)
   {
    var group=plan.groups.First(g=>g.id==shot.groupId);string stem=SafeFile((source?source.name:"Avatar")+"V"+group.version);
    string path=Path.Combine(folder,stem+".png");int index=1;
    while(File.Exists(path)||File.Exists(path+".tmp"))path=Path.Combine(folder,stem+"_"+(index++).ToString("000")+".png");
    string temp=path+".tmp";
    try{using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write)){var png=shot.texture.EncodeToPNG();stream.Write(png,0,png.Length);}File.Move(temp,path);}
    finally{if(File.Exists(temp))File.Delete(temp);}
    shot.exportedPath=path;paths.Add(path);
    string assets=Path.GetFullPath(Application.dataPath)+Path.DirectorySeparatorChar;
    if(path.StartsWith(assets,StringComparison.OrdinalIgnoreCase))
    {
     string asset="Assets/"+path.Substring(assets.Length).Replace('\\','/');AssetDatabase.ImportAsset(asset,ImportAssetOptions.ForceUpdate);
     if(AssetImporter.GetAtPath(asset) is TextureImporter importer&&importer.npotScale!=TextureImporterNPOTScale.None){importer.npotScale=TextureImporterNPOTScale.None;importer.SaveAndReimport();}
    }
   }
   return paths;
  }
 }
 internal static class SplitPhotoPose
 {
  public static readonly List<string> Names=new List<string>{"T Pose","A Pose","自选动画"};
  static readonly Dictionary<int,AnimationClip> BuiltIns=new Dictionary<int,AnimationClip>();
  static SplitPhotoPose(){AssemblyReloadEvents.beforeAssemblyReload+=Clear;EditorApplication.quitting+=Clear;}
  static void Clear(){foreach(var clip in BuiltIns.Values)if(clip)Object.DestroyImmediate(clip);BuiltIns.Clear();}
  public static AnimationClip Clip(SplitPhotoSettings s)=>s.posePreset!=4||string.IsNullOrEmpty(s.poseGuid)?null:AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(s.poseGuid));
  public static string ClipHash(SplitPhotoSettings s)=>s.posePreset!=4||string.IsNullOrEmpty(s.poseGuid)?"bone-camera-v1-basic-poses-v2":AssetDatabase.GetAssetDependencyHash(AssetDatabase.GUIDToAssetPath(s.poseGuid)).ToString();
  public static bool ConfigureDefault(SplitPhotoSettings s)
  {
   if(s.poseLibraryVersion>=3)return false;
   // Keep an explicitly chosen custom animation; replace removed presets with T Pose.
   if(s.posePreset==5||(s.poseLibraryVersion<2&&s.posePreset!=4)){s.posePreset=0;s.poseGuid="";s.poseTime=0;}
   s.poseConfigured=true;s.poseLibraryVersion=3;return true;
  }
  public static AnimationClip BuiltIn(int preset)
  {
   if(preset<0||preset>1)throw new ArgumentOutOfRangeException(nameof(preset));
   if(BuiltIns.TryGetValue(preset,out var cached)&&cached)return cached;
   // Original parameter-authored poses. No imported animation is sampled,
   // copied, or used as input. Clips exist only in editor memory.
   var values=HumanTrait.MuscleName.ToDictionary(n=>n,n=>0f);
   void Set(string name,float value){if(!values.ContainsKey(name))name=name.Replace("Hand."," ").Replace("."," ");if(values.ContainsKey(name))values[name]=value;}
   string CurveName(string name)
   {
    foreach(string side in new[]{"Left","Right"})foreach(string finger in new[]{"Thumb","Index","Middle","Ring","Little"})
    {string prefix=side+" "+finger+" ";if(name.StartsWith(prefix,StringComparison.Ordinal))return side+"Hand."+finger+"."+name.Substring(prefix.Length);}
    return name;
   }
   foreach(string side in new[]{"Left","Right"})
   {
    Set(side+" Shoulder Down-Up",0);Set(side+" Arm Down-Up",preset==1?-.25f:0);
    Set(side+" Forearm Stretch",1);Set(side+" Lower Leg Stretch",1);
    foreach(string finger in new[]{"Thumb","Index","Middle","Ring","Little"})
     foreach(string segment in new[]{"1","2","3"})Set(side+"Hand."+finger+"."+segment+" Stretched",1);
   }
   var clip=new AnimationClip{name=Names[preset],hideFlags=HideFlags.HideAndDontSave};
   foreach(var item in values)AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("",typeof(Animator),CurveName(item.Key)),AnimationCurve.Constant(0,1,item.Value));
   foreach(var item in new Dictionary<string,float>{{"RootT.x",0},{"RootT.y",1},{"RootT.z",0},{"RootQ.x",0},{"RootQ.y",0},{"RootQ.z",0},{"RootQ.w",1}})
    AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("",typeof(Animator),item.Key),AnimationCurve.Constant(0,1,item.Value));
   BuiltIns[preset]=clip;return clip;
  }
  public static void Validate(AnimationClip clip)
  {
   if(!clip)return;
   if(!clip.humanMotion||AnimationUtility.GetObjectReferenceCurveBindings(clip).Length>0||AnimationUtility.GetCurveBindings(clip).Any(b=>b.type!=typeof(Animator)||!string.IsNullOrEmpty(b.path)))
    throw new InvalidOperationException("测试版请使用纯人形姿势动画；含物体开关、材质或其他对象曲线的动画暂不支持。");
  }
  internal struct Rest
  {
   public Matrix4x4 inverse;public Vector3 position;public Quaternion rotation;
   public Rest(Transform t){inverse=t.worldToLocalMatrix;position=t.position;rotation=t.rotation;}
  }
  internal static void Follow(Dictionary<Transform,Transform> links,Dictionary<Transform,Rest> rest)
  {
   var done=new HashSet<Transform>();var visiting=new HashSet<Transform>();
   foreach(var bone in links.Keys)Move(bone);
   void Ancestors(Transform t){if(!t)return;if(links.ContainsKey(t))Move(t);else Ancestors(t.parent);}
   void Move(Transform bone)
   {
    if(!bone||done.Contains(bone))return;
    if(!visiting.Add(bone))throw new InvalidOperationException("衣服骨骼存在循环映射，无法应用拍照姿势。");
    Ancestors(bone.parent);
    var target=links[bone];Ancestors(target);
    bone.SetPositionAndRotation(target.localToWorldMatrix.MultiplyPoint3x4(rest[target].inverse.MultiplyPoint3x4(rest[bone].position)),target.rotation*Quaternion.Inverse(rest[target].rotation)*rest[bone].rotation);
    visiting.Remove(bone);done.Add(bone);
   }
  }
  public static int Apply(GameObject copy,SplitPhotoSettings settings)
  {

   var clip=settings.posePreset==4?Clip(settings):BuiltIn(settings.posePreset);if(!clip)throw new InvalidOperationException("拍照姿势动画已丢失，请重新指定或清空姿势动画。");Validate(clip);
   var animator=copy.GetComponentsInChildren<Animator>(true).FirstOrDefault(a=>a.avatar&&a.avatar.isValid&&a.avatar.isHuman);
   if(!animator&&settings.posePreset==0)return 0;
   if(!animator)throw new InvalidOperationException("源角色没有有效的人形 Animator，无法使用姿势动画；可清空姿势动画按原姿势拍摄。");
   var root=copy.transform;bool Inside(Transform t)=>t&&(t==root||t.IsChildOf(root));
   var links=new Dictionary<Transform,Transform>();
   foreach(var merge in copy.GetComponentsInChildren<ModularAvatarMergeArmature>(true))
   {
    var pairs=merge.GetBonesMapping();if(pairs==null)continue;
    foreach(var pair in pairs)Link(pair.Item2,pair.Item1);
   }
   foreach(var proxy in copy.GetComponentsInChildren<ModularAvatarBoneProxy>(true))if(proxy.target)Link(proxy.transform,proxy.target);
   void Link(Transform bone,Transform target)
   {
    if(bone==target)return;
    if(!Inside(bone)||!Inside(target))throw new InvalidOperationException("衣服骨架指向角色外部，请先检查 MA 骨骼映射。");
    links[bone]=target;
   }
   var rest=copy.GetComponentsInChildren<Transform>(true).ToDictionary(t=>t,t=>new Rest(t));
   var position=root.position;var rotation=root.rotation;var scale=root.localScale;
   var graph=PlayableGraph.Create("Zincat Photo Pose");
   try
   {
    animator.enabled=true;animator.runtimeAnimatorController=null;animator.applyRootMotion=false;animator.fireEvents=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
    var playable=AnimationClipPlayable.Create(graph,clip);playable.SetApplyFootIK(false);playable.SetApplyPlayableIK(false);playable.SetTime(Mathf.Clamp(settings.poseTime,0,clip.length));playable.SetSpeed(0);
    var output=AnimationPlayableOutput.Create(graph,"Photo Pose",animator);output.SetSourcePlayable(playable);graph.Play();graph.Evaluate(0);
   }
   finally
   {
    graph.Destroy();animator.enabled=false;root.SetPositionAndRotation(position,rotation);root.localScale=scale;
   }
   if(settings.posePreset==0||settings.posePreset==1)
   {
    // Retargeted muscle ranges vary by Avatar. Enforce geometric T/A arm
    // directions after sampling, then let garment mappings follow the final pose.
    foreach(bool left in new[]{true,false})
    {
     var upper=animator.GetBoneTransform(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm);
     var lower=animator.GetBoneTransform(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm);
     var hand=animator.GetBoneTransform(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
     if(!upper||!lower||!hand)continue;
     float sign=Mathf.Sign(root.InverseTransformPoint(upper.position).x);if(sign==0)sign=left?1:-1;
     float angle=settings.posePreset==0?0:45*Mathf.Deg2Rad;
     var direction=root.right*(sign*Mathf.Cos(angle))-root.up*Mathf.Sin(angle);
     upper.rotation=Quaternion.FromToRotation(lower.position-upper.position,direction)*upper.rotation;
     lower.rotation=Quaternion.FromToRotation(hand.position-lower.position,direction)*lower.rotation;
    }
   }
   Follow(links,rest);return links.Count;
  }
 }
 internal static class SplitPhotoLook
 {
  public static void Compact(SplitPhotoSettings s)
  {s.upperBody=true;s.width=1200;s.height=900;s.padding=1.03f;s.yaw=s.pitch=s.verticalOffset=0;s.lookVersion=2;}
  public static bool Upgrade(SplitPhotoSettings s)
  {
   if(s.lookVersion>=2)return false;
   if(s.lookVersion<1)
   {
    s.upperBody=true;s.padding=1.03f;s.verticalOffset=0;
    s.outlineEnabled=true;s.outlineWidth=4;s.outlineColor=new Color(.78f,.83f,.9f,1);
    if(Mathf.Abs(s.background.r-.13f)<.001f&&Mathf.Abs(s.background.g-.15f)<.001f&&Mathf.Abs(s.background.b-.19f)<.001f)s.background=new Color(48f/255,56f/255,70f/255,1);
   }
   if(s.width==1200&&s.height==675)s.height=900;
   s.lookVersion=2;return true;
  }
  public static bool SkeletonFrame(GameObject avatar,bool upperBody,out Bounds bounds)
  {
   bounds=default;var animator=avatar.GetComponentsInChildren<Animator>(true).FirstOrDefault(a=>a.avatar&&a.avatar.isValid&&a.avatar.isHuman);if(!animator)return false;
   Transform Bone(HumanBodyBones bone)=>animator.GetBoneTransform(bone);
   var head=Bone(HumanBodyBones.Head);var hips=Bone(HumanBodyBones.Hips);var lf=Bone(HumanBodyBones.LeftFoot);var rf=Bone(HumanBodyBones.RightFoot);
   if(!head||!hips||!lf||!rf)return false;
   float feet=Mathf.Min(lf.position.y,rf.position.y),height=Mathf.Max(.05f,head.position.y-feet);
   float top=head.position.y+height*.22f;
   float bottom=upperBody?hips.position.y-height*.20f:feet-height*.055f;
   var ls=Bone(HumanBodyBones.LeftUpperArm);var rs=Bone(HumanBodyBones.RightUpperArm);
   float width=ls&&rs?Vector3.Distance(ls.position,rs.position)*1.35f:height*.35f;width=Mathf.Max(width,height*.32f);
   bounds=new Bounds(new Vector3(hips.position.x,(top+bottom)*.5f,hips.position.z),new Vector3(width,top-bottom,height*.22f));return true;
  }
  public static Bounds GeometryBounds(Renderer renderer)
  {
   Bounds local;var skin=renderer as SkinnedMeshRenderer;
   if(skin&&skin.sharedMesh)
   {
    var mesh=new Mesh{hideFlags=HideFlags.HideAndDontSave};
    try{skin.BakeMesh(mesh);mesh.RecalculateBounds();local=mesh.bounds;}
    finally{UnityEngine.Object.DestroyImmediate(mesh);}
   }
   else {var filter=renderer.GetComponent<MeshFilter>();if(!filter||!filter.sharedMesh)return renderer.bounds;local=filter.sharedMesh.bounds;}
   bool first=true;Bounds world=default;
   for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
   {var point=renderer.transform.TransformPoint(local.center+Vector3.Scale(local.extents,new Vector3(x,y,z)));if(first){world=new Bounds(point,Vector3.zero);first=false;}else world.Encapsulate(point);}
   return world;
  }
  static byte[] Mask(Color32[] pixels,Color background)
  {
   var mask=new byte[pixels.Length];bool alpha=false;for(int i=0;i<pixels.Length;i++){mask[i]=pixels[i].a;if(mask[i]>8)alpha=true;}
   if(!alpha)
   {
    Color32 bg=background;
    for(int i=0;i<pixels.Length;i++)mask[i]=(byte)(Mathf.Max(Mathf.Abs(pixels[i].r-bg.r),Mathf.Abs(pixels[i].g-bg.g),Mathf.Abs(pixels[i].b-bg.b))>10?255:0);
   }
   return mask;
  }
  public static void FitCamera(Camera camera,Texture2D probe,SplitPhotoSettings settings)
  {
   int w=probe.width,h=probe.height;var mask=Mask(probe.GetPixels32(),settings.background);
   int minX=w,minY=h,maxX=-1,maxY=-1;
   for(int y=0;y<h;y++)for(int x=0;x<w;x++)if(mask[y*w+x]>16){minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}
   if(maxX<minX||maxY<minY)throw new InvalidOperationException("未拍到可见人物，请检查模型网格或材质。");
   float height=maxY-minY+1;float bottom=minY,top=maxY+1;
   if(settings.upperBody)
   {
    bottom=minY+height*.47f;int left=w,right=-1;
    for(int y=Mathf.Clamp(Mathf.FloorToInt(bottom),0,h-1);y<=maxY;y++)for(int x=minX;x<=maxX;x++)if(mask[y*w+x]>16){left=Math.Min(left,x);right=Math.Max(right,x);}
    if(right>=left){minX=left;maxX=right;}
   }
   float centerX=(minX+maxX+1)*.5f,centerY=(bottom+top)*.5f;
   float oldSize=camera.orthographicSize;
   camera.transform.position+=camera.transform.right*((centerX/w-.5f)*2*oldSize*camera.aspect)+camera.transform.up*((centerY/h-.5f)*2*oldSize)+Vector3.up*settings.verticalOffset;
   float ratio=Mathf.Max((top-bottom)/h,(maxX-minX+1f)/w);
   camera.orthographicSize=Mathf.Max(.0005f,oldSize*ratio*settings.padding);
  }
  public static void Composite(Texture2D texture,SplitPhotoSettings settings)
  {
   int w=texture.width,h=texture.height;var pixels=texture.GetPixels32();var mask=Mask(pixels,settings.background);
   Color captured=settings.background;
   for(int i=0;i<mask.Length;i++)if(mask[i]==0){captured=pixels[i];break;}
   int radius=settings.outlineEnabled?Mathf.Clamp(settings.outlineWidth,0,32):0;ushort[] distance=null;
   if(radius>0)
   {
    distance=new ushort[mask.Length];int cap=(radius+3)*3;
    for(int i=0;i<mask.Length;i++)distance[i]=(ushort)(mask[i]>32?0:cap);
    for(int y=0;y<h;y++)for(int x=0;x<w;x++)
    {int i=y*w+x,d=distance[i];if(x>0)d=Math.Min(d,distance[i-1]+3);if(y>0){d=Math.Min(d,distance[i-w]+3);if(x>0)d=Math.Min(d,distance[i-w-1]+4);if(x+1<w)d=Math.Min(d,distance[i-w+1]+4);}distance[i]=(ushort)d;}
    for(int y=h-1;y>=0;y--)for(int x=w-1;x>=0;x--)
    {int i=y*w+x,d=distance[i];if(x+1<w)d=Math.Min(d,distance[i+1]+3);if(y+1<h){d=Math.Min(d,distance[i+w]+3);if(x>0)d=Math.Min(d,distance[i+w-1]+4);if(x+1<w)d=Math.Min(d,distance[i+w+1]+4);}distance[i]=(ushort)d;}
   }
   for(int i=0;i<pixels.Length;i++)
   {
    float a=mask[i]/255f;float edge=distance==null?0:Mathf.Clamp01(radius+.5f-distance[i]/3f)*settings.outlineColor.a;
    Color under=Color.Lerp(settings.background,settings.outlineColor,edge);Color raw=pixels[i];float inverse=1-a;
    pixels[i]=new Color(Mathf.Clamp01(raw.r-captured.r*inverse+under.r*inverse),Mathf.Clamp01(raw.g-captured.g*inverse+under.g*inverse),Mathf.Clamp01(raw.b-captured.b*inverse+under.b*inverse),1);
   }
   texture.SetPixels32(pixels);texture.Apply(false,false);
  }
 }
 internal sealed class SplitPhotoStage : IDisposable
 {
  public Scene Scene {get;private set;}
  public readonly List<GameObject> Actors=new List<GameObject>();
  public readonly List<int> PoseFollowCounts=new List<int>();
  internal Vector3 CameraPosition;internal Quaternion CameraRotation;internal float CameraSize;
  readonly List<GameObject> cameraNodes=new List<GameObject>();
  internal Bounds ReferenceFrame;internal bool ReferenceIsSkeletal;
  public SplitPhotoStage(){Scene=EditorSceneManager.NewPreviewScene();}
  GameObject Node(string name){var go=new GameObject(name);SceneManager.MoveGameObjectToScene(go,Scene);go.hideFlags=HideFlags.HideAndDontSave;cameraNodes.Add(go);return go;}
  public SplitPhotoShot Capture(AvatarSplitPlan plan,SplitGroup group,Action<int,int> progress=null)
  {
   if(!SplitIds.Editing)throw new InvalidOperationException("请退出游戏模式后拍照。");
   var source=SplitIds.Resolve(plan.sourceId) as GameObject;if(!source)throw new InvalidOperationException("源 Avatar 已失效，请重新选择。");
   var ids=SplitPhoto.Selected(group);if(ids.Length==0)throw new InvalidOperationException(group.name+" 没有选择拍照衣服。");
   var s=plan.photoSettings;
   if(s.width<256||s.width>4096||s.height<256||s.height>4096||s.spacing<0||s.padding<.2f)throw new InvalidOperationException("尺寸需为 256～4096，间距不能为负，画面留白需大于 0.2。");
   bool any=false,allSkeletal=true;Bounds bounds=default;var originallyEnabled=new HashSet<Behaviour>();
   for(int i=0;i<ids.Length;i++)
   {
    progress?.Invoke(i,ids.Length);var outfit=SplitIds.Resolve(ids[i]) as GameObject;if(!outfit)throw new InvalidOperationException("拍照衣服引用失效，请刷新后重新选择。");
    var copy=AvatarSplitBuilder.CreateVariant(source,new[]{outfit},"Photo_"+i,"",Scene,out _,out _);Actors.Add(copy);
    copy.hideFlags=HideFlags.HideAndDontSave;copy.transform.rotation=Quaternion.identity;copy.transform.position=Vector3.zero;
    foreach(var behavior in copy.GetComponentsInChildren<Behaviour>(true))if(behavior){if(behavior.enabled)originallyEnabled.Add(behavior);behavior.enabled=false;}
    foreach(var particles in copy.GetComponentsInChildren<ParticleSystem>(true))particles.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
    var collection=AvatarSplitBuilder.Collection(copy);if(collection.PlaceholderOutfit)collection.PlaceholderOutfit.SetActive(false);
    var worn=collection.Entries[0].OutfitRoot;
    for(var node=worn.transform;node&&node!=copy.transform;node=node.parent)node.gameObject.SetActive(true);
    copy.SetActive(true);
    // Measure a canonical T pose before the chosen pose, excluding clothes,
    // hands and accessories from camera placement and scale.
    SplitPhotoPose.Apply(copy,new SplitPhotoSettings{posePreset=0});
    bool skeletal=SplitPhotoLook.SkeletonFrame(copy,s.upperBody,out var actorBounds);allSkeletal&=skeletal;
    PoseFollowCounts.Add(SplitPhotoPose.Apply(copy,s));
    var renderers=copy.GetComponentsInChildren<Renderer>(true).Where(r=>r&&r.enabled&&r.gameObject.activeInHierarchy&&(r is SkinnedMeshRenderer||r is MeshRenderer)).ToArray();
    if(renderers.Length==0)throw new InvalidOperationException("模型没有可见网格，无法拍摄。");
    foreach(var r in renderers)r.forceRenderingOff=false;
    if(!skeletal){actorBounds=SplitPhotoLook.GeometryBounds(renderers[0]);foreach(var r in renderers.Skip(1))actorBounds.Encapsulate(SplitPhotoLook.GeometryBounds(r));}
    // All variants share the source avatar's origin. Clothing geometry (heels, hems,
    // asymmetrical accessories) must not move the shared body or skeleton.
    copy.transform.position=new Vector3((i-(ids.Length-1)*.5f)*s.spacing,0,0);
    actorBounds.center+=copy.transform.position;
    if(!any){bounds=actorBounds;any=true;}else bounds.Encapsulate(actorBounds);
   }
   if(!any||bounds.size.y<.001f)throw new InvalidOperationException("模型边界无效，无法构图。");
   var physics=s.simulatePhysBones?SplitPhotoEditPhysics.Run(Actors,originallyEnabled,s.physicsSeconds):default;
   ReferenceFrame=bounds;ReferenceIsSkeletal=allSkeletal;var shot=RenderFrame(plan,group,bounds,allSkeletal);shot.physicsCount=physics.count;shot.physicsChanged=physics.changed;return shot;
  }
  internal SplitPhotoShot RenderFrame(AvatarSplitPlan plan,SplitGroup group,Bounds bounds,bool allSkeletal)
  {
   var s=plan.photoSettings;
   var camera=Node("PhotoCamera").AddComponent<Camera>();camera.enabled=false;camera.cameraType=CameraType.Preview;camera.scene=Scene;
   camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(s.background.r,s.background.g,s.background.b,0);camera.allowHDR=false;camera.orthographic=true;camera.aspect=(float)s.width/s.height;
   var frame=bounds; // Human avatars use a pose-independent skeletal reference.
   Quaternion rotation=Quaternion.Euler(s.pitch,180+s.yaw,0);Vector3 right=rotation*Vector3.right,up=rotation*Vector3.up;
   float halfWidth=0,halfHeight=0;
   for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
   {var offset=Vector3.Scale(frame.extents,new Vector3(x,y,z));halfWidth=Mathf.Max(halfWidth,Mathf.Abs(Vector3.Dot(offset,right)));halfHeight=Mathf.Max(halfHeight,Mathf.Abs(Vector3.Dot(offset,up)));}
   camera.orthographicSize=Mathf.Max(.05f,Mathf.Max(halfHeight,halfWidth/camera.aspect)*s.padding);
   float distance=Mathf.Max(3,bounds.size.magnitude*2);var target=frame.center+Vector3.up*(allSkeletal?s.verticalOffset:0);
   camera.transform.SetPositionAndRotation(target-(rotation*Vector3.forward)*distance,rotation);camera.nearClipPlane=.01f;camera.farClipPlane=distance*3+10;
   var key=Node("PhotoKey").AddComponent<Light>();key.type=LightType.Directional;key.intensity=1.25f;key.transform.rotation=Quaternion.Euler(35,-145,0);key.shadows=LightShadows.None;
   var fill=Node("PhotoFill").AddComponent<Light>();fill.type=LightType.Directional;fill.intensity=.6f;fill.transform.rotation=Quaternion.Euler(15,135,0);fill.shadows=LightShadows.None;
   var render=new RenderTexture(s.width,s.height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB){antiAliasing=4,hideFlags=HideFlags.HideAndDontSave};
   var old=RenderTexture.active;Texture2D texture=null;
   try
   {
    camera.targetTexture=render;camera.Render();RenderTexture.active=render;
    texture=new Texture2D(s.width,s.height,TextureFormat.RGBA32,false,false){hideFlags=HideFlags.HideAndDontSave,name=group.name};
    texture.ReadPixels(new Rect(0,0,s.width,s.height),0,0);texture.Apply(false,false);
    if(!allSkeletal)
    {
     SplitPhotoLook.FitCamera(camera,texture,s);
     camera.Render();RenderTexture.active=render;texture.ReadPixels(new Rect(0,0,s.width,s.height),0,0);texture.Apply(false,false);
    }
    CameraPosition=camera.transform.position;CameraRotation=camera.transform.rotation;CameraSize=camera.orthographicSize;
    SplitPhotoLook.Composite(texture,s);
    return new SplitPhotoShot{groupId=group.id,signature=SplitPhoto.Signature(plan,group),texture=texture};
   }
   catch{if(texture)Object.DestroyImmediate(texture);throw;}
   finally{RenderTexture.active=old;camera.targetTexture=null;render.Release();Object.DestroyImmediate(render);}
  }
  public void Dispose(){Actors.Clear();PoseFollowCounts.Clear();foreach(var node in cameraNodes)if(node)Object.DestroyImmediate(node);cameraNodes.Clear();if(Scene.IsValid())EditorSceneManager.ClosePreviewScene(Scene);Scene=default;}
 }
}
#endif
