#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using nadena.dev.modular_avatar.core;
using Zincat.Clothing;
using Object=UnityEngine.Object;
namespace Zincat.AvatarSplitTest
{
 [InitializeOnLoad] internal static class SplitSerializationCheck
 {
  const string Request="Library/ZincatSerializationCheck.request";
  static SplitSerializationCheck(){if(File.Exists(Request))EditorApplication.update+=Run;}
  static int Orphans(string path)=>Regex.Matches(File.ReadAllText(path),@"^--- !u!4 &\d+[^\n]*\nTransform:\r?\n(?:(?!^---).)*?^  m_GameObject: \{fileID: 0\}",RegexOptions.Multiline|RegexOptions.Singleline).Count;
  static void Run()
  {
   if(EditorApplication.isCompiling||EditorApplication.isUpdating||!SplitIds.Editing||CauBridge.Busy)return;
   EditorApplication.update-=Run;if(!File.Exists(Request))return;File.Delete(Request);
   var log=new List<string>();var previous=SceneManager.GetActiveScene();Scene fixture=default;
   var folder=AvatarSplitBuilder.Root+"/_SerializationCheck_"+Guid.NewGuid().ToString("N");
   try
   {
    AvatarSplitBuilder.EnsureFolder(folder);
    fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
    var source=new GameObject("SerializationFixture");SceneManager.MoveGameObjectToScene(source,fixture);
    source.AddComponent<Animator>();source.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
    var menu=Child(source,"衣服菜单");var c=menu.AddComponent<WardrobeGeneratedSwitch>();
    c.AvatarRoot=source;c.IsSeparateCollection=true;c.UsesInstallTargets=true;c.DirectOutfitToggles=true;c.ParameterName="Outfit";
    menu.AddComponent<ModularAvatarParameters>().parameters=new List<ParameterConfig>{new ParameterConfig{nameOrPrefix="Outfit",syncType=ParameterSyncType.Int}};
    var outfit=Child(source,"保留衣服");var parts=Child(source,"衣服分件菜单");var wear=Child(parts,"换衣");
    wear.AddComponent<ModularAvatarMenuItem>().Control=new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle};
    wear.AddComponent<ModularAvatarObjectToggle>();
    c.Entries.Add(new WardrobeSwitchLink{OutfitRoot=outfit,PartMenuRoot=parts,WearNode=wear,PartsNode=Child(menu,"入口")});c.DefaultOutfit=outfit;
    var temporary=Child(source,"MissingPrefabPlaceholder");temporary.hideFlags=HideFlags.DontSaveInEditor;temporary.transform.hideFlags=HideFlags.None;
    var hidden=Child(source,"合法隐藏菜单");hidden.hideFlags=HideFlags.HideInHierarchy;
    var copy=AvatarSplitBuilder.CreateVariant(source,new[]{outfit},"序列化测试副本","",fixture,out _,out _);
    Assert(!copy.transform.Find("MissingPrefabPlaceholder")&&temporary&&temporary.transform.parent==source.transform,"生成副本排除不保存物体，源对象保留");
    Assert(copy.transform.Find("合法隐藏菜单")&&copy.transform.Find("保留衣服"),"正常隐藏菜单和衣服不受影响");
    Object.DestroyImmediate(source);
    string path=folder+"/Fixture.unity";
    Assert(EditorSceneManager.SaveScene(fixture,path)&&Orphans(path)==0,"保存后的场景文件不存在 m_GameObject=0 的孤立 Transform");
    EditorSceneManager.CloseScene(fixture,true);fixture=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
    Assert(fixture.GetRootGameObjects().Length==1&&Orphans(path)==0,"关闭并重新打开临时场景，结构完整且无孤立 Transform");
    EditorSceneManager.CloseScene(fixture,true);fixture=default;
    if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);
    foreach(var group in Resources.FindObjectsOfTypeAll<AvatarSplitOutputRoot>().Where(r=>r&&r.gameObject.scene.IsValid()&&r.gameObject.scene.isLoaded&&!EditorUtility.IsPersistent(r)))
     foreach(var generated in group.Generated)if(generated&&generated.transform.parent==group.transform)AvatarSplitBuilder.RemoveUnsavableChildren(generated);
    var scene=SceneManager.GetSceneByPath("Assets/纸柠shenx 6.unity");
    if(scene.IsValid()&&scene.isLoaded)
    {
     SplitIds.Flush();
     Assert(EditorSceneManager.SaveScene(scene),"保存当前用户场景中的修复结果");
     Assert(Orphans(scene.path)==0,"实际用户场景已清理：孤立 Transform 数量为 0");
    }
    log.Add("PASS: 检查及修复完成；未改变主窗口、未执行上传。");
   }
   catch(Exception e){log.Add("FAIL: "+e);Debug.LogException(e);}
   finally
   {
    if(fixture.IsValid()&&fixture.isLoaded)EditorSceneManager.CloseScene(fixture,true);
    if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);
    if(AssetDatabase.IsValidFolder(folder))AssetDatabase.DeleteAsset(folder);
    File.WriteAllLines("Library/ZincatSerializationCheck.result.txt",log);
   }
   void Assert(bool condition,string message){if(!condition)throw new Exception(message);log.Add("PASS: "+message);}
  }
  static GameObject Child(GameObject parent,string name){var go=new GameObject(name);go.transform.SetParent(parent.transform,false);return go;}
 }
}
#endif
