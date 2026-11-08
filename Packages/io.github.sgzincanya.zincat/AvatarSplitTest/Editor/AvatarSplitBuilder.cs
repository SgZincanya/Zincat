#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using Zincat.Clothing;
using Object = UnityEngine.Object;

namespace Zincat.AvatarSplitTest
{
    internal static class AvatarSplitBuilder
    {
        public const string Root = "Assets/Zincat/AvatarSplitTest";
        public static WardrobeGeneratedSwitch Collection(GameObject avatar)
        {
            var groups = avatar.GetComponentsInChildren<WardrobeGeneratedSwitch>(true);
            if (groups.Length != 1) throw new InvalidOperationException("测试版要求源角色中恰好有一个已保存的衣服切换总菜单。");
            var c = groups[0];
            if (!c.IsSeparateCollection || !c.UsesInstallTargets || !c.DirectOutfitToggles || c.UsesMergeMotion)
                throw new InvalidOperationException("测试版仅支持新版直接切换衣服结构，请先用原衣服插件保存总菜单。");
            if (c.Entries == null || c.Entries.Count == 0 || c.Entries.Any(e => e == null || !e.OutfitRoot || !e.WearNode || !e.PartMenuRoot || !e.PartsNode))
                throw new InvalidOperationException("原衣服总菜单记录不完整，请先在原插件修复并保存。");
            if (c.Entries.Select(e => e.OutfitRoot).Distinct().Count() != c.Entries.Count)
                throw new InvalidOperationException("原衣服总菜单中存在重复衣服。");
            return c;
        }

        // Uses sibling-index paths only while source and freshly instantiated clone
        // have the same hierarchy. Saved selections use GlobalObjectId instead.
        static Transform Counterpart(Transform source, Transform sourceRoot, Transform copyRoot)
        {
            var indices = new Stack<int>();
            for (var t = source; t != sourceRoot; t = t.parent)
            {
                if (t == null) throw new InvalidOperationException("衣服引用不属于源角色。");
                indices.Push(t.GetSiblingIndex());
            }
            var result = copyRoot;
            foreach (int i in indices) result = result.GetChild(i);
            return result;
        }

