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
  public bool preserveExistingName;
  public CauModelBaseline baseline;
  public CauQueueItem queueItem;
 }
 internal enum CauExtraStatus { AwaitingModel,Pending,Running,Done,Failed,Skipped }
 [Serializable] internal sealed class CauExtraJob
 {
  public string token,batch,settingGuid,displayName,desiredName,imagePath,imageHash,imageGuid,expectedId,sourceId,avatarId,userId,error,avatarObjectId;
  public bool modelSucceeded,metadataOnly,preserveName,localMismatch,imageUnchanged,nameApplied,imageApplied;
  public string sourceKey,groupId;
  public CauModelBaseline baseline;
  public CauQueueItem queueItem;
  public CauVerificationStatus verification;
  public string verificationMessage,uploadPlatform,startedAt,checkedAt,appliedImageUrl,appliedImageMd5;
  public int remoteVersion;
  public CauExtraStatus status;
 }
 [Serializable] internal sealed class CauExtraState
 {
  public List<CauExtraProfile> profiles=new List<CauExtraProfile>();
  public List<CauExtraJob> jobs=new List<CauExtraJob>();
 }
 internal sealed class CauExtraRemote {public string id,authorId,name,assetUrl,imageUrl,thumbnailUrl,imageMd5;public int version,assetVersion;public string modelUpdatedAt,metadataUpdatedAt,modelTimeError;public bool pendingUpload,imageUnchanged;public object native;}
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
   job.imageApplied = job.nameApplied = false;
   if (image != null)
   {
    var uploaded = await api.Image(remote,image,token);
    Ownership(job,uploaded,user);
    if (api.UserId != user) throw new InvalidOperationException("登录账号已切换，请重试。");
    job.appliedImageUrl = uploaded.imageUrl;
    job.appliedImageMd5 = uploaded.imageMd5;
    job.imageUnchanged = uploaded.imageUnchanged;
    remote = await api.Read(job.avatarId,token);
    Ownership(job,remote,user);
    if (api.UserId != user) throw new InvalidOperationException("登录账号已切换，请重试。");
    if (string.IsNullOrEmpty(job.appliedImageUrl) || remote.imageUrl != job.appliedImageUrl)
     throw new InvalidOperationException("封面更新后未能确认线上结果，请刷新线上信息。");
    job.imageApplied = true;
   }
   if (!job.preserveName)
   {
    if (remote.name != job.desiredName.Trim()) await api.Rename(remote,job.desiredName.Trim(),token);
    var confirmed = await api.Read(job.avatarId,token);
    Ownership(job,confirmed,user);
    if (api.UserId != user) throw new InvalidOperationException("登录账号已切换，请重试。");
    if (confirmed.name != job.desiredName.Trim()) throw new InvalidOperationException("名称更新后未能确认线上结果，请刷新线上信息。");
    job.nameApplied = true;
   }
  }
 }
 internal sealed class CauSdkExtraBackend:ICauServerDetailsBackend
 {
  public string UserId=>APIUser.CurrentUser?.id;
  static CauExtraRemote Wrap(VRCAvatar avatar)=>new CauExtraRemote{id=avatar.ID,authorId=avatar.AuthorId,name=avatar.Name,pendingUpload=avatar.PendingUpload,assetUrl=avatar.GetLatestAssetUrlForPlatform("standalonewindows"),imageUrl=avatar.ImageUrl,thumbnailUrl=avatar.ThumbnailImageUrl,version=avatar.Version,metadataUpdatedAt=avatar.UpdatedAt==default?null:avatar.UpdatedAt.ToUniversalTime().ToString("o"),native=avatar};
  public async Task<CauExtraRemote> Read(string id,CancellationToken token)=>Wrap(await VRCApi.GetAvatar(id,forceRefresh:true,cancellationToken:token));
  public async Task<CauExtraRemote> Details(string id,CancellationToken token)
  {
   var remote=await Read(id,token);
   string user = UserId;
   if (remote.id != id || remote.authorId != user || string.IsNullOrEmpty(user))
    throw new InvalidOperationException("模型不属于当前账号。");
   if (CauServerQueries.TryModelFile(remote.imageUrl, out string imageFileId, out int imageVersion))
   {
    try
    {
     var file = await VRCApi.Get<VRCFile>("file/" + imageFileId, forceRefresh:true, cancellationToken:token);
     if (UserId == user && file.ID == imageFileId && file.OwnerId == user)
     {
      var imageEntry = file.Versions?.FirstOrDefault(v => v != null && v.Version == imageVersion &&
       !v.Deleted && v.Status == "complete" && v.File?.Status == "complete");
      remote.imageMd5 = imageEntry?.File?.MD5;
     }
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception) { /* Cover comparison remains unknown until the file can be read. */ }
   }
   if(!CauServerQueries.TryModelFile(remote.assetUrl,out var fileId,out int version))
   {remote.modelTimeError="服务器尚未提供可查询的 Windows 模型文件。";return remote;}
   remote.assetVersion=version;
   try
   {
    // Read the raw created_at key: some SDK versions omit its JsonProperty mapping.
    var file=await VRCApi.Get<Newtonsoft.Json.Linq.JObject>("file/"+fileId,forceRefresh:true,cancellationToken:token);
    if(file["id"]?.ToString()!=fileId||file["ownerId"]?.ToString()!=remote.authorId)throw new InvalidOperationException("模型文件所属账号不匹配。");
    var entries=file["versions"] as Newtonsoft.Json.Linq.JArray;
    var entry=entries?.OfType<Newtonsoft.Json.Linq.JObject>().FirstOrDefault(v=>int.TryParse(v["version"]?.ToString(),out int n)&&n==version&&!string.Equals(v["deleted"]?.ToString(),"true",StringComparison.OrdinalIgnoreCase));
    string created=(entry?["created_at"]??entry?["createdAt"])?.ToString();
    if(entry==null||entry["status"]?.ToString()!="complete"||!DateTimeOffset.TryParse(created,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out var timestamp))remote.modelTimeError="服务器未提供当前模型文件版本的时间。";
    else remote.modelUpdatedAt=timestamp.UtcDateTime.ToString("o");
   }
   catch(OperationCanceledException){throw;}
   catch(Exception e){remote.modelTimeError="模型文件时间查询未完成："+e.Message;}
   return remote;
  }
  async Task<CauExtraRemote> ReuseImage(CauExtraRemote avatar,string path,CancellationToken token)
  {
   if(!CauServerQueries.TryModelFile(avatar.imageUrl,out string fileId,out int currentVersion))return null;
   string user=UserId;var file=await VRCApi.Get<VRCFile>("file/"+fileId,forceRefresh:true,cancellationToken:token);
   if(UserId!=user||file.ID!=fileId||file.OwnerId!=user||avatar.authorId!=user)throw new InvalidOperationException("封面文件所属账号不匹配。");
   string md5 = CauCoverPreparation.FileMd5(path);
   var versions=file.Versions?.Where(v=>v!=null).Select(v=>new CauImageVersion{version=v.Version,deleted=v.Deleted,status=v.Status,fileStatus=v.File?.Status,url=v.File?.URL,md5=v.File?.MD5,size=v.File?.SizeInBytes??0});
   var reusable=CauImagePolicy.Reusable(versions,fileId,currentVersion,md5,new FileInfo(path).Length);
   if(reusable==null)return null;
   token.ThrowIfCancellationRequested();
   if(reusable.version==currentVersion){avatar.imageUnchanged=true;avatar.imageMd5=md5;return avatar;}
   // A previous attempt may have uploaded the image but failed its final avatar PUT.
   // Link that verified completed image version; never upload its bytes again.
   if(UserId!=user)throw new InvalidOperationException("登录账号已变化，未绑定封面。");
   var linked=await VRCApi.Put<object,VRCAvatar>("avatars/"+avatar.id,new Dictionary<string,string>{{"imageUrl",reusable.url}},cancellationToken:token);
   var result = Wrap(linked); result.imageMd5 = md5; return result;
  }
  public async Task<CauExtraRemote> Image(CauExtraRemote avatar,string path,CancellationToken token)
  {
   string user=UserId,prepared=CauCoverPreparation.Prepare(path);
   try
   {
    for(int attempt=0;;attempt++)
    {
     try
     {
      token.ThrowIfCancellationRequested();var current=await Read(avatar.id,token);
      if(UserId!=user||current.authorId!=user||current.id!=avatar.id)throw new InvalidOperationException("封面更新账号或蓝图发生变化。");
      var reuse=await ReuseImage(current,prepared,token);if(reuse!=null)return reuse;
      if(UserId!=user)throw new InvalidOperationException("登录账号已变化，未更新封面。");
      try
      {
       var result = Wrap(await VRCApi.UpdateAvatarImage(current.id,(VRCAvatar)current.native,prepared,cancellationToken:token));
       result.imageMd5 = CauCoverPreparation.FileMd5(prepared);
       return result;
      }
      catch(Exception e) when(CauImagePolicy.Duplicate(e))
      {
       var confirmed=await Read(current.id,token);if(UserId!=user||confirmed.authorId!=user)throw new InvalidOperationException("封面更新账号发生变化。");
       reuse=await ReuseImage(confirmed,prepared,token);if(reuse!=null)return reuse;
       throw new InvalidOperationException("服务器报告重复封面，但尚未确认文件完整或绑定；未标记成功，请稍后重试。",e);
      }
     }
     catch(Exception e) when(!token.IsCancellationRequested&&CauImagePolicy.Transient(e))
     {
      if(attempt>=2)throw new InvalidOperationException(CauImagePolicy.Explain(e),e);
      await Task.Delay(1000*(attempt+1),token);
     }
    }
   }
   finally{if(File.Exists(prepared))File.Delete(prepared);}
  }
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
  internal static void ReplaceLocalFile(string temp,string target)
  {
   for(int attempt=0;;attempt++)
   {
    try{AssetDatabase.ReleaseCachedFileHandles();if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);return;}
    catch(IOException) when(attempt<5){Thread.Sleep(15*(1<<attempt));}
   }
  }
  internal static bool HasJournal=>cache!=null||File.Exists(Journal);
  internal static void Save()
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
   var profile=new CauExtraProfile{settingGuid=GuidOf(setting),groupId=group.id,sourceKey=SplitConfigStore.Key(plan),displayName=group.name,desiredName=group.UploadName,expectedId=group.blueprintId??"",preserveExistingName=!string.IsNullOrEmpty(group.blueprintId)};
   if(string.IsNullOrEmpty(profile.settingGuid))throw new InvalidOperationException("CAU 配置尚未保存。");
   var avatarReference=Field(setting,"avatarDescriptor");
   var avatarDescriptor=avatarReference?.GetType().GetMethod("TryResolve",Flags)?.Invoke(avatarReference,null) as VRCAvatarDescriptor;
   if(!avatarDescriptor)throw new InvalidOperationException("无法确认 CAU 配置绑定的模型。");
   profile.avatarObjectId=SplitIds.Of(avatarDescriptor);
   var source=SplitIds.Resolve(plan.sourceId) as GameObject;profile.sourceId=source?source.GetComponent<PipelineManager>()?.blueprintId:"";
   if(cover){profile.imageGuid=AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(cover));profile.imageHash=group.photoCoverHash;profile.imagePath=FreezeImage(cover,profile.imageHash);}
   profile.queueItem=CauQueueItem.Capture(plan,group);
   CauUploadVerification.Bind(profile, APIUser.CurrentUser?.id);
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

   string changes=updateName&&updateCover?"名称和封面":updateName?"名称":"封面";
   string preview=string.Join("\n\n",jobs.Select(j=>j.displayName+"\n"+j.avatarId+(updateName?"\n名称："+j.desiredName:"")+(updateCover?"\n封面：已确认的上传照片":"")));
   if(!EditorUtility.DisplayDialog("仅更新"+changes,"将更新以下已有 Avatar 的"+changes+"：\n\n"+preview+"\n\n不会构建或上传模型，也不会新建蓝图。","开始更新","取消"))return 0;
   return QueueReviewed(jobs);
  }
  internal static int QueueReviewed(List<CauExtraJob> jobs)
  {
   if (!SplitIds.Editing || Busy || CauBridge.Busy || CauServerQueries.Busy) throw new InvalidOperationException("请等待当前任务结束。");
   if (jobs.GroupBy(j=>j.avatarId,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))
    throw new InvalidOperationException("同一个线上模型只能在本次更新中出现一次。");
   if (jobs.Any(j=>j.userId!=APIUser.CurrentUser?.id) ||
       State.jobs.Any(j=>(j.status==CauExtraStatus.Pending||j.status==CauExtraStatus.Running)&&jobs.Any(n=>n.avatarId==j.avatarId)))
    throw new InvalidOperationException("账号已切换，或所选模型还有更新正在进行。");
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
    sourceKey=profile.sourceKey,groupId=profile.groupId,preserveName=profile.preserveExistingName,imagePath=profile.imagePath,imageHash=profile.imageHash,imageGuid=profile.imageGuid,expectedId=profile.expectedId,sourceId=profile.sourceId,avatarObjectId=profile.avatarObjectId,userId=APIUser.CurrentUser?.id,status=CauExtraStatus.AwaitingModel});
   var startedJob=State.jobs.Last();startedJob.queueItem=profile.queueItem;
   CauUploadVerification.Started(startedJob,profile,Field(progress,"uploadingTargetPlatform")?.ToString());
   if(profile.queueItem!=null)try{profile.queueItem.Validate();}catch(Exception e){startedJob.localMismatch=true;startedJob.error="交接后模型配置发生变化："+e.Message;}
   Save();
  }
  static string UploadError(object progress, string settingGuid, string platform)
  {
   if (Field(progress,"uploadErrors") is IEnumerable errors)
    foreach (var error in errors)
     if (GuidOf(Field(error,"uploadingAvatar")) == settingGuid &&
         Field(error,"targetPlatform")?.ToString() == platform)
      return Field(error,"message") as string ?? "CAU 报告该模型上传失败。";
   return null;
  }
  static void Finished(object progress,object setting)
  {
   var job=State.jobs.FirstOrDefault(j=>j.token==Token(progress,setting));if(job==null)return;
   if(job.localMismatch){job.status=CauExtraStatus.Failed;CauUploadVerification.ModelFailed(job);Save();return;}
   var uploadError=UploadError(progress,job.settingGuid,job.uploadPlatform);
   if(uploadError!=null){job.status=CauExtraStatus.Failed;job.error=uploadError;CauUploadVerification.ModelFailed(job);Save();return;}
   var reference=Field(setting,"avatarDescriptor");
   var descriptor=reference?.GetType().GetMethod("TryResolve",Flags)?.Invoke(reference,null) as VRCAvatarDescriptor;
   if(!descriptor||string.IsNullOrEmpty(job.avatarObjectId)||SplitIds.Of(descriptor)!=job.avatarObjectId)
   {job.status=CauExtraStatus.Failed;job.error="CAU 绑定模型已变化，请从拆分窗口重新交接。";CauUploadVerification.ModelFailed(job);Save();return;}
   string id=descriptor.GetComponent<PipelineManager>()?.blueprintId;
   if(string.IsNullOrEmpty(id)){job.status=CauExtraStatus.Failed;job.error="无法确认上传结果的蓝图号，未同步。";CauUploadVerification.ModelFailed(job);Save();return;}
   if((!string.IsNullOrEmpty(job.expectedId)&&id!=job.expectedId)||(job.sourceId==id&&job.expectedId!=id))
   {job.status=CauExtraStatus.Failed;job.error="上传结果蓝图与交接记录不一致。";CauUploadVerification.ModelFailed(job);Save();return;}
   if(job.queueItem!=null)try{job.queueItem.Validate(id);}catch(Exception e){job.status=CauExtraStatus.Failed;job.error="上传期间配置发生变化："+e.Message;CauUploadVerification.ModelFailed(job);Save();return;}
   job.avatarId=id;job.modelSucceeded=true;job.status=job.preserveName&&string.IsNullOrEmpty(job.imagePath)?CauExtraStatus.Done:CauExtraStatus.Pending;job.error="";Save();
  }
  static void BatchFinished(object progress,object success)
  {
   foreach(var job in State.jobs.Where(j=>j.batch==Batch(progress)&&j.status==CauExtraStatus.AwaitingModel))
   {job.status=CauExtraStatus.Failed;job.error=UploadError(progress,job.settingGuid,job.uploadPlatform)??"CAU 未确认此模型上传成功，任务可能已中止。";CauUploadVerification.ModelFailed(job);}
   Save();
  }
  static void Tick()
  {
   if(stopping||running||CauServerQueries.Busy||CauBatchQueue.Preparing||EditorApplication.isCompiling||EditorApplication.isUpdating||!SplitIds.Editing||CauBridge.Busy)return;
   if(cache==null&&!File.Exists(Journal))return;
   var next=State.jobs.FirstOrDefault(j=>j.status==CauExtraStatus.Pending&&(j.modelSucceeded||j.metadataOnly));if(next!=null)Pump(next);
  }
  static async void Pump(CauExtraJob job)
  {
   running=true;cancellation=new CancellationTokenSource();
   try
   {
    job.nameApplied=job.imageApplied=false;job.status=CauExtraStatus.Running;Save();
    await CauExtraProcessor.Apply(job,new CauSdkExtraBackend(),cancellation.Token);
    job.status=CauExtraStatus.Done;job.error="";
   }
   catch(OperationCanceledException){job.status=CauExtraStatus.Pending;job.error="同步已暂停，编辑模式空闲后继续。";}
   catch(Exception e){job.status=CauExtraStatus.Failed;job.error=e.ToString();Debug.LogError("[封面与名称] "+job.displayName+"："+e.Message);}
   finally
   {
    running=false;cancellation.Dispose();cancellation=null;
    if(!stopping)try{Save();CauServerQueries.RecordApplied(job);}catch(Exception e){Debug.LogException(e);}
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
   switch(job.status){case CauExtraStatus.AwaitingModel:return "等待模型上传";case CauExtraStatus.Pending:return "等待同步";case CauExtraStatus.Running:return "正在更新"+(job.preserveName?"封面":string.IsNullOrEmpty(job.imagePath)?"名称":"封面与名称");case CauExtraStatus.Done:return job.metadataOnly?"名称和封面更新已完成":"已完成";case CauExtraStatus.Skipped:return "已跳过";default:return "未完成 · "+job.error;}
  }
 }
}
#endif
