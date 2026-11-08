#if UNITY_EDITOR
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using Object=UnityEngine.Object;
namespace Zincat.AvatarSplitTest
{
 internal struct SplitPhotoPhysicsResult{public int count,changed;}
 internal static class SplitPhotoEditPhysics
 {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
  static FieldInfo Field(Type type,string name){for(;type!=null;type=type.BaseType){var field=type.GetField(name,Flags|BindingFlags.DeclaredOnly);if(field!=null)return field;}throw new MissingFieldException(name);}
  static MethodInfo Method(Type type,string name){for(;type!=null;type=type.BaseType){var method=type.GetMethod(name,Flags|BindingFlags.DeclaredOnly);if(method!=null)return method;}throw new MissingMethodException(name);}
  static object Call(object target,string name,params object[] args)=>Method(target.GetType(),name).Invoke(target,args);
  public static SplitPhotoPhysicsResult Run(IReadOnlyList<GameObject> actors,HashSet<Behaviour> originallyEnabled,float seconds)
  {
   if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("编辑模式动骨模拟不能在游戏模式中运行。");
   bool Owned(Transform t)=>t&&actors.Any(a=>a&&(t==a.transform||t.IsChildOf(a.transform)));
   if(actors.Any(a=>!a||!UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(a.scene)))throw new InvalidOperationException("动骨模拟仅允许用于临时拍照分身。");
   var allBones=actors.SelectMany(a=>a.GetComponentsInChildren<VRCPhysBoneBase>(true)).ToArray();
   var allColliders=actors.SelectMany(a=>a.GetComponentsInChildren<VRCPhysBoneColliderBase>(true)).ToArray();
   var bones=allBones.Where(b=>b&&b.gameObject.activeInHierarchy&&originallyEnabled.Contains(b)).ToArray();
   if(bones.Length==0)return default;
   foreach(var bone in bones)
   {
    if(!Owned(bone.GetRootTransform())||(bone.ignoreTransforms!=null&&bone.ignoreTransforms.Any(t=>t&&!Owned(t))))throw new InvalidOperationException("动骨引用了分身以外的骨骼，已停止模拟。");
    if(bone.colliders!=null&&bone.colliders.Any(c=>c&&(!Owned(c.transform)||!Owned(c.GetRootTransform()))))throw new InvalidOperationException("动骨引用了分身以外的碰撞体，已停止模拟。");
   }
   foreach(var collider in allColliders.Where(c=>c&&c.gameObject.activeInHierarchy&&originallyEnabled.Contains(c)))
    if(!Owned(collider.GetRootTransform()))throw new InvalidOperationException("碰撞体引用了分身以外的骨骼，已停止模拟。");
   var managerType=typeof(PhysBoneManager);var instance=Field(managerType,"Inst");var timing=Field(managerType,"DisableTiming");var step=Field(managerType,"DebugTimeElapsed");
   var schedule=Method(managerType,"ScheduleExecutionJob");Method(managerType,"Awake");Field(managerType,"collidersToAdd");
   foreach(var bone in bones){Method(bone.GetType(),"Start");Method(bone.GetType(),"InitTransforms");}
   var originalManager=instance.GetValue(null);var originalTiming=timing.GetValue(null);var originalStep=step.GetValue(null);
   var initCallback=Field(typeof(VRCPhysBoneBase),"OnInitialize");var colliderCallback=Field(typeof(VRCPhysBoneColliderBase),"OnPreShapeInitialize");
   var oldInit=initCallback.GetValue(null);var oldCollider=colliderCallback.GetValue(null);
   var transforms=actors.SelectMany(a=>a.GetComponentsInChildren<Transform>(true)).ToArray();
   var before=transforms.Select(t=>(t.localPosition,t.localRotation,t.localScale)).ToArray();
   (Vector3 position,Quaternion rotation,Vector3 scale)[] frozen=null;GameObject managerObject=null;PhysBoneManager manager=null;
   var result=new SplitPhotoPhysicsResult{count=bones.Length};
   try
   {
    if(originalManager is Object existing&&existing)Call(originalManager,"CompleteJob");
    managerObject=new GameObject("Photo PhysBone Solver"){hideFlags=HideFlags.HideAndDontSave};SceneManager.MoveGameObjectToScene(managerObject,actors[0].scene);
    manager=managerObject.AddComponent<PhysBoneManager>();Call(manager,"Awake");instance.SetValue(null,manager);Field(managerType,"IsSDK").SetValue(manager,true);
    timing.SetValue(null,true);step.SetValue(null,1f/60);initCallback.SetValue(null,null);colliderCallback.SetValue(null,null);
    foreach(var collider in allColliders.Where(c=>c&&c.gameObject.activeInHierarchy&&originallyEnabled.Contains(c)))
    {
     collider.enabled=true;
     if(!(Field(collider.GetType(),"_parentChangeDetector").GetValue(collider) is Object detector)||!detector)Call(collider,"Awake");
     var queue=(IList)Field(managerType,"collidersToAdd").GetValue(manager);
     if(!queue.Contains(collider))Call(manager,"AddCollider",collider);
    }
    foreach(var bone in bones)
    {
     bone.chainId=ChainId.Null;bone.enabled=true;
     if(!(Field(bone.GetType(),"_parentChangeDetector").GetValue(bone) is Object detector)||!detector)Call(bone,"Awake");
     Call(bone,"InitTransforms",true);Call(bone,"Start");
    }
    int steps=Mathf.Clamp(Mathf.RoundToInt(seconds*60),12,300);
    for(int i=0;i<steps;i++)
    {
     if(i%15==0&&!Application.isBatchMode&&EditorUtility.DisplayCancelableProgressBar("分组拍照","正在计算临时分身的动骨…",(float)i/steps))throw new OperationCanceledException();
     var handle=(Unity.Jobs.JobHandle)schedule.Invoke(manager,new object[]{default(Unity.Jobs.JobHandle)});handle.Complete();
    }
    frozen=transforms.Select(t=>(t.localPosition,t.localRotation,t.localScale)).ToArray();
    result.changed=Enumerable.Range(0,transforms.Length).Count(i=>Vector3.Distance(before[i].Item1,frozen[i].position)>.00001f||Quaternion.Angle(before[i].Item2,frozen[i].rotation)>.05f);
   }
   catch(TargetInvocationException e){throw new InvalidOperationException("当前 SDK 的编辑模式动骨模拟失败："+(e.InnerException?.Message??e.Message),e.InnerException??e);}
   finally
   {
    try
    {
     // Remove SDK components while their isolated manager is still selected.
     // Destruction can reset transforms, so restore the frozen pose afterwards.
     foreach(var bone in allBones)if(bone){bone.resetWhenDisabled=false;Object.DestroyImmediate(bone);}
     foreach(var collider in allColliders)if(collider)Object.DestroyImmediate(collider);
     if(managerObject)Object.DestroyImmediate(managerObject);
    }
    finally
    {
     instance.SetValue(null,originalManager);timing.SetValue(null,originalTiming);step.SetValue(null,originalStep);initCallback.SetValue(null,oldInit);colliderCallback.SetValue(null,oldCollider);
     if(frozen!=null)for(int i=0;i<transforms.Length;i++)if(transforms[i]){transforms[i].localPosition=frozen[i].position;transforms[i].localRotation=frozen[i].rotation;transforms[i].localScale=frozen[i].scale;}
     if(!Application.isBatchMode)EditorUtility.ClearProgressBar();
    }
   }
   return result;
  }
 }
}
#endif
