#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Zincat.AvatarSplitTest
{
    // CAU is autoReferenced=false. Keep the test add-on independent of its assembly
    // and fail explicitly if the installed CAU API no longer matches.
    [InitializeOnLoad]
    internal static class CauBridge
    {
        const string Namespace = "Anatawa12.ContinuousAvatarUploader.Editor.";
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly List<Delegate> Hooks = new List<Delegate>();
        static readonly Dictionary<string, Type> KnownTypes = new Dictionary<string, Type>();
        static readonly Dictionary<string, double> MissingTypes = new Dictionary<string, double>();
        static Func<bool> isBusy;
        static bool hooked;
        static CauBridge() { EditorApplication.delayCall += Attach; }
        public static Type Type(string name)
        {
            if (KnownTypes.TryGetValue(name, out var cached)) return cached;
            if (MissingTypes.TryGetValue(name, out var retryAt) && EditorApplication.timeSinceStartup < retryAt) return null;
            var found = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(Namespace + name, false)).FirstOrDefault(t => t != null);
            if (found != null) KnownTypes[name] = found; else MissingTypes[name] = EditorApplication.timeSinceStartup + 2;
            return found;
        }
        public static bool Available => Type("ContinuousAvatarUploader") != null && Type("AvatarUploadSetting") != null;
        internal static void ValidateApi()
        {
            if (!Available) throw new InvalidOperationException("未检测到 CAU。");
            foreach (string field in new[] { "settingsOrGroups", "temporarySettings" })
                if (Type("ContinuousAvatarUploader").GetField(field, Fields) == null) throw new InvalidOperationException("CAU 缺少队列接口：" + field);
            foreach (string field in new[] { "avatarDescriptor", "avatarName", "windows", "quest", "ios" })
                if (Type("AvatarUploadSetting").GetField(field, Fields) == null) throw new InvalidOperationException("CAU 缺少配置接口：" + field);
            if (Type("MaySceneReference")?.GetConstructor(new[] { typeof(Object) }) == null ||
                Type("Preferences")?.GetMethod("SetUploadFor", Fields) == null)
                throw new InvalidOperationException("CAU 的场景引用或平台接口不匹配。");
            CauExtraUploads.ValidateApi();
            Attach();
            if (!hooked) throw new InvalidOperationException("CAU 蓝图保存事件无法接入。");
        }
        public static bool Busy
        {
            get
            {
                if (isBusy == null)
                {
                    var method = Type("UploadOrchestrator")?.GetMethod("IsUploadInProgress", Fields);
                    if (method != null) isBusy = (Func<bool>)method.CreateDelegate(typeof(Func<bool>));
                }
                return isBusy != null && isBusy();
            }
        }
        static object Get(object target, string name) => target.GetType().GetField(name, Fields)?.GetValue(target);
        static void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, Fields);
            if (field == null) throw new InvalidOperationException("CAU 接口不匹配：" + name);
            field.SetValue(target, value);
        }
        static void Attach()
        {
            if (hooked || !Available) return;
            var orchestrator = Type("UploadOrchestrator");
            var ev = orchestrator?.GetEvent("OnUploadSingleAvatarFinished", Fields);
            if (ev == null) return;
            var arguments = ev.EventHandlerType.GetMethod("Invoke").GetParameters()
                .Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            if (arguments.Length != 2) return;
            var call = Expression.Call(typeof(CauBridge).GetMethod(nameof(OnFinished), Fields),
                Expression.Convert(arguments[0], typeof(object)), Expression.Convert(arguments[1], typeof(object)));
            var handler = Expression.Lambda(ev.EventHandlerType, call, arguments).Compile();
            ev.AddEventHandler(null, handler); Hooks.Add(handler); hooked = true;
        }
        static void OnFinished(object progress, object setting)
        {
            // CAU 0.3.12 raises Finished even after a failed item when continuing.
            // Inspect its error list before recording success.
            bool failed = false;
            if (Get(progress, "uploadErrors") is IEnumerable errors)
                foreach (var error in errors)
                    if (ReferenceEquals(Get(error, "uploadingAvatar"), setting)) failed = true;
            var plans = SplitConfigStore.LoadedPlans().Concat(AssetDatabase.FindAssets("t:AvatarSplitPlan")
                .Select(guid => AssetDatabase.LoadAssetAtPath<AvatarSplitPlan>(AssetDatabase.GUIDToAssetPath(guid))));
            foreach (var plan in plans)
            {
                foreach (var group in plan.groups)
                {
                    if (string.IsNullOrEmpty(group.cauSettingGuid) ||
                        AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath((Object)setting)) != group.cauSettingGuid) continue;
                    var reference = Get(setting, "avatarDescriptor");
                    var descriptor = reference?.GetType().GetMethod("TryResolve", Fields)?.Invoke(reference, null) as VRCAvatarDescriptor;
                    if (descriptor) RecordBlueprint(plan, group, descriptor.gameObject, !failed);
                }
            }
        }
        internal static void RecordBlueprint(AvatarSplitPlan plan, SplitGroup group, GameObject avatar, bool success)
        {
            var id = avatar.GetComponent<PipelineManager>()?.blueprintId;
            if (string.IsNullOrEmpty(id)) return;
            var source = SplitIds.Resolve(plan.sourceId) as GameObject;
            if (source && source.GetComponent<PipelineManager>()?.blueprintId == id && group.blueprintId != id)
                throw new InvalidOperationException("生成模型使用了源模型的蓝图号，已阻止关联；请检查拆分模型的 Pipeline Manager。");
            if (!string.IsNullOrEmpty(group.blueprintId) && group.blueprintId != id)
                throw new InvalidOperationException("生成模型蓝图号被更换，与该分组原记录不一致，请先检查；未覆盖分组记录。");
            if (plan.groups.Any(g => g != group && g.blueprintId == id))
            { Debug.LogError("[分组拆分测试] 蓝图号与另一分组重复，未写回配置。"); return; }
            group.blueprintId = id; group.uploadSucceeded = success;
            EditorUtility.SetDirty(avatar.GetComponent<PipelineManager>());
            var scene = avatar.scene;
            if (scene.path == group.generatedScene && scene.isLoaded)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            SplitIds.Save(plan);
            Debug.Log("[分组拆分测试] " + group.name + (success ? " 上传成功，已保存蓝图号。" : " 上传未完成，已保留分配的蓝图号供重试。"));
        }
        public static void SyncBlueprints(AvatarSplitPlan plan, IEnumerable<SplitGroup> groups = null)
        {
            foreach (var group in groups ?? plan.groups)
            {
                var avatar = SplitIds.Avatar(group);
                if (!avatar && !string.IsNullOrEmpty(group.generatedScene))
                {
                    var scene = SceneManager.GetSceneByPath(group.generatedScene);
                    if (!scene.isLoaded && System.IO.File.Exists(group.generatedScene))
                        EditorSceneManager.OpenScene(group.generatedScene, OpenSceneMode.Additive);
                    avatar = SplitIds.Avatar(group);
                }
                var id = avatar ? avatar.GetComponent<PipelineManager>()?.blueprintId : null;
                if (!string.IsNullOrEmpty(id) && id != group.blueprintId) RecordBlueprint(plan, group, avatar, false);
            }
        }
        internal static SplitGroup[] SelectedUploads(AvatarSplitPlan plan) => plan ? plan.groups.Where(g => g.UploadSelected).ToArray() : Array.Empty<SplitGroup>();
        public static void PrepareQueue(AvatarSplitPlan plan)
        {
            if (!Available || Busy || CauExtraUploads.Busy || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("CAU 未安装、正在上传，或 Unity 正处于运行模式。");
            ValidateApi();
            if (!hooked) throw new InvalidOperationException("当前 CAU 无法连接蓝图保存回调，已停止交接。");
            var groups = SelectedUploads(plan);
            if (groups.Length == 0) throw new InvalidOperationException("请至少勾选一个要上传的分组。");
            if ((bool)Type("Preferences").GetProperty("TakeThumbnailInPlaymodeByDefault", Fields).GetValue(null))
                throw new InvalidOperationException("请在 CAU 关闭“默认在运行模式拍摄封面”后再交接；本扩展不会进入运行模式。");
            SyncBlueprints(plan, groups);
            var avatars = new List<GameObject>();
            var covers = groups.ToDictionary(g => g.id, g => SplitPhoto.UploadCover(plan, g));
            foreach (var group in groups)
            {
                var avatar = SplitIds.Avatar(group);
                if (!avatar || group.generatedRevision != group.revision) throw new InvalidOperationException(group.name + " 尚未生成，或分组已修改，请重新生成。");
                if (avatar.name != group.name.Trim()) throw new InvalidOperationException("模型名称与分组不一致，请重新生成或恢复名称。");
                var id = avatar.GetComponent<PipelineManager>()?.blueprintId ?? "";
                if (id != (group.blueprintId ?? "")) throw new InvalidOperationException("蓝图号与分组记录不一致，请检查。");
                if (avatar.scene.isDirty && !EditorSceneManager.SaveScene(avatar.scene)) throw new InvalidOperationException("当前场景保存失败。");
                avatars.Add(avatar);
            }
            if (groups.Where(g => !string.IsNullOrEmpty(g.blueprintId)).GroupBy(g => g.blueprintId).Any(g => g.Count() > 1))
                throw new InvalidOperationException("分组蓝图号重复，已停止。");
            if (!EditorUtility.DisplayDialog("准备交给 CAU", "仅将勾选的分组按以下顺序加入 CAU（Windows）：\n\n" +
                string.Join("\n", groups.Select((g, i) => (i + 1) + ". " + g.UploadName + " · " + (string.IsNullOrEmpty(g.blueprintId) ? "新建私有 Avatar" : "更新已有 Avatar") + (covers[g.id] ? " · 已确认封面" : " · 保留原封面"))) +
                "\n\n这一步只打开上传队列。请检查 CAU，然后亲自点击“开始上传”。", "打开 CAU 队列", "取消")) return;
            AvatarSplitBuilder.EnsureFolder(AvatarSplitBuilder.Root + "/UploadSettings");
            var settingType = Type("AvatarUploadSetting");
            var settings = Array.CreateInstance(Type("AvatarUploadSettingOrGroup"), avatars.Count);
            for (int i = 0; i < avatars.Count; i++)
            {
                var g = groups[i];
                var setting = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(g.cauSettingGuid ?? ""), settingType) as ScriptableObject;
                if (!setting)
                {
                    setting = ScriptableObject.CreateInstance(settingType);
                    var path = AssetDatabase.GenerateUniqueAssetPath(AvatarSplitBuilder.Root + "/UploadSettings/CAU_" + SplitConfigStore.Key(plan) + "_" + g.id + ".asset");
                    AssetDatabase.CreateAsset(setting, path); g.cauSettingGuid = AssetDatabase.AssetPathToGUID(path);
                }
                setting.name = g.name;
                Set(setting, "avatarName", g.name);
                Set(setting, "avatarDescriptor", Activator.CreateInstance(Type("MaySceneReference"), new object[] { avatars[i].GetComponent<VRCAvatarDescriptor>() }));
                foreach (var field in new[] { "windows", "quest", "ios" })
                {
                    var info = Get(setting, field); Set(info, "enabled", field == "windows");
                    Set(info, "updateImage", field == "windows" && string.IsNullOrEmpty(g.blueprintId));
                    Set(info, "imageTakeEditorMode", Enum.Parse(Type("ImageTakeEditorMode"), "InEditMode"));
                    Set(info, "versioningEnabled", false); Set(info, "gitEnabled", false);
                }
                EditorUtility.SetDirty(setting); settings.SetValue(setting, i);
                CauExtraUploads.Prepare(plan, g, setting, covers[g.id]);
            }
            var window = Type("ContinuousAvatarUploader").GetMethod("OpenWindow", Fields).Invoke(null, null) as EditorWindow;
            Set(window, "settingsOrGroups", settings);
            ((IList)Get(window, "temporarySettings")).Clear();
            var targetType = Type("TargetPlatform");
            var setPlatform = Type("Preferences").GetMethod("SetUploadFor", Fields);
            foreach (string platform in new[] { "Windows", "Android", "iOS" })
                setPlatform.Invoke(null, new[] { Enum.Parse(targetType, platform), (object)(platform == "Windows") });
            // The final upload action remains in CAU. Never call StartUpload here.
            window.Show(); window.Repaint(); SplitIds.Save(plan); AssetDatabase.SaveAssets();
        }

        public static void RetargetExistingSettings(AvatarSplitPlan plan)
        {
            if (!Available) return;
            foreach (var group in plan.groups)
            {
                if (string.IsNullOrEmpty(group.cauSettingGuid)) continue;
                var setting = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(group.cauSettingGuid), Type("AvatarUploadSetting")) as ScriptableObject;
                var avatar = SplitIds.Avatar(group);
                if (!setting || !avatar || group.generatedRevision != group.revision) continue;
                Set(setting, "avatarDescriptor", Activator.CreateInstance(Type("MaySceneReference"), new object[] { avatar.GetComponent<VRCAvatarDescriptor>() }));
                Set(setting, "avatarName", group.name); setting.name = group.name;
                EditorUtility.SetDirty(setting); AssetDatabase.SaveAssetIfDirty(setting);
            }
        }
        internal static void AssignBlueprint(AvatarSplitPlan plan, SplitGroup group, string value)
        {
            if (Busy || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("请等待上传或编译结束，并退出运行模式。");
            var id = (value ?? "").Trim();
            if (id.Length != 0 && !System.Text.RegularExpressions.Regex.IsMatch(id,
                @"^avtr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"))
                throw new InvalidOperationException("蓝图号格式应为 avtr_ 加完整 UUID；留空可在下次上传时新建。");
            id = id.ToLowerInvariant();
            if (id.Length > 0 && plan.groups.Any(g => g != group && string.Equals(g.blueprintId, id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("此蓝图号已分配给另一分组，请使用不同蓝图号。");
            if ((group.blueprintId ?? "") == id) return;
            var avatar = SplitIds.Avatar(group);
            Undo.RecordObject(plan, "手动分配蓝图号");
            if (avatar)
            {
                var pipeline = avatar.GetComponent<PipelineManager>();
                if (!pipeline) pipeline = Undo.AddComponent<PipelineManager>(avatar);
                Undo.RecordObject(pipeline, "更新副本蓝图号"); pipeline.blueprintId = id;
                EditorUtility.SetDirty(pipeline); EditorSceneManager.MarkSceneDirty(avatar.scene);
                if (!string.IsNullOrEmpty(avatar.scene.path)) EditorSceneManager.SaveScene(avatar.scene);
            }
            group.blueprintId = id; group.uploadSucceeded = false; SplitIds.Save(plan);
        }
        internal static void PruneDeletedSettings(HashSet<string> paths)
        {
            if (!Available || paths.Count == 0) return;
            foreach (var window in Resources.FindObjectsOfTypeAll(Type("ContinuousAvatarUploader")))
            {
                if (!(Get(window, "settingsOrGroups") is IEnumerable current)) continue;
                var kept = current.Cast<Object>().Where(o => o && !paths.Contains(AssetDatabase.GetAssetPath(o))).ToArray();
                var values = Array.CreateInstance(Type("AvatarUploadSettingOrGroup"), kept.Length);
                for (int i = 0; i < kept.Length; i++) values.SetValue(kept[i], i);
                Set(window, "settingsOrGroups", values); EditorUtility.SetDirty(window);
            }
        }
    }
}
#endif