        public static GameObject CreateVariant(GameObject source, IEnumerable<GameObject> selection,
            string name, string blueprint, Scene target, out List<string> removedPaths, out List<string> issues)
        {
            var sourceCollection = Collection(source);
            var selected = selection.ToArray();
            if (selected.Length == 0 || selected.Any(x => !x) || selected.Distinct().Count() != selected.Length ||
                selected.Any(x => !sourceCollection.Entries.Any(e => e.OutfitRoot == x)))
                throw new InvalidOperationException("分组含有空衣服、重复项或未登记在当前总菜单中的衣服，请重新读取选择。");
            if (sourceCollection.UsesPlaceholderOutfit && sourceCollection.PlaceholderOutfit && selected.Contains(sourceCollection.PlaceholderOutfit))
                throw new InvalidOperationException("占位衣服由工具自动保留，不要加入正常衣服列表。");
            var copy = Object.Instantiate(source);
            copy.SetActive(false);
            copy.name = name;
            try
            {
                SceneManager.MoveGameObjectToScene(copy, target);
                if (PrefabUtility.IsPartOfPrefabInstance(copy)) PrefabUtility.UnpackPrefabInstance(copy, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                var collection = Counterpart(sourceCollection.transform, source.transform, copy.transform).GetComponent<WardrobeGeneratedSwitch>();
                bool hasPlaceholder = collection.UsesPlaceholderOutfit && collection.PlaceholderOutfit;
                collection.UsesPlaceholderOutfit = hasPlaceholder;
                if (!hasPlaceholder) collection.PlaceholderOutfit = null;
                var keep = new HashSet<GameObject>(selected.Select(x => Counterpart(x.transform, source.transform, copy.transform).gameObject));
                var entries = collection.Entries.Where(e => keep.Contains(e.OutfitRoot)).ToList();
                var removed = new HashSet<GameObject>();
                foreach (var entry in collection.Entries.Where(e => !keep.Contains(e.OutfitRoot)))
                    foreach (var node in new[] { entry.OutfitRoot, entry.PartMenuRoot, entry.MenuNode, entry.PartsNode })
                        if (node) removed.Add(node);
                // Also remove registered outfits outside the saved collection.
                foreach (var menu in copy.GetComponentsInChildren<WardrobeGeneratedMenu>(true))
                    if (menu.ClothingRoot && !keep.Contains(menu.ClothingRoot) && menu.ClothingRoot != collection.PlaceholderOutfit)
                    { removed.Add(menu.ClothingRoot); removed.Add(menu.gameObject); }
                var protectedNodes = entries.SelectMany(e => new[] { e.OutfitRoot, e.PartMenuRoot, e.WearNode, e.PartsNode })
                    .Concat(new[] { copy, collection.gameObject, collection.PlaceholderOutfit }).Where(x => x).ToArray();
                foreach (var node in removed)
                {
                    if (!node.transform.IsChildOf(copy.transform) || protectedNodes.Any(p => p == node || p.transform.IsChildOf(node.transform)))
                        throw new InvalidOperationException("排除的衣服或菜单与保留对象层级重叠，无法自动拆分。");
                }
                removedPaths = removed.Select(x => AnimationUtility.CalculateTransformPath(x.transform, copy.transform)).Distinct().ToList();
                var defaultOutfit = keep.Contains(collection.DefaultOutfit) ? collection.DefaultOutfit : entries[0].OutfitRoot;
                entries = entries.OrderBy(e => e.OutfitRoot == defaultOutfit ? 0 : 1).ToList();
                collection.AvatarRoot = copy;
                collection.Entries = entries;
                collection.DefaultOutfit = defaultOutfit;
                collection.GeneratedMotion = null;
                collection.GeneratedClips.Clear();
                var parameters = collection.GetComponent<ModularAvatarParameters>();
                if (!parameters || parameters.parameters == null) throw new InvalidOperationException("总菜单缺少 MA 参数声明。");
                int parameterIndex = parameters.parameters.FindIndex(p => p.nameOrPrefix == collection.ParameterName && !p.isPrefix);
                if (parameterIndex < 0) throw new InvalidOperationException("未找到总菜单的换衣参数。");
                var parameter = parameters.parameters[parameterIndex];
                parameter.defaultValue = 0;
                parameter.hasExplicitDefaultValue = true;
                parameters.parameters[parameterIndex] = parameter;
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    var item = entry.WearNode.GetComponent<ModularAvatarMenuItem>();
                    var toggle = entry.WearNode.GetComponent<ModularAvatarObjectToggle>();
                    if (!item || item.Control == null || !toggle) throw new InvalidOperationException("保留衣服缺少换衣按钮或开关。");
                    item.Control.value = i;
                    item.Control.parameter = new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.Parameter { name = collection.ParameterName };
                    item.automaticValue = false;
                    toggle.Objects = entries.Select(e => new ToggledObject { Object = new AvatarObjectReference(e.OutfitRoot), Active = e == entry }).ToList();
                    if (hasPlaceholder)
                        toggle.Objects.Add(new ToggledObject { Object = new AvatarObjectReference(collection.PlaceholderOutfit), Active = false });
                    entry.OutfitRoot.SetActive((!hasPlaceholder || !collection.PlaceholderOutfit.activeSelf) && entry.OutfitRoot == defaultOutfit);
                }
                var pipeline = copy.GetComponent<PipelineManager>();
                if (!pipeline) pipeline = copy.AddComponent<PipelineManager>();
                pipeline.blueprintId = blueprint ?? "";
                CleanRemovedShapeTargets(copy, removed);
                issues = new List<string>();
                foreach (var node in removed.Where(x => !removed.Any(p => p != x && x.transform.IsChildOf(p.transform))).ToArray())
                    Object.DestroyImmediate(node);
                foreach (var config in copy.GetComponentsInChildren<AvatarSplitConfig>(true))
                    Object.DestroyImmediate(config.gameObject);
                RemoveUnsavableChildren(copy);
                return copy;
            }
            catch { Object.DestroyImmediate(copy); throw; }
        }

        internal static int RemoveUnsavableChildren(GameObject copy)
        {
            // Missing nested prefabs may have DontSaveInEditor on their GameObject,
            // but not their Transform. After unpacking that serializes an orphan
            // Transform with m_GameObject: 0. Remove only these unsavable objects
            // from generated copies; ordinary hidden menu nodes must be retained.
            var nodes = copy.GetComponentsInChildren<Transform>(true)
                .Where(t => t && t != copy.transform && t.gameObject &&
                    (t.gameObject.hideFlags & HideFlags.DontSaveInEditor) != 0)
                .Select(t => t.gameObject).ToArray();
            int count = 0;
            foreach (var node in nodes)
                if (node) { Object.DestroyImmediate(node); count++; }
            return count;
        }

