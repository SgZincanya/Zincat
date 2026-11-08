using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using Object=UnityEngine.Object;

public class CopyArmatureTransformsWindow : EditorWindow
{
    [SerializeField] GameObject sourceRoot,destRoot;
    [SerializeField] Transform sourceSkeleton,targetSkeleton;
    [SerializeField] bool copyScale,protectDriven=true;
    List<ZincatBoneRepair.Row> rows; Vector2 scroll; string message; ZincatBoneRepair.Report lastReport;
    [MenuItem("Zincat/复制骨骼变换")]
    public static void ShowWindow() { GetWindow<CopyArmatureTransformsWindow>("复制骨骼变换").minSize=new Vector2(560,420); }
    void OnGUI() {
        EditorGUI.BeginChangeCheck();
        sourceRoot=(GameObject)EditorGUILayout.ObjectField("参考模型（正常）",sourceRoot,typeof(GameObject),true);
        destRoot=(GameObject)EditorGUILayout.ObjectField("目标模型（修复）",destRoot,typeof(GameObject),true);
        sourceSkeleton=(Transform)EditorGUILayout.ObjectField("参考骨架（可选）",sourceSkeleton,typeof(Transform),true);
        targetSkeleton=(Transform)EditorGUILayout.ObjectField("目标骨架（可选）",targetSkeleton,typeof(Transform),true);
        copyScale=EditorGUILayout.Toggle("同时复制缩放",copyScale);
        protectDriven=EditorGUILayout.Toggle("跳过受约束驱动的属性",protectDriven);
        if(EditorGUI.EndChangeCheck()) {rows=null;message=null;lastReport=null;}
        if(!protectDriven) EditorGUILayout.HelpBox("恢复原约束后，新姿态可能再次被覆盖；工具会在恢复后复查并报告。",MessageType.Warning);
        using(new EditorGUI.DisabledScope(sourceRoot==null||destRoot==null||EditorApplication.isPlayingOrWillChangePlaymode))
        if(GUILayout.Button("扫描骨骼差异")) {
            try {rows=ZincatBoneRepair.Scan(sourceRoot,destRoot,sourceSkeleton,targetSkeleton,copyScale);message=null;lastReport=null;}
            catch(Exception ex) {message=ex.Message;}
        }
        scroll=EditorGUILayout.BeginScrollView(scroll);
        if(rows!=null) {
            EditorGUILayout.LabelField("匹配 "+rows.Count(r=>r.Source!=null&&r.Target!=null)+"；差异 "+rows.Count(r=>r.Changed)+"；缺失或歧义 "+rows.Count(r=>r.Issue!=null));
            foreach(var row in rows) {
                EditorGUILayout.BeginHorizontal();
                using(new EditorGUI.DisabledScope(!row.Changed||row.Issue!=null)) row.Apply=EditorGUILayout.Toggle(row.Apply,GUILayout.Width(20));
                EditorGUILayout.LabelField(row.Path,GUILayout.MinWidth(180));EditorGUILayout.LabelField(row.Issue??row.Status,GUILayout.MinWidth(210));EditorGUILayout.EndHorizontal();
            }
        }
        EditorGUILayout.EndScrollView();
        using(new EditorGUI.DisabledScope(rows==null||!rows.Any(r=>r.Apply)||EditorApplication.isPlayingOrWillChangePlaymode))
        if(GUILayout.Button("应用所选修复",GUILayout.Height(28))) {
            try {lastReport=ZincatBoneRepair.Apply(rows,copyScale,protectDriven);message=lastReport.ToString();EditorApplication.delayCall+=()=> {if(this!=null) Repaint();};}
            catch(Exception ex) {message=ex.Message;}
        }
        if(lastReport!=null) message=lastReport.ToString();
        if(!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message,MessageType.Info);
        EditorGUILayout.LabelField("默认只恢复局部位置和旋转，保留体型缩放；按完整路径匹配。",EditorStyles.wordWrappedMiniLabel);
    }
}

