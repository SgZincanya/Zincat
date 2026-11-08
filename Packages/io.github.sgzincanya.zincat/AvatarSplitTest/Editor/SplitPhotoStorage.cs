#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
namespace Zincat.AvatarSplitTest
{
 [InitializeOnLoad] internal static class SplitPhotoStorage
 {
  const string CompatibilityRoot="Assets/Zincat/Zincat兼容";
  const string OldCompatibilityRoot="Assets/Zincat兼容";
  public const string DefaultUploadFolder=CompatibilityRoot+"/UploadSettings/Covers";
  public const string DefaultExportFolder=CompatibilityRoot+"/模型拆分/照片导出";
  static readonly string[] PreviousUploadFolders={OldCompatibilityRoot+"/UploadSettings/Covers",OldCompatibilityRoot+"/模型拆分/照片缓存",CompatibilityRoot+"/模型拆分/照片缓存"};
  const string UploadPref="Zincat.SplitPhoto.LastUploadFolder";
  const string ExportPref="Zincat.SplitPhoto.LastExportFolder";
  const string Owner="Zincat.SplitPhoto.UploadCache.v1";
  [Serializable] sealed class Tag{public string owner,sourceKey,groupId;public int version;}
  static SplitPhotoStorage(){EditorApplication.delayCall+=MigrateLoaded;}
  static void MigrateLoaded()
  {
   if(EditorApplication.isCompiling||EditorApplication.isUpdating||!SplitIds.Editing||CauBridge.Busy||CauExtraUploads.Busy){EditorApplication.delayCall+=MigrateLoaded;return;}
   foreach(var plan in SplitConfigStore.LoadedPlans().ToArray())try{MigrateLegacy(plan);}catch(Exception e){Debug.LogWarning("[照片缓存] "+e.Message);}
  }
  static string RebaseMovedPath(string value)
  {
   if(string.IsNullOrWhiteSpace(value))return value;
   string normalized;try{normalized=UploadRoot(value);}catch{return value;}
   if(normalized.Equals(OldCompatibilityRoot,StringComparison.OrdinalIgnoreCase))return CompatibilityRoot;
   if(normalized.StartsWith(OldCompatibilityRoot+"/",StringComparison.OrdinalIgnoreCase))return CompatibilityRoot+normalized.Substring(OldCompatibilityRoot.Length);
   return value;
  }
  static string RenameDefault(string value)
  {
   value=RebaseMovedPath(value);if(string.IsNullOrWhiteSpace(value))return value;
   string normalized;try{normalized=UploadRoot(value);}catch{return value;}
   foreach(string previous in PreviousUploadFolders)
   {
    if(normalized.Equals(previous,StringComparison.OrdinalIgnoreCase))return DefaultUploadFolder;
    if(normalized.StartsWith(previous+"/",StringComparison.OrdinalIgnoreCase))return DefaultUploadFolder+normalized.Substring(previous.Length);
   }
   return value;
  }
  public static bool Upgrade(AvatarSplitPlan plan)
  {
   var s=plan.photoSettings;if(s==null)plan.photoSettings=s=new SplitPhotoSettings();if(s.storageVersion>=3)return false;
   string remembered=EditorPrefs.GetString(UploadPref,DefaultUploadFolder),renamed=RenameDefault(remembered);
   if(renamed!=remembered)EditorPrefs.SetString(UploadPref,renamed);
   string rememberedExport=EditorPrefs.GetString(ExportPref,DefaultExportFolder),renamedExport=RebaseMovedPath(rememberedExport);
   if(renamedExport!=rememberedExport)EditorPrefs.SetString(ExportPref,renamedExport);
   if(s.storageVersion<1)
   {
    if(string.IsNullOrWhiteSpace(s.uploadFolder)||s.uploadFolder==DefaultUploadFolder)s.uploadFolder=renamed;
    if(string.IsNullOrWhiteSpace(s.outputFolder)||s.outputFolder=="Assets/ZincatPhotos"||s.outputFolder==DefaultExportFolder)s.outputFolder=renamedExport;
   }
   s.uploadFolder=RenameDefault(s.uploadFolder);s.outputFolder=RebaseMovedPath(s.outputFolder);s.storageVersion=3;SplitIds.SaveLater(plan);return true;
  }
  static void MovePreviousDefault()
  {
   foreach(string previous in PreviousUploadFolders)
   {
    if(!AssetDatabase.IsValidFolder(previous)||Directory.Exists(DefaultUploadFolder)||AssetDatabase.IsValidFolder(DefaultUploadFolder))continue;
    AvatarSplitBuilder.EnsureFolder(CompatibilityRoot+"/UploadSettings");AssetDatabase.ReleaseCachedFileHandles();
    string error=AssetDatabase.MoveAsset(previous,DefaultUploadFolder);if(!string.IsNullOrEmpty(error))throw new IOException(error);
   }
  }
  public static string UploadRoot(string value)
  {
   string full=SplitPhoto.ResolveFolder(value),assets=Path.GetFullPath(Application.dataPath);
   if(full.Equals(assets,StringComparison.OrdinalIgnoreCase))return "Assets";
   if(!full.StartsWith(assets+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("上传缓存需要保存在当前工程的 Assets 内；额外导出可以选择任意文件夹。");
   return "Assets/"+full.Substring(assets.Length+1).Replace('\\','/');
  }
  public static void RememberUpload(SplitPhotoSettings s){s.uploadFolder=UploadRoot(s.uploadFolder);EditorPrefs.SetString(UploadPref,s.uploadFolder);}
  public static void RememberExport(SplitPhotoSettings s){SplitPhoto.ResolveFolder(s.outputFolder);EditorPrefs.SetString(ExportPref,s.outputFolder);}
  public static string PathFor(AvatarSplitPlan plan,SplitGroup group)
  {
   string key=SplitConfigStore.Key(plan);if(string.IsNullOrEmpty(key)||key!=SplitPhoto.SafeFile(key))throw new InvalidOperationException("分组配置标识无效，请先保存配置。");
   if(group.version<1)throw new InvalidOperationException("版本编号需要大于 0。");
   return UploadRoot(plan.photoSettings.uploadFolder)+"/"+key+"/V"+group.version+".png";
  }
  static string Legacy(AvatarSplitPlan plan,SplitGroup group)=>AvatarSplitBuilder.Root+"/UploadCovers/"+SplitConfigStore.Key(plan)+"_"+group.id+".png";
  static Tag ReadTag(string path)
  {
   var importer=AssetImporter.GetAtPath(path) as TextureImporter;
   if(importer==null||string.IsNullOrEmpty(importer.userData))return null;
   try{return JsonUtility.FromJson<Tag>(importer.userData);}catch{return null;}
  }
  static bool Owned(AvatarSplitPlan plan,SplitGroup group,string path)
  {
   if(path==Legacy(plan,group))return true;
   var tag=ReadTag(path);return tag!=null&&tag.owner==Owner&&tag.sourceKey==SplitConfigStore.Key(plan)&&tag.groupId==group.id;
  }
  public static void ValidateTarget(AvatarSplitPlan plan,SplitGroup group,string path)
  {
   if(File.Exists(path)&&!Owned(plan,group,path))throw new InvalidOperationException("目标已有其他文件，未覆盖："+path+"。请更换上传缓存位置。");
  }
  public static void Configure(AvatarSplitPlan plan,SplitGroup group,string path)
  {
   if(!(AssetImporter.GetAtPath(path) is TextureImporter importer))throw new IOException("无法读取封面导入设置。");
   string json=JsonUtility.ToJson(new Tag{owner=Owner,sourceKey=SplitConfigStore.Key(plan),groupId=group.id,version=group.version});
   if(importer.npotScale==TextureImporterNPOTScale.None&&importer.userData==json)return;
   importer.npotScale=TextureImporterNPOTScale.None;importer.userData=json;importer.SaveAndReimport();
  }
  public static string PreparePath(AvatarSplitPlan plan,SplitGroup group)
  {
   string path=PathFor(plan,group);ValidateTarget(plan,group,path);string folder=Path.GetDirectoryName(path).Replace('\\','/');AvatarSplitBuilder.EnsureFolder(folder);
   string note=folder+"/照片缓存说明.txt";
   if(!File.Exists(note))
   {
    var source=SplitIds.Resolve(plan.sourceId) as GameObject;
    File.WriteAllText(note,"Zincat 自动生成的上传照片缓存\n源 Avatar："+(source?source.name:plan.name)+"\n配置标识："+SplitConfigStore.Key(plan)+"\n发送给上传时按 V 编号覆盖；额外导出的照片存放在另一个目录。\n请勿把个人文件放到同名 V 编号下。\n");AssetDatabase.ImportAsset(note);
   }
   string current=AssetDatabase.GUIDToAssetPath(group.photoCoverGuid??"");
   if(string.IsNullOrEmpty(current)||!File.Exists(current))current=Legacy(plan,group);
   if(!File.Exists(current))current=PreviousUploadFolders.Select(folder=>folder+"/"+SplitConfigStore.Key(plan)+"/V"+group.version+".png").FirstOrDefault(File.Exists)??current;
   if(current!=path&&File.Exists(current)&&Owned(plan,group,current)&&!File.Exists(path))
   {
    AssetDatabase.ReleaseCachedFileHandles();
    string error=AssetDatabase.MoveAsset(current,path);if(!string.IsNullOrEmpty(error))throw new IOException(error);
   }
   return path;
  }
  public static void MigrateLegacy(AvatarSplitPlan plan)
  {
   Upgrade(plan);if(CauBridge.Busy||CauExtraUploads.Busy||!SplitIds.Editing)return;
   MovePreviousDefault();
   string targetAvatar=UploadRoot(plan.photoSettings.uploadFolder)+"/"+SplitConfigStore.Key(plan);
   foreach(string previous in PreviousUploadFolders)
   {
    string previousAvatar=previous+"/"+SplitConfigStore.Key(plan);
    if(!AssetDatabase.IsValidFolder(previousAvatar)||Directory.Exists(targetAvatar))continue;
    AvatarSplitBuilder.EnsureFolder(UploadRoot(plan.photoSettings.uploadFolder));AssetDatabase.ReleaseCachedFileHandles();
    string error=AssetDatabase.MoveAsset(previousAvatar,targetAvatar);if(!string.IsNullOrEmpty(error))throw new IOException(error);
   }
   foreach(var group in plan.groups)
   {
    if(!File.Exists(Legacy(plan,group))&&!PreviousUploadFolders.Any(folder=>File.Exists(folder+"/"+SplitConfigStore.Key(plan)+"/V"+group.version+".png")))continue;
    string path=PreparePath(plan,group);if(File.Exists(path))Configure(plan,group,path);
   }
   foreach(string oldFolder in new[]{AvatarSplitBuilder.Root+"/UploadCovers"}.Concat(PreviousUploadFolders))
    if(AssetDatabase.IsValidFolder(oldFolder)&&Directory.Exists(oldFolder)&&!Directory.EnumerateFileSystemEntries(oldFolder).Any())AssetDatabase.DeleteAsset(oldFolder);
  }
 }
}
#endif