        static void CleanRemovedShapeTargets(GameObject root, HashSet<GameObject> removed)
        {
            // Only prune the known MA entries whose targets are about to be
            // deleted. Do not walk SerializedObject properties for every component.
            var removedTransforms = new HashSet<Transform>();
            foreach (var node in removed)
                foreach (var t in node.GetComponentsInChildren<Transform>(true)) removedTransforms.Add(t);
            foreach (var changer in root.GetComponentsInChildren<ModularAvatarShapeChanger>(true))
            {
                if (removedTransforms.Contains(changer.transform) || changer.Shapes == null) continue;
                changer.Shapes.RemoveAll(shape =>
                {
                    var target = shape?.Object?.Get(changer);
                    return target && removedTransforms.Contains(target.transform);
                });
            }
        }

        public static List<string> Inspect(GameObject avatar, List<string> removedPaths)
        {
            var issues = new List<string>();
            foreach (var t in avatar.GetComponentsInChildren<Transform>(true))
            {
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) > 0)
                    issues.Add(t.name + " 上存在丢失脚本。");
                foreach (var component in t.GetComponents<Component>())
                {
                    if (!component || component is Transform) continue;
                    using (var so = new SerializedObject(component))
                    {
                        var p = so.GetIterator();
                        while (p.Next(true))
                        {
                            if (p.propertyType != SerializedPropertyType.ObjectReference) continue;
                            if (!p.objectReferenceValue && p.objectReferenceInstanceIDValue != 0)
                                issues.Add($"{t.name} / {component.GetType().Name}.{p.propertyPath} 引用已丢失。");
                            var value = p.objectReferenceValue;
                            var go = value as GameObject ?? (value as Component)?.gameObject;
                            if (go && go.scene.IsValid() && !go.transform.IsChildOf(avatar.transform))
                                issues.Add($"{t.name} / {component.GetType().Name}.{p.propertyPath} 引用了角色外的场景对象 {go.name}。");
                        }
                    }
                }
            }
            var dependencies = EditorUtility.CollectDependencies(new Object[] { avatar });
            var clips = new HashSet<AnimationClip>(dependencies.OfType<AnimationClip>());
            foreach (var controller in dependencies.OfType<RuntimeAnimatorController>())
                foreach (var clip in controller.animationClips) if (clip) clips.Add(clip);
            foreach (var animator in avatar.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController)
                    foreach (var clip in animator.runtimeAnimatorController.animationClips) if (clip) clips.Add(clip);
            foreach (var clip in clips)
                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                    if (removedPaths.Any(path => binding.path == path || binding.path.StartsWith(path + "/", StringComparison.Ordinal)))
                        issues.Add($"动画 {clip.name} 仍控制已排除路径 {binding.path}；请检查共享动画。");
            return issues.Distinct().ToList();
        }

        public static void Generate(AvatarSplitPlan plan)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || CauBridge.Busy)
                throw new InvalidOperationException("请等待编译或上传结束，并退出运行模式。");
            var source = SplitIds.Resolve(plan.sourceId) as GameObject;
            if (!source || !source.GetComponent<VRCAvatarDescriptor>()) throw new InvalidOperationException("请打开源场景并重新绑定源角色。");
            SplitSourceStore.EnsureSaved(source);
            plan.sourceScene = source.scene.path;
            SplitNaming.Apply(plan, source);
            Collection(source);
            if (plan.groups.Count == 0 || plan.groups.Any(g => string.IsNullOrWhiteSpace(g.name) || g.outfitIds.Count == 0))
                throw new InvalidOperationException("每个分组都需要名称和至少一套衣服。");
            if (plan.groups.Select(g => g.name.Trim()).Distinct().Count() != plan.groups.Count)
                throw new InvalidOperationException("请给各分组使用不同名称。");
            if (plan.groups.Where(g => !string.IsNullOrEmpty(g.blueprintId)).GroupBy(g => g.blueprintId).Any(g => g.Count() > 1))
                throw new InvalidOperationException("不同分组记录了同一个蓝图号，已阻止生成，请先检查配置。");
            var selections = plan.groups.Select(g => g.outfitIds.Select(id => SplitIds.Resolve(id) as GameObject).ToArray()).ToArray();
            if (selections.Any(s => s.Any(x => !x))) throw new InvalidOperationException("部分衣服引用已失效，请刷新衣服菜单后重新勾选。");
            CauBridge.SyncBlueprints(plan);
            DisableGeneratedPreviews();
            var previousActive = SceneManager.GetActiveScene();
            var scene = source.scene;
            if (string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("请先保存源场景。");
            var planGuid = SplitConfigStore.Key(plan);
            if (string.IsNullOrEmpty(planGuid)) throw new InvalidOperationException("请先创建一个分组方案。");
            var roots = scene.GetRootGameObjects().Select(g => g.GetComponent<AvatarSplitOutputRoot>())
                .Where(r => r && r.PlanGuid == planGuid && (!r.Source || r.Source == source)).ToArray();
            if (roots.Length > 1) throw new InvalidOperationException("当前方案有多个拆分结果节点，请先保留一个。");
            var output = roots.FirstOrDefault();
            if (output && source.transform.IsChildOf(output.transform))
                throw new InvalidOperationException("源角色位于本方案的拆分结果中，请绑定原始角色或新建方案。");
            var previousCopies = output ? output.Generated.Where(g => g && g.transform.parent == output.transform).ToArray() : Array.Empty<GameObject>();
            SceneManager.SetActiveScene(scene);
            var copies = new List<GameObject>();
            var paths = new List<List<string>>();
            var reports = new List<List<string>>();
            int undoGroup = -1;
            try
            {
                for (int i = 0; i < plan.groups.Count; i++)
                {
                    var g = plan.groups[i];
                    if (!Application.isBatchMode)
                        EditorUtility.DisplayProgressBar("生成衣服版本", "正在生成 " + g.name, (float)i / plan.groups.Count);
                    var copy = CreateVariant(source, selections[i], g.name.Trim(), g.blueprintId, scene, out var removed, out var issues);
                    copies.Add(copy); paths.Add(removed); reports.Add(issues);
                    copy.transform.position += Vector3.right * ((i + 1) * 2f);
                }
                Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("生成或更新衣服拆分结果");
                Undo.RecordObject(plan, "更新拆分结果关联");
                if (!output)
                {
                    var container = new GameObject("拆分结果 · " + plan.name);
                    Undo.RegisterCreatedObjectUndo(container, "创建拆分结果节点");
                    output = container.AddComponent<AvatarSplitOutputRoot>();
                    if (!output) throw new InvalidOperationException("拆分结果组件未加载，请等待 Unity 编译完成。");
                    output.PlanGuid = planGuid;
                }
                output.Source = source;
                Undo.RecordObject(output, "更新生成模型列表");
                foreach (var copy in copies)
                {
                    Undo.RegisterCreatedObjectUndo(copy, "生成拆分模型");
                    Undo.SetTransformParent(copy.transform, output.transform, "归入拆分结果节点");
                }
                // All new copies are ready before replacing the previous ones.
                foreach (var old in previousCopies) Undo.DestroyObjectImmediate(old);
                output.Generated = new List<GameObject>(copies);
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("当前场景保存失败。");
                plan.outputRootId = SplitIds.Of(output.gameObject);
                for (int i = 0; i < plan.groups.Count; i++)
                {
                    var g = plan.groups[i];
                    g.generatedAvatarId = SplitIds.Of(copies[i]); g.generatedScene = scene.path;
                    g.generatedRevision = g.revision; g.removedPaths = paths[i]; g.issues = reports[i];
                }
                SplitIds.Save(plan);
                try { CauBridge.RetargetExistingSettings(plan); }
                catch (Exception e) { Debug.LogWarning("[分组拆分测试] 模型已更新；CAU 配置需重新交接：" + e.Message); }
                Undo.CollapseUndoOperations(undoGroup);
                Selection.objects = copies.Cast<Object>().ToArray();
            }
            catch
            {
                if (undoGroup >= 0) Undo.RevertAllDownToGroup(undoGroup);
                foreach (var copy in copies) if (copy) Object.DestroyImmediate(copy);
                throw;
            }
            finally
            {
                if (!Application.isBatchMode) EditorUtility.ClearProgressBar();
                if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
            }
        }

        public static void EnsureFolder(string path)
        {
            var parts = path.Split('/'); var parent = parts[0];
            foreach (var part in parts.Skip(1)) { var next = parent + "/" + part; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(parent, part); parent = next; }
        }

        public static void DisableGeneratedPreviews()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var container = root.GetComponent<AvatarSplitOutputRoot>();
                    if (container)
                        foreach (var copy in container.Generated)
                            if (copy && copy.transform.parent == container.transform && copy.activeSelf) copy.SetActive(false);
                    // Existing results from the old separate-scene version.
                    if (scene.path.StartsWith(Root + "/Generated/", StringComparison.Ordinal) &&
                        root.GetComponent<VRCAvatarDescriptor>() && root.activeSelf) root.SetActive(false);
                }
            }
        }
    }
}
#endif
