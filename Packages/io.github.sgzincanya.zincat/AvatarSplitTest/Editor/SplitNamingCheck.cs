#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using Object=UnityEngine.Object;
namespace Zincat.AvatarSplitTest
{
 [InitializeOnLoad] internal static class SplitNamingCheck
 {
  const string Request="Library/ZincatNamingCheck.request";
  static SplitNamingCheck(){if(File.Exists(Request))EditorApplication.update+=Run;}
  static void Run()
  {
   if(EditorApplication.isCompiling||EditorApplication.isUpdating||!SplitIds.Editing||CauBridge.Busy)return;
   EditorApplication.update-=Run;if(!File.Exists(Request))return;File.Delete(Request);
   var report=new List<string>();var previous=SceneManager.GetActiveScene();Scene fixture=default;AvatarSplitPlan plan=null;
   var folder=AvatarSplitBuilder.Root+"/_NamingCheck_"+Guid.NewGuid().ToString("N");
   try
   {
    AvatarSplitBuilder.EnsureFolder(folder);fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
    var source=new GameObject("命名测试");SceneManager.MoveGameObjectToScene(source,fixture);
    var generated=new GameObject("旧模型名");SceneManager.MoveGameObjectToScene(generated,fixture);
    var pipeline=generated.AddComponent<PipelineManager>();pipeline.blueprintId="avtr_66666666-6666-6666-6666-666666666666";
    var stale=new GameObject("未更新的旧模型");SceneManager.MoveGameObjectToScene(stale,fixture);
    EditorSceneManager.SaveScene(fixture,folder+"/Fixture.unity");
    plan=ScriptableObject.CreateInstance<AvatarSplitPlan>();plan.sourceId=SplitIds.Of(source);plan.sourceScene=fixture.path;
    var a=new SplitGroup{version=1,outfitNames=new List<string>{" 兔女郎\n","旗袍 "},revision=3,generatedRevision=3,generatedAvatarId=SplitIds.Of(generated),blueprintId=pipeline.blueprintId};
    var b=new SplitGroup{version=2,outfitNames=new List<string>{"修女","OL"},revision=3,generatedRevision=2,generatedAvatarId=SplitIds.Of(stale)};
    plan.groups.Add(a);plan.groups.Add(b);string identity=a.id;
    SplitNaming.Apply(plan,source);
    Assert(a.name=="命名测试V1｜兔女郎·旗袍"&&b.name=="命名测试V2｜修女·OL","按每组已选菜单名命名，清除多余空白并保持菜单顺序");
    Assert(generated.name==a.name&&a.generatedRevision==a.revision&&a.id==identity&&pipeline.blueprintId==a.blueprintId,"仅改名称时同步已有模型，保留蓝图、分组身份和就绪状态");
    Assert(stale.name=="未更新的旧模型"&&b.generatedRevision!=b.revision,"衣服已变化的旧结果保持待生成，不用新名字冒充新内容");
    plan.groups.Reverse();SplitNaming.Apply(plan,source);
    Assert(a.name.StartsWith("命名测试V1｜")&&b.name.StartsWith("命名测试V2｜"),"调整分组顺序不改变版本号或衣服名称");
    a.Invalidate();a.outfitNames=new List<string>{"泳装"};SplitNaming.Apply(plan,source);
    Assert(a.name=="命名测试V1｜泳装"&&a.generatedRevision!=a.revision&&generated.name=="命名测试V1｜兔女郎·旗袍","更改勾选内容后更新分组名并要求重新生成");
    Assert(SplitNaming.NameFor("角色",new SplitGroup{version=3})=="角色V3","空分组保持基础名称，不添加空分隔符");
    SplitIds.Flush();Object.DestroyImmediate(plan);plan=null;
    EditorSceneManager.CloseScene(fixture,true);fixture=default;
    if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);
    foreach(var current in SplitConfigStore.LoadedPlans().ToArray())
    {
     var avatar=SplitIds.Resolve(current.sourceId) as GameObject;if(!avatar)continue;
     Dictionary<string,OutfitMenuChoice> choices;
     try{choices=AvatarOutfitMenus.Read(avatar).ToDictionary(c=>c.Id);}catch(InvalidOperationException){continue;}
     var blueprints=current.groups.ToDictionary(g=>g.id,g=>g.blueprintId);
     foreach(var group in current.groups)
      group.outfitNames=group.outfitIds.Select((id,i)=>choices.TryGetValue(id,out var entry)?entry.Name:i<group.outfitNames.Count?group.outfitNames[i]:"已失效衣服").ToList();
     SplitNaming.Apply(current,avatar);CauBridge.RetargetExistingSettings(current);SplitIds.Save(current);
     Assert(current.groups.All(g=>blueprints[g.id]==g.blueprintId),"现有分组名称已更新，蓝图号保持不变："+avatar.name);
     foreach(var group in current.groups)report.Add("NAME: "+group.name+(group.generatedRevision==group.revision?" / 模型与上传名称已同步":" / 衣服已变化，需重新生成"));
    }
    report.Add("PASS: 命名更新完成；未操作主窗口，未上传。");
   }
   catch(Exception e){report.Add("FAIL: "+e);Debug.LogException(e);}
   finally
   {
    if(plan){SplitIds.Flush();Object.DestroyImmediate(plan);}
    if(fixture.IsValid()&&fixture.isLoaded)EditorSceneManager.CloseScene(fixture,true);
    if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);
    if(AssetDatabase.IsValidFolder(folder))AssetDatabase.DeleteAsset(folder);
    File.WriteAllLines("Library/ZincatNamingCheck.result.txt",report);
   }
   void Assert(bool condition,string message){if(!condition)throw new Exception(message);report.Add("PASS: "+message);}
  }
 }
}
#endif