internal static class ZincatBoneRepair
{
    internal class Row {
        public string Path,Issue,Status;public Transform Source,Target,SourceRoot,TargetRoot;public bool Changed,Apply;
        public Vector3 Position,Scale;public Quaternion Rotation;
        public List<Driver> Drivers=new List<Driver>();
    }
    internal class Driver {
        public Behaviour Component;public Transform Target;public bool Position,Rotation,Scale,Active;
        public IConstraint Unity; public FieldInfo VrcActive,VrcLocked;
        public void Pause() {Component.enabled=false;if(Unity!=null) {Unity.constraintActive=false;Unity.locked=false;} else {VrcActive?.SetValue(Component,false);VrcLocked?.SetValue(Component,false);}}
    }
    internal class Report {
        public int Copied,Skipped;public List<string> Overridden=new List<string>();
        public override string ToString()=>$"已修复 {Copied} 个骨骼；跳过 {Skipped} 个受约束驱动的属性。可一次撤销。"+(Overridden.Count>0?"\n恢复约束后仍被覆盖：\n"+string.Join("\n",Overridden):"");
    }
    internal static Transform FindSkeleton(GameObject root) {
        if(root==null) throw new InvalidOperationException("请指定参考和目标模型。");
        var named=root.GetComponentsInChildren<Transform>(true).Where(t=>t.name.StartsWith("Armature",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(named.Length==1) return named[0];
        if(named.Length>1) throw new InvalidOperationException(root.name+" 下有多个 Armature，请手动指定骨架。");
        var candidates=root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r=>r.rootBone).Where(t=>t!=null&&t.IsChildOf(root.transform)).Select(t=> {
            while(t.parent!=null&&t.parent!=root.transform) t=t.parent;return t;
        }).Distinct().ToArray();
        if(candidates.Length==1) return candidates[0];
        if(candidates.Length>1) throw new InvalidOperationException(root.name+" 下有多个骨架，请手动指定骨架。");
        // Passing a skeleton object directly remains possible for generic rigs.
        if(root.GetComponent("VRCAvatarDescriptor")==null) return root.transform;
        throw new InvalidOperationException(root.name+" 未找到明确骨架，请手动指定。");
    }
    internal static List<Row> Scan(GameObject source,GameObject target,Transform sourceSkeleton,Transform targetSkeleton,bool scale) {
        if(target==null||EditorUtility.IsPersistent(target)||!target.scene.IsValid()||EditorSceneManager.IsPreviewScene(target.scene)) throw new InvalidOperationException("修复目标必须是场景中的模型。");
        var a=sourceSkeleton!=null?sourceSkeleton:FindSkeleton(source);var b=targetSkeleton!=null?targetSkeleton:FindSkeleton(target);
        if(source==null||!a.IsChildOf(source.transform)||!b.IsChildOf(target.transform)) throw new InvalidOperationException("手动指定的骨架必须属于对应模型。");
        if(a==b||a.IsChildOf(b)||b.IsChildOf(a)) throw new InvalidOperationException("参考与目标骨架不能相同或互相包含。");
        var src=Index(a);var dst=Index(b);var drivers=FindDrivers(target);
        var result=new List<Row>();
        foreach(var path in src.Keys.Union(dst.Keys).OrderBy(x=>x,StringComparer.Ordinal)) {
            src.TryGetValue(path,out var s);dst.TryGetValue(path,out var d);
            var row=new Row {Path=path,SourceRoot=a,TargetRoot=b};
            if(s==null) row.Issue="仅目标存在，保留";
            else if(d==null) row.Issue="目标缺失，跳过";
            else if(s.Count!=1||d.Count!=1||path.Split('/').Any(string.IsNullOrEmpty)||s.Any(t=>HasSlash(a,t))||d.Any(t=>HasSlash(b,t))) row.Issue="路径歧义，跳过";
            else {
                row.Source=s[0];row.Target=d[0];row.Position=s[0].localPosition;row.Rotation=s[0].localRotation;row.Scale=s[0].localScale;
                row.Changed=Different(row.Target,row,scale);row.Apply=row.Changed;
                row.Drivers=drivers.Where(x=>x.Target==row.Target).ToList();
                bool driven=row.Drivers.Any(x=>x.Active&&(x.Position||x.Rotation||(scale&&x.Scale)));
                row.Status=(row.Changed?"有差异":"一致")+(driven?"；受约束驱动":"")+(row.Drivers.Count>0?"；约束 "+row.Drivers.Count:"");
            } result.Add(row);
        } return result;
    }
    static bool HasSlash(Transform root,Transform t) {for(;t!=root;t=t.parent) if(t.name.Contains("/")) return true;return false;}
    static Dictionary<string,List<Transform>> Index(Transform root) {
        return root.GetComponentsInChildren<Transform>(true).Where(t=>t!=root).GroupBy(t=>AnimationUtility.CalculateTransformPath(t,root)).ToDictionary(g=>g.Key,g=>g.ToList());
    }
    internal static List<Driver> FindDrivers(GameObject root) {
        var result=new List<Driver>();
        foreach(var component in root.scene.GetRootGameObjects().SelectMany(o=>o.GetComponentsInChildren<Behaviour>(true))) {
            if(component==null) continue;
            if(component is IConstraint unity) {
                result.Add(new Driver {Component=component,Unity=unity,Target=component.transform,Active=component.enabled&&component.gameObject.activeInHierarchy&&unity.constraintActive&&unity.weight>0,
                    Position=component is ParentConstraint||component is PositionConstraint,Rotation=component is ParentConstraint||component is RotationConstraint||component is AimConstraint||component is LookAtConstraint,Scale=component is ScaleConstraint});
            } else {
                var type=component.GetType();if(!IsVrcConstraint(type)) continue;
                var targetField=type.GetField("TargetTransform");var target=(Transform)targetField?.GetValue(component)??component.transform;
                var active=type.GetField("IsActive");var weight=type.GetField("GlobalWeight");
                result.Add(new Driver {Component=component,Target=target,VrcActive=active,VrcLocked=type.GetField("Locked"),Active=component.enabled&&component.gameObject.activeInHierarchy&&(bool)(active?.GetValue(component)??false)&&Convert.ToSingle(weight?.GetValue(component)??0)>0,
                    Position=(bool)(type.GetProperty("AffectsPosition",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(component)??false),Rotation=(bool)(type.GetProperty("AffectsRotation",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(component)??false),Scale=(bool)(type.GetProperty("AffectsScale",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(component)??false)});
            }
        } return result;
    }
    static bool IsVrcConstraint(Type t) {for(;t!=null;t=t.BaseType) if(t.FullName=="VRC.Dynamics.VRCConstraintBase") return true;return false;}
    static bool Different(Transform t,Row r,bool scale)=>(t.localPosition-r.Position).sqrMagnitude>1e-10f||Quaternion.Angle(t.localRotation,r.Rotation)>0.001f||(scale&&(t.localScale-r.Scale).sqrMagnitude>1e-10f);
    internal static Report Apply(List<Row> rows,bool scale,bool protect) {
        if(EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请退出运行模式后修复。");
        var selected=rows.Where(r=>r.Apply&&r.Issue==null).ToArray();
        if(selected.Any(r=>r.Source==null||r.Target==null||r.SourceRoot==null||r.TargetRoot==null||EditorUtility.IsPersistent(r.Target)||!r.Source.IsChildOf(r.SourceRoot)||!r.Target.IsChildOf(r.TargetRoot)||AnimationUtility.CalculateTransformPath(r.Source,r.SourceRoot)!=r.Path||AnimationUtility.CalculateTransformPath(r.Target,r.TargetRoot)!=r.Path||Index(r.SourceRoot)[r.Path].Count!=1||Index(r.TargetRoot)[r.Path].Count!=1)) throw new InvalidOperationException("骨架层级或路径已变化，请重新扫描。");
        var states=new Dictionary<Behaviour,string>();var result=new Report();
        Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("修复骨骼姿态");
        try {
            // Rescan drivers to avoid using a stale enabled/active state from the preview.
            var all=selected.GroupBy(r=>r.Target.gameObject.scene.handle).SelectMany(g=>FindDrivers(g.First().Target.gameObject)).ToArray();
            foreach(var r in selected) r.Drivers=all.Where(d=>d.Target==r.Target).ToList();
            foreach(var driver in selected.SelectMany(r=>r.Drivers).GroupBy(d=>d.Component).Select(g=>g.First())) {
                if(protect&&driver.Active) continue;
                states[driver.Component]=EditorJsonUtility.ToJson(driver.Component);Undo.RegisterCompleteObjectUndo(driver.Component,"临时暂停骨骼约束");driver.Pause();
            }
            foreach(var r in selected) {
                r.Position=r.Source.localPosition;r.Rotation=r.Source.localRotation;r.Scale=r.Source.localScale;
                bool pd=(r.Target.localPosition-r.Position).sqrMagnitude>1e-10f,rd=Quaternion.Angle(r.Target.localRotation,r.Rotation)>0.001f,sd=scale&&(r.Target.localScale-r.Scale).sqrMagnitude>1e-10f;
                bool pos=pd&&!(protect&&r.Drivers.Any(d=>d.Active&&d.Position));bool rot=rd&&!(protect&&r.Drivers.Any(d=>d.Active&&d.Rotation));bool scl=sd&&!(protect&&r.Drivers.Any(d=>d.Active&&d.Scale));
                result.Skipped+=(pd&&!pos?1:0)+(rd&&!rot?1:0)+(sd&&!scl?1:0);
                if(!pos&&!rot&&!scl) continue;
                Undo.RecordObject(r.Target,"恢复骨骼姿态");if(pos) r.Target.localPosition=r.Position;if(rot) r.Target.localRotation=r.Rotation;if(scl) r.Target.localScale=r.Scale;
                PrefabUtility.RecordPrefabInstancePropertyModifications(r.Target);EditorSceneManager.MarkSceneDirty(r.Target.gameObject.scene);result.Copied++;
            }
            Restore(states);states.Clear();Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);
            foreach(var r in selected) if(!protect&&Different(r.Target,r,scale)) result.Overridden.Add(r.Path);
            if(!protect) EditorApplication.delayCall+=()=> {
                var overridden=selected.Where(r=>r.Target!=null&&Different(r.Target,r,scale)).Select(r=>r.Path).ToArray();
                result.Overridden.Clear();result.Overridden.AddRange(overridden);
                if(overridden.Length>0) Debug.LogWarning("[Zincat 骨骼修复] 恢复约束后仍被覆盖：\n"+string.Join("\n",overridden));
            };
            return result;
        } catch {Restore(states);Undo.RevertAllDownToGroup(group);throw;}
        finally {Restore(states);}
    }
    static void Restore(Dictionary<Behaviour,string> states) {foreach(var pair in states) if(pair.Key!=null) {EditorJsonUtility.FromJsonOverwrite(pair.Value,pair.Key);PrefabUtility.RecordPrefabInstancePropertyModifications(pair.Key);}}
}
