using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;

public class SmallFeaturesPlugin : EditorWindow
{
    [SerializeField] int page;
    [SerializeField] string prefix="", suffix="", find="", replace="", dividerTitle="";
    [SerializeField] bool number;
    [SerializeField] int firstNumber=1;
    [SerializeField] Transform dividerParent;
    [SerializeField] List<GameObject> targets=new List<GameObject>();
    Vector2 scroll; string message; MessageType messageType;
    List<ZincatSceneTools.MeshCount> meshCounts;
    [MenuItem("Zincat/场景小工具")]
    static void Open() { GetWindow<SmallFeaturesPlugin>("场景小工具").minSize=new Vector2(440,340); }
    void OnGUI()
    {
        page=GUILayout.Toolbar(page,new[]{"自动保存","分割线","重命名","三角形"});
        scroll=EditorGUILayout.BeginScrollView(scroll); EditorGUILayout.Space();
        if(page==0) DrawSave(); else if(page==1) DrawDivider(); else
        {
            if(GUILayout.Button("使用当前选择")) { targets=Selection.gameObjects.Where(ZincatSceneTools.IsSceneObject).OrderBy(ZincatSceneTools.HierarchyKey).ToList(); meshCounts=null; message=null; }
            EditorGUILayout.LabelField("已固定目标",targets.Count+" 个");
            if(page==2) DrawRename(); else DrawCounts();
        }
        if(!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message,messageType);
        EditorGUILayout.EndScrollView();
    }
    void DrawSave()
    {
        EditorGUI.BeginChangeCheck();
        bool enabled=EditorGUILayout.Toggle("启用自动保存",ZincatSceneTools.AutoSaveEnabled);
        float interval=EditorGUILayout.Slider("保存间隔（分钟）",ZincatSceneTools.IntervalMinutes,1,30);
        if(EditorGUI.EndChangeCheck()) ZincatSceneTools.SetSaveSettings(enabled,interval);
        EditorGUILayout.LabelField("仅保存当前工程中已命名、有修改的场景。",EditorStyles.wordWrappedLabel);
        if(GUILayout.Button("立即保存已修改场景")) { var r=ZincatSceneTools.SaveDirtyScenes(); message=r.ToString(); messageType=r.Failed>0?MessageType.Error:MessageType.None; }
    }
    void DrawDivider()
    {
        dividerParent=(Transform)EditorGUILayout.ObjectField("父级",dividerParent,typeof(Transform),true);
        if(GUILayout.Button("使用选中对象作为父级")) dividerParent=Selection.activeTransform;
        dividerTitle=EditorGUILayout.TextField("标题",dividerTitle);
        using(new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
        if(GUILayout.Button("创建分割线")) Run(()=>{Selection.activeGameObject=ZincatSceneTools.CreateDivider(dividerParent,dividerTitle); return "";});
    }
    void DrawRename()
    {
        prefix=EditorGUILayout.TextField("前缀",prefix); suffix=EditorGUILayout.TextField("后缀",suffix);
        find=EditorGUILayout.TextField("查找文字",find); replace=EditorGUILayout.TextField("替换为",replace);
        number=EditorGUILayout.Toggle("追加编号",number); if(number) firstNumber=EditorGUILayout.IntField("起始编号",firstNumber);
        var plan=ZincatSceneTools.PlanRename(targets,prefix,suffix,find,replace,number,firstNumber);
        foreach(var row in plan) EditorGUILayout.LabelField(row.Target.name+" → "+row.NewName);
        using(new EditorGUI.DisabledScope(plan.Count==0||EditorApplication.isPlayingOrWillChangePlaymode))
        if(GUILayout.Button("应用重命名")) Run(()=>{
            var risks=ZincatSceneTools.FindAnimationRisks(plan);
            if(risks.Count>0&&!EditorUtility.DisplayDialog("这些对象被动画路径引用",string.Join("\n",risks.Take(12))+"\n\n改名可能让这些动画失效。仍然改名？","仍然改名","取消")) return "";
            ZincatSceneTools.ApplyRename(plan); return "已重命名 "+plan.Count+" 个对象，可一次撤销。";
        });
    }
    void DrawCounts()
    {
        if(GUILayout.Button("统计固定目标")) { meshCounts=ZincatSceneTools.CountMeshes(targets); message=null; }
        if(meshCounts==null) return;
        EditorGUILayout.LabelField("启用的 Renderer",meshCounts.Where(x=>x.Enabled).Sum(x=>x.Triangles).ToString("N0")+" 三角形");
        EditorGUILayout.LabelField("全部 Renderer",meshCounts.Sum(x=>x.Triangles).ToString("N0")+" 三角形");
        EditorGUILayout.LabelField("按实例计数，包含所有 LOD；不是构建后的性能评级。",EditorStyles.wordWrappedMiniLabel);
        foreach(var x in meshCounts.OrderByDescending(x=>x.Triangles)) {
            EditorGUILayout.BeginHorizontal(); EditorGUILayout.ObjectField(x.Renderer,typeof(Renderer),true);
            GUILayout.Label(x.Triangles.ToString("N0")+(x.Enabled?"":"（停用）"),GUILayout.Width(140)); EditorGUILayout.EndHorizontal();
        }
    }
    void Run(Func<string> action) { try {message=action();messageType=MessageType.None;} catch(Exception ex) {message=ex.Message;messageType=MessageType.Error;} }
}

internal static class ZincatSceneTools
{
    internal struct RenameRow { public GameObject Target; public string NewName; }
    internal struct MeshCount { public Renderer Renderer; public long Triangles; public bool Enabled; }
    internal struct SaveReport {
        public int Saved,Unnamed,Failed; public string Blocked;
        public override string ToString()=>Blocked??$"已保存 {Saved} 个场景；跳过 {Unnamed} 个未命名场景；失败 {Failed} 个。";
    }
    internal static string PreferenceKey(string dataPath) {
        using(var h=SHA256.Create()) return "Zincat.SceneTools."+BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(dataPath.Replace('\\','/').ToLowerInvariant()))).Replace("-","");
    }
    static string Key=>PreferenceKey(Application.dataPath);
    internal static bool AutoSaveEnabled {get;private set;}
    internal static float IntervalMinutes {get;private set;}
    static double nextSave; static bool saving;
    [InitializeOnLoadMethod] static void Initialize() {
        if(!EditorPrefs.HasKey(Key+".Enabled")) {
            EditorPrefs.SetBool(Key+".Enabled",false);
            EditorPrefs.SetFloat(Key+".Interval",5);
        }
        AutoSaveEnabled=EditorPrefs.GetBool(Key+".Enabled",false); IntervalMinutes=Mathf.Clamp(EditorPrefs.GetFloat(Key+".Interval",5),1,30);
        ScheduleNext(); EditorApplication.update-=Update; EditorApplication.update+=Update;
        EditorApplication.quitting-=Quitting; EditorApplication.quitting+=Quitting;
    }
    internal static void SetSaveSettings(bool enabled,float interval) {
        AutoSaveEnabled=enabled; IntervalMinutes=Mathf.Clamp(interval,1,30);
        EditorPrefs.SetBool(Key+".Enabled",enabled); EditorPrefs.SetFloat(Key+".Interval",IntervalMinutes); ScheduleNext();
    }
    static void ScheduleNext() { nextSave=EditorApplication.timeSinceStartup+IntervalMinutes*60; }
    static bool Busy=>saving||EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating||PrefabStageUtility.GetCurrentPrefabStage()!=null;
    static void Update() {
        if(!AutoSaveEnabled||Application.isBatchMode||Busy||EditorApplication.timeSinceStartup<nextSave) return;
        ScheduleNext(); var r=SaveDirtyScenes(); if(r.Failed>0) Debug.LogWarning("[Zincat 自动保存] "+r);
    }
    static void Quitting() { if(AutoSaveEnabled&&!Application.isBatchMode&&!Busy) SaveDirtyScenes(); }
    internal static SaveReport SaveDirtyScenes() {
        if(Busy) return new SaveReport {Blocked="当前正在运行、编译或编辑 Prefab，请回到场景编辑后保存。"};
        return SaveScenes(Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).ToArray(),s=>EditorSceneManager.SaveScene(s));
    }
    internal static SaveReport SaveScenes(IEnumerable<Scene> scenes,Func<Scene,bool> writer) {
        var r=new SaveReport(); saving=true;
        try { foreach(var s in scenes) {
            if(!s.IsValid()||!s.isLoaded||!s.isDirty||EditorSceneManager.IsPreviewScene(s)) continue;
            if(string.IsNullOrEmpty(s.path)) {r.Unnamed++;continue;}
            try {if(writer(s)) r.Saved++; else r.Failed++;} catch(Exception ex) {r.Failed++;Debug.LogWarning("[Zincat 自动保存] "+s.path+": "+ex.Message);}
        }} finally {saving=false;ScheduleNext();} return r;
    }
    internal static bool IsSceneObject(GameObject o)=>o!=null&&!EditorUtility.IsPersistent(o)&&o.scene.IsValid()&&!EditorSceneManager.IsPreviewScene(o.scene);
    internal static string HierarchyKey(GameObject o) {
        var indices=new List<int>(); for(var t=o.transform;t!=null;t=t.parent) indices.Add(t.GetSiblingIndex()); indices.Reverse();
        return o.scene.path+"/"+string.Join("/",indices.Select(x=>x.ToString("D8")));
    }
    internal static GameObject CreateDivider(Transform parent,string title) {
        if(parent!=null&&!IsSceneObject(parent.gameObject)) throw new InvalidOperationException("父级必须是场景对象。");
        var o=new GameObject(string.IsNullOrWhiteSpace(title)?"---------------------------------":"──── "+title.Trim()+" ────");
        if(parent!=null) {SceneManager.MoveGameObjectToScene(o,parent.gameObject.scene);o.transform.SetParent(parent,false);}
        Undo.RegisterCreatedObjectUndo(o,"创建分割线"); EditorSceneManager.MarkSceneDirty(o.scene); return o;
    }
    internal static List<RenameRow> PlanRename(IEnumerable<GameObject> objects,string prefix,string suffix,string find,string replace,bool number,int start) {
        var rows=new List<RenameRow>();int i=0;
        foreach(var o in objects.Where(IsSceneObject).Distinct()) {
            string n=string.IsNullOrEmpty(find)?o.name:o.name.Replace(find,replace??""); n=(prefix??"")+n+(suffix??"")+(number?"_"+(start+i).ToString("D2"):"");i++;
            if(n!=o.name) rows.Add(new RenameRow {Target=o,NewName=n});
        } return rows;
    }
    internal static void ApplyRename(List<RenameRow> rows) {
        if(rows.Any(x=>!IsSceneObject(x.Target)||string.IsNullOrWhiteSpace(x.NewName)||x.NewName.Contains("/"))) throw new InvalidOperationException("名称不能为空或包含 /，目标必须是场景对象。");
        Undo.IncrementCurrentGroup();int g=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("批量重命名");
        try {Undo.RecordObjects(rows.Select(x=>(UnityEngine.Object)x.Target).ToArray(),"批量重命名");
            foreach(var x in rows) {x.Target.name=x.NewName;PrefabUtility.RecordPrefabInstancePropertyModifications(x.Target);EditorSceneManager.MarkSceneDirty(x.Target.scene);}
            Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(g);
        } catch {Undo.RevertAllDownToGroup(g);throw;}
    }
    internal static List<string> FindAnimationRisks(List<RenameRow> rows) {
        var risks=new HashSet<string>();
        foreach(var row in rows) {
            var avatar=row.Target.GetComponentInParent<VRCAvatarDescriptor>();var root=avatar!=null?avatar.gameObject:row.Target.transform.root.gameObject;
            var controllers=new List<KeyValuePair<Transform,RuntimeAnimatorController>>();
            foreach(var a in root.GetComponentsInChildren<Animator>(true)) if(a.runtimeAnimatorController!=null) controllers.Add(new KeyValuePair<Transform,RuntimeAnimatorController>(a.transform,a.runtimeAnimatorController));
            foreach(var m in root.GetComponentsInChildren<ModularAvatarMergeAnimator>(true)) if(m.animator!=null) controllers.Add(new KeyValuePair<Transform,RuntimeAnimatorController>(m.pathMode==MergeAnimatorPathMode.Absolute?root.transform:(m.relativePathRoot?.Get(m)?.transform??m.transform),m.animator));
            if(avatar!=null) foreach(var l in (avatar.baseAnimationLayers??Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()).Concat(avatar.specialAnimationLayers??Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())) if(l.animatorController!=null) controllers.Add(new KeyValuePair<Transform,RuntimeAnimatorController>(avatar.transform,l.animatorController));
            foreach(var pair in controllers) {
                if(row.Target.transform==pair.Key||!row.Target.transform.IsChildOf(pair.Key)) continue;
                string path=AnimationUtility.CalculateTransformPath(row.Target.transform,pair.Key);
                foreach(var clip in pair.Value.animationClips.Distinct())
                    if(AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)).Any(b=>b.path==path||b.path.StartsWith(path+"/",StringComparison.Ordinal))) risks.Add(row.Target.name+" ← "+clip.name);
            }
        } return risks.OrderBy(x=>x).ToList();
    }
    internal static List<MeshCount> CountMeshes(IEnumerable<GameObject> roots) {
        var result=new List<MeshCount>();
        foreach(var r in roots.Where(IsSceneObject).SelectMany(x=>x.GetComponentsInChildren<Renderer>(true)).Distinct()) {
            var mesh=r is SkinnedMeshRenderer s?s.sharedMesh:r is MeshRenderer?r.GetComponent<MeshFilter>()?.sharedMesh:null;
            if(mesh==null) continue; long count=0;
            for(int i=0;i<mesh.subMeshCount;i++) {
                if(mesh.GetTopology(i)==MeshTopology.Triangles) count+=mesh.GetIndexCount(i)/3;
                else if(mesh.GetTopology(i)==MeshTopology.Quads) count+=mesh.GetIndexCount(i)/4*2;
            }
            result.Add(new MeshCount {Renderer=r,Triangles=count,Enabled=r.enabled&&r.gameObject.activeInHierarchy});
        } return result;
    }
}
