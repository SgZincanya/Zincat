#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.Api;
using Object=UnityEngine.Object;
namespace Zincat.AvatarSplitTest
{
 [Serializable] internal sealed class CauExtraProfile
 {
  public string settingGuid,groupId,sourceKey,displayName,desiredName,imageGuid,imagePath,imageHash,expectedId,sourceId,avatarObjectId;
 }
 internal enum CauExtraStatus { AwaitingModel,Pending,Running,Done,Failed,Skipped }
 [Serializable] internal sealed class CauExtraJob
 {
  public string token,batch,settingGuid,displayName,desiredName,imagePath,imageHash,imageGuid,expectedId,sourceId,avatarId,userId,error,avatarObjectId;
  public bool modelSucceeded,metadataOnly,preserveName;
  public string sourceKey,groupId;
  public CauExtraStatus status;
 }
 [Serializable] internal sealed class CauExtraState
 {
  public List<CauExtraProfile> profiles=new List<CauExtraProfile>();
  public List<CauExtraJob> jobs=new List<CauExtraJob>();
 }
 internal sealed class CauExtraRemote {public string id,authorId,name;public bool pendingUpload;public object native;}
 internal interface ICauExtraBackend
 {
  string UserId{get;}
  Task<CauExtraRemote> Read(string id,CancellationToken token);
  Task<CauExtraRemote> Image(CauExtraRemote avatar,string path,CancellationToken token);
  Task<CauExtraRemote> Rename(CauExtraRemote avatar,string name,CancellationToken token);
 }
 // Metadata-only flow, authorized by either successful model upload or an explicit standalone request.
 internal static class CauExtraProcessor
 {
  public const string SnapshotFolder="Library/ZincatCauExtras/Snapshots";
  public static string Hash(string path)
  {using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();}
  public static string Snapshot(CauExtraJob job)
  {
   if(string.IsNullOrEmpty(job.imagePath))return null;
   string path=Path.GetFullPath(job.imagePath),folder=Path.GetFullPath(SnapshotFolder)+Path.DirectorySeparatorChar;
   if(!path.StartsWith(folder,StringComparison.OrdinalIgnoreCase)||!File.Exists(path)||string.IsNullOrEmpty(job.imageHash)||Hash(path)!=job.imageHash)
    throw new InvalidOperationException("上传封面快照缺失或已变化，请重新确认封面并交接。");
   return path;
  }
  static void Ownership(CauExtraJob job,CauExtraRemote remote,string user)
  {
   if(remote==null||remote.id!=job.avatarId||remote.authorId!=user)throw new InvalidOperationException("蓝图或所属账号不匹配，未继续更新。");
   if(job.metadataOnly&&remote.pendingUpload)throw new InvalidOperationException("此蓝图尚未完成首次模型上传，请先上传模型。");
  }
  public static async Task Apply(CauExtraJob job,ICauExtraBackend api,CancellationToken token)
  {
   token.ThrowIfCancellationRequested();
   if(!job.modelSucceeded&&!job.metadataOnly)throw new InvalidOperationException("模型尚未确认上传成功，不能同步封面和名称。");
   if(job.metadataOnly&&string.IsNullOrEmpty(job.expectedId))throw new InvalidOperationException("独立更新必须指定已有蓝图。");
   string avatarId=job.avatarId??"";
   if(!avatarId.StartsWith("avtr_",StringComparison.Ordinal)||!Guid.TryParseExact(avatarId.Substring(5),"D",out _))throw new InvalidOperationException("蓝图号无效。");
   if(!string.IsNullOrEmpty(job.expectedId)&&job.expectedId!=job.avatarId)throw new InvalidOperationException("上传蓝图与交接记录不一致。");
   if(job.sourceId==job.avatarId&&job.expectedId!=job.avatarId)throw new InvalidOperationException("检测到源模型蓝图，未继续更新。");
   string user=api.UserId;
   if(string.IsNullOrEmpty(user)||string.IsNullOrEmpty(job.userId)||user!=job.userId)throw new InvalidOperationException("请登录本次模型上传使用的账号，再重试。");
   if(!job.preserveName&&string.IsNullOrWhiteSpace(job.desiredName))throw new InvalidOperationException("上传名称为空，请重新交接。");
   string image=Snapshot(job);
   var remote=await api.Read(job.avatarId,token);Ownership(job,remote,user);if(api.UserId!=user)throw new InvalidOperationException("登录账号已切换，请重试。");token.ThrowIfCancellationRequested();
   if(image!=null){remote=await api.Image(remote,image,token);Ownership(job,remote,user);if(api.UserId!=user)throw new InvalidOperationException("登录账号已切换，请重试。");}
   if(!job.preserveName)
   {
    // After uploading an image, fetch current metadata before changing its name.
    if(image!=null)remote=await api.Read(job.avatarId,token);
    Ownership(job,remote,user);if(api.UserId!=user)throw new InvalidOperationException("登录账号已切换，请重试。");token.ThrowIfCancellationRequested();
    if(remote.name!=job.desiredName.Trim())await api.Rename(remote,job.desiredName.Trim(),token);
   }
  }
 }
 internal sealed class CauSdkExtraBackend:ICauExtraBackend
 {
  public string UserId=>APIUser.CurrentUser?.id;
  static CauExtraRemote Wrap(VRCAvatar avatar)=>new CauExtraRemote{id=avatar.ID,authorId=avatar.AuthorId,name=avatar.Name,pendingUpload=avatar.PendingUpload,native=avatar};
  public async Task<CauExtraRemote> Read(string id,CancellationToken token)=>Wrap(await VRCApi.GetAvatar(id,forceRefresh:true,cancellationToken:token));
  public async Task<CauExtraRemote> Image(CauExtraRemote avatar,string path,CancellationToken token)=>Wrap(await VRCApi.UpdateAvatarImage(avatar.id,(VRCAvatar)avatar.native,path,cancellationToken:token));
  public async Task<CauExtraRemote> Rename(CauExtraRemote avatar,string name,CancellationToken token)
  {var data=(VRCAvatar)avatar.native;data.Name=name;return Wrap(await VRCApi.UpdateAvatarInfo(avatar.id,data,cancellationToken:token));}
 }
 [InitializeOnLoad] internal static class CauExtraUploads
 {
  const string Journal="Library/ZincatCauExtras/queue.json";
  const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
  static CauExtraState cache;
  static bool hooked,running,stopping;
  static CancellationTokenSource cancellation;
  static readonly List<Delegate> hooks=new List<Delegate>();
  public static int Version{get;private set;}
  public static bool Busy=>running;
  internal static CauExtraState State
  {
   get
   {
    if(cache!=null)return cache;
    if(File.Exists(Journal))
    {
     try{cache=JsonUtility.FromJson<CauExtraState>(File.ReadAllText(Journal));}
     catch(Exception e){File.Copy(Journal,Journal+".broken-"+DateTime.Now.ToString("yyyyMMddHHmmss"),true);Debug.LogError("[封面与名称] 本地记录读取失败，已保留副本："+e.Message);}
    }
    if(cache==null)cache=new CauExtraState();
    foreach(var job in cache.jobs.Where(j=>j.status==CauExtraStatus.Running))job.status=(job.modelSucceeded||job.metadataOnly)?CauExtraStatus.Pending:CauExtraStatus.AwaitingModel;
    return cache;
   }
  }
  static CauExtraUploads()
  {
   EditorApplication.delayCall+=Attach;
   EditorApplication.update+=Tick;
   AssemblyReloadEvents.beforeAssemblyReload+=()=>{stopping=true;cancellation?.Cancel();};
   EditorApplication.quitting+=()=>{stopping=true;cancellation?.Cancel();};
   EditorApplication.playModeStateChanged+=s=>{if(s==PlayModeStateChange.ExitingEditMode){cancellation?.Cancel();}};
  }
  static object Field(object value,string name)=>value?.GetType().GetField(name,Flags)?.GetValue(value);
  static string GuidOf(object value)=>value is Object obj?AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(obj)):"";
  static string Batch(object progress)=>GuidOf(progress);
  static string Token(object progress,object setting)=>Batch(progress)+":"+GuidOf(setting)+":"+Field(progress,"uploadingTargetPlatform");
  static void ReplaceLocalFile(string temp,string target)
  {
   for(int attempt=0;;attempt++)
   {
    try{AssetDatabase.ReleaseCachedFileHandles();if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);return;}
    catch(IOException) when(attempt<5){Thread.Sleep(15*(1<<attempt));}
   }
  }
  static void Save()
  {
   Directory.CreateDirectory(Path.GetDirectoryName(Journal));string temp=Journal+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{File.WriteAllText(temp,JsonUtility.ToJson(State,true));ReplaceLocalFile(temp,Journal);Version++;}
   finally{if(File.Exists(temp))File.Delete(temp);}
  }
  internal static void ValidateApi()
  {
   var type=CauBridge.Type("UploadOrchestrator");
   foreach(string name in new[]{"OnUploadSingleAvatarStarted","OnUploadSingleAvatarFinished","OnUploadFinished"})
   {
    var ev=type?.GetEvent(name,Flags);
    if(ev?.EventHandlerType.GetMethod("Invoke")?.GetParameters().Length!=2)throw new InvalidOperationException("CAU 扩展事件不匹配："+name);
   }
   if(CauBridge.Type("Preferences")?.GetProperty("TakeThumbnailInPlaymodeByDefault",Flags)?.CanWrite!=true)throw new InvalidOperationException("CAU 编辑模式拍照设置不可用。");
   Attach();if(!hooked)throw new InvalidOperationException("CAU 附加同步事件无法连接。");
  }
  static void Attach()
  {
   if(hooked||!CauBridge.Available)return;
   var type=CauBridge.Type("UploadOrchestrator");
   var names=new[]{"OnUploadSingleAvatarStarted","OnUploadSingleAvatarFinished","OnUploadFinished"};
   var methods=new[]{nameof(Started),nameof(Finished),nameof(BatchFinished)};
   if(names.Any(n=>type?.GetEvent(n,Flags)?.EventHandlerType.GetMethod("Invoke")?.GetParameters().Length!=2))return;
   for(int i=0;i<names.Length;i++)
   {
    var ev=type.GetEvent(names[i],Flags);var args=ev.EventHandlerType.GetMethod("Invoke").GetParameters().Select(p=>Expression.Parameter(p.ParameterType,p.Name)).ToArray();
    var call=Expression.Call(typeof(CauExtraUploads).GetMethod(methods[i],Flags),args.Select(a=>(Expression)Expression.Convert(a,typeof(object))).ToArray());
    var handler=Expression.Lambda(ev.EventHandlerType,call,args).Compile();ev.AddEventHandler(null,handler);hooks.Add(handler);
   }
   hooked=true;
  }
  public static void Prepare(AvatarSplitPlan plan,SplitGroup group,Object setting,Texture2D cover)
  {
   ValidateApi();if(Busy||CauBridge.Busy)throw new InvalidOperationException("请等待当前上传或附加同步完成。");
   var profile=new CauExtraProfile{settingGuid=GuidOf(setting),groupId=group.id,sourceKey=SplitConfigStore.Key(plan),displayName=group.name,desiredName=group.UploadName,expectedId=group.blueprintId??""};
   if(string.IsNullOrEmpty(profile.settingGuid))throw new InvalidOperationException("CAU 配置尚未保存。");
   var avatarReference=Field(setting,"avatarDescriptor");
   var avatarDescriptor=avatarReference?.GetType().GetMethod("TryResolve",Flags)?.Invoke(avatarReference,null) as VRCAvatarDescriptor;
   if(!avatarDescriptor)throw new InvalidOperationException("无法确认 CAU 配置绑定的模型。");
   profile.avatarObjectId=SplitIds.Of(avatarDescriptor);
   var source=SplitIds.Resolve(plan.sourceId) as GameObject;profile.sourceId=source?source.GetComponent<PipelineManager>()?.blueprintId:"";
   if(cover){profile.imageGuid=AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(cover));profile.imageHash=group.photoCoverHash;profile.imagePath=FreezeImage(cover,profile.imageHash);}
   State.profiles.RemoveAll(p=>p.settingGuid==profile.settingGuid);State.profiles.Add(profile);Save();
  }
  static string FreezeImage(Texture2D cover,string expectedHash)
  {
   string path=AssetDatabase.GetAssetPath(cover),hash=SplitPhoto.CoverHash(path);
   if(string.IsNullOrEmpty(expectedHash)||hash!=expectedHash)throw new InvalidOperationException("封面文件已变化，请重新确认并发送给上传。");
   Directory.CreateDirectory(CauExtraProcessor.SnapshotFolder);string frozen=Path.Combine(CauExtraProcessor.SnapshotFolder,hash+".png");
   if(!File.Exists(frozen)||CauExtraProcessor.Hash(frozen)!=hash)
   {
    string temp=frozen+"."+Guid.NewGuid().ToString("N")+".tmp";
    try{File.Copy(path,temp);ReplaceLocalFile(temp,frozen);}
    finally{if(File.Exists(temp))File.Delete(temp);}
   }
   return frozen;
  }
  internal static bool HasBlueprint(SplitGroup group)
  {
   string id=(group?.blueprintId??"").Trim().ToLowerInvariant();return id.StartsWith("avtr_",StringComparison.Ordinal)&&Guid.TryParseExact(id.Substring(5),"D",out _);
  }
  internal static List<CauExtraJob> BuildDirect(AvatarSplitPlan plan,IEnumerable<SplitGroup> selected,bool updateName,bool updateCover)
  {
   if(!SplitIds.Editing||Busy||CauBridge.Busy)throw new InvalidOperationException("请退出运行模式，并等待当前上传或更新结束。");
   if(!plan||(!updateName&&!updateCover))throw new InvalidOperationException("请选择要更新的内容。");
   string user=APIUser.CurrentUser?.id;if(string.IsNullOrEmpty(user))throw new InvalidOperationException("请先在 VRChat SDK 面板登录账号。");
   var groups=selected?.Distinct().ToArray()??Array.Empty<SplitGroup>();if(groups.Length==0)throw new InvalidOperationException("请勾选需要更新的版本。");
   if(groups.Any(g=>g==null||!plan.groups.Contains(g)))throw new InvalidOperationException("更新分组不属于当前 Avatar 配置。");
   foreach(var group in groups)if(!HasBlueprint(group))throw new InvalidOperationException(group.name+" 没有有效的已有蓝图，请先完成首次模型上传或填写蓝图号。");
   if(groups.GroupBy(g=>g.blueprintId.Trim(),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidOperationException("所选版本的蓝图号重复，请先检查。");
   string key=SplitConfigStore.Key(plan);if(string.IsNullOrEmpty(key))throw new InvalidOperationException("请先保存 Avatar 分组配置。");
   var source=SplitIds.Resolve(plan.sourceId) as GameObject;string sourceId=source?source.GetComponent<PipelineManager>()?.blueprintId:"";
   var jobs=new List<CauExtraJob>();
   foreach(var group in groups)
   {
    string id=group.blueprintId.Trim().ToLowerInvariant();
    var job=new CauExtraJob{token="direct:"+Guid.NewGuid().ToString("N"),batch="",settingGuid=group.cauSettingGuid,sourceKey=key,groupId=group.id,displayName=group.name,
     desiredName=group.UploadName,preserveName=!updateName,metadataOnly=true,modelSucceeded=false,avatarId=id,expectedId=id,sourceId=sourceId,userId=user,status=CauExtraStatus.Pending};
    if(updateName&&string.IsNullOrWhiteSpace(job.desiredName))throw new InvalidOperationException(group.name+" 的 VRC 模型名称为空。");
    if(updateCover)
    {
     var cover=SplitPhoto.UploadCover(plan,group);if(!cover)throw new InvalidOperationException(group.name+" 尚未发送有效封面，请先在拍照窗口发送给上传。");
     job.imageGuid=AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(cover));job.imageHash=group.photoCoverHash;job.imagePath=FreezeImage(cover,job.imageHash);
    }
    jobs.Add(job);
   }
   return jobs;
  }
  public static int QueueDirect(AvatarSplitPlan plan,IEnumerable<SplitGroup> selected,bool updateName,bool updateCover)
  {
   var jobs=BuildDirect(plan,selected,updateName,updateCover);
   if(State.jobs.Any(j=>(j.status==CauExtraStatus.Pending||j.status==CauExtraStatus.Running)&&jobs.Any(n=>n.avatarId==j.avatarId)))throw new InvalidOperationException("其中有蓝图正在等待同步，请完成当前任务后再更新。");
   string changes=updateName&&updateCover?"名称和封面":updateName?"名称":"封面";
   string preview=string.Join("\n\n",jobs.Select(j=>j.displayName+"\n"+j.avatarId+(updateName?"\n名称："+j.desiredName:"")+(updateCover?"\n封面：已确认的上传照片":"")));
   if(!EditorUtility.DisplayDialog("仅更新"+changes,"将更新以下已有 Avatar 的"+changes+"：\n\n"+preview+"\n\n不会构建或上传模型，也不会新建蓝图。","开始更新","取消"))return 0;
   State.jobs.AddRange(jobs);Save();return jobs.Count;
  }
  public static CauExtraJob LatestJob(AvatarSplitPlan plan,SplitGroup group)
  {
   if(!plan||group==null)return null;string key=SplitConfigStore.Key(plan);
   return State.jobs.LastOrDefault(j=>(!string.IsNullOrEmpty(key)&&j.sourceKey==key&&j.groupId==group.id)||(!string.IsNullOrEmpty(group.cauSettingGuid)&&j.settingGuid==group.cauSettingGuid));
  }
  static void Started(object progress,object setting)
  {
   if(running){cancellation?.Cancel();}
   var profile=State.profiles.FirstOrDefault(p=>p.settingGuid==GuidOf(setting));if(profile==null)return;
   string token=Token(progress,setting);if(State.jobs.Any(j=>j.token==token))return;
   State.jobs.Add(new CauExtraJob{token=token,batch=Batch(progress),settingGuid=profile.settingGuid,displayName=profile.displayName,desiredName=profile.desiredName,
    sourceKey=profile.sourceKey,groupId=profile.groupId,imagePath=profile.imagePath,imageHash=profile.imageHash,imageGuid=profile.imageGuid,expectedId=profile.expectedId,sourceId=profile.sourceId,avatarObjectId=profile.avatarObjectId,userId=APIUser.CurrentUser?.id,status=CauExtraStatus.AwaitingModel});Save();
  }
  static bool HasError(object progress,object setting)
  {
   if(Field(progress,"uploadErrors") is IEnumerable errors)foreach(var error in errors)if(Equals(Field(error,"uploadingAvatar"),setting))return true;
   return false;
  }
  static void Finished(object progress,object setting)
  {
   var job=State.jobs.FirstOrDefault(j=>j.token==Token(progress,setting));if(job==null)return;
   if(HasError(progress,setting)){job.status=CauExtraStatus.Failed;job.error="模型上传有错误，未同步封面与名称。";Save();return;}
   var reference=Field(setting,"avatarDescriptor");
   var descriptor=reference?.GetType().GetMethod("TryResolve",Flags)?.Invoke(reference,null) as VRCAvatarDescriptor;
   if(!descriptor||string.IsNullOrEmpty(job.avatarObjectId)||SplitIds.Of(descriptor)!=job.avatarObjectId)
   {job.status=CauExtraStatus.Failed;job.error="CAU 绑定模型已变化，请从拆分窗口重新交接。";Save();return;}
   string id=descriptor.GetComponent<PipelineManager>()?.blueprintId;
   if(string.IsNullOrEmpty(id)){job.status=CauExtraStatus.Failed;job.error="无法确认上传结果的蓝图号，未同步。";Save();return;}
   job.avatarId=id;job.modelSucceeded=true;job.status=CauExtraStatus.Pending;job.error="";Save();
  }
  static void BatchFinished(object progress,object success)
  {
   foreach(var job in State.jobs.Where(j=>j.batch==Batch(progress)&&j.status==CauExtraStatus.AwaitingModel))
   {job.status=CauExtraStatus.Failed;job.error="CAU 未确认此模型上传成功，请完成模型上传后重试。";}
   Save();
  }
  static void Tick()
  {
   if(stopping||running||EditorApplication.isCompiling||EditorApplication.isUpdating||!SplitIds.Editing||CauBridge.Busy)return;
   if(cache==null&&!File.Exists(Journal))return;
   var next=State.jobs.FirstOrDefault(j=>j.status==CauExtraStatus.Pending&&(j.modelSucceeded||j.metadataOnly));if(next!=null)Pump(next);
  }
  static async void Pump(CauExtraJob job)
  {
   running=true;cancellation=new CancellationTokenSource();
   try
   {
    job.status=CauExtraStatus.Running;Save();
    await CauExtraProcessor.Apply(job,new CauSdkExtraBackend(),cancellation.Token);
    job.status=CauExtraStatus.Done;job.error="";
   }
   catch(OperationCanceledException){job.status=CauExtraStatus.Pending;job.error="同步已暂停，编辑模式空闲后继续。";}
   catch(Exception e){job.status=CauExtraStatus.Failed;job.error=e.Message;Debug.LogError("[封面与名称] "+job.displayName+"："+e.Message);}
   finally
   {
    running=false;cancellation.Dispose();cancellation=null;
    if(!stopping)try{Save();}catch(Exception e){Debug.LogException(e);}
   }
  }
  public static void Retry(CauExtraJob job)
  {
   if(Busy||CauBridge.Busy)throw new InvalidOperationException("请等待当前任务完成。");
   if(!job.modelSucceeded&&!job.metadataOnly)throw new InvalidOperationException("请先通过 CAU 完成此模型上传。");
   job.status=CauExtraStatus.Pending;job.error="";Save();
  }
  public static void Skip(CauExtraJob job){if(job.status==CauExtraStatus.Running)return;job.status=CauExtraStatus.Skipped;Save();}
  public static void ClearFinished(){State.jobs.RemoveAll(j=>j.status==CauExtraStatus.Done||j.status==CauExtraStatus.Skipped);Save();}
  public static string Status(CauExtraJob job)
  {
   switch(job.status){case CauExtraStatus.AwaitingModel:return "等待模型上传";case CauExtraStatus.Pending:return "等待同步";case CauExtraStatus.Running:return "正在更新"+(job.preserveName?"封面":string.IsNullOrEmpty(job.imagePath)?"名称":"封面与名称");case CauExtraStatus.Done:return job.metadataOnly?"独立更新已完成":"已完成";case CauExtraStatus.Skipped:return "已跳过";default:return "未完成 · "+job.error;}
  }
 }
}
#endif
