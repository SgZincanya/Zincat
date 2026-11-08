#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Zincat.Clothing;
using Object = UnityEngine.Object;

namespace Zincat.AvatarSplitTest
{
    internal static class AvatarSplitSelfTest
    {
        const string Request = "Library/ZincatAvatarSplitSelfTest.request";
        public const string Result = "Library/ZincatAvatarSplitSelfTest.result.txt";
        [InitializeOnLoadMethod]
        static void RequestedTest()
        {
            if (!File.Exists(Request)) return;
            EditorApplication.update += RunWhenReady;
        }
        static void RunWhenReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || CauBridge.Busy) return;
            EditorApplication.update -= RunWhenReady;
            if (!File.Exists(Request)) return;
            File.Delete(Request); Run();
            if (!CauBridge.Busy && !EditorApplication.isPlayingOrWillChangePlaymode) AvatarSplitBuilder.DisableGeneratedPreviews();
            AvatarSplitWindow.Open();
        }
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || CauBridge.Busy) return;
            var previous = SceneManager.GetActiveScene();
            var previousSelection = Selection.objects;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var lines = new List<string>();
            string testFolder = null;
            Undo.IncrementCurrentGroup();
            int testUndoGroup = Undo.GetCurrentGroup();
            try
            {
                var source = new GameObject("SelfTestSource"); source.AddComponent<Animator>(); source.AddComponent<VRCAvatarDescriptor>();
                source.AddComponent<PipelineManager>().blueprintId = "avtr_11111111-1111-1111-1111-111111111111";
                var placeholder = Child(source, "Placeholder"); placeholder.SetActive(true);
                var menu = Child(source, "衣服菜单");
                var collection = menu.AddComponent<WardrobeGeneratedSwitch>();
                collection.AvatarRoot = source; collection.IsSeparateCollection = true; collection.UsesInstallTargets = true;
                collection.DirectOutfitToggles = true; collection.UsesPlaceholderOutfit = true;
                collection.PlaceholderOutfit = placeholder; collection.ParameterName = "Outfit";
                menu.AddComponent<ModularAvatarParameters>().parameters = new List<ParameterConfig> {
                    new ParameterConfig { nameOrPrefix = "Outfit", syncType = ParameterSyncType.Int, defaultValue = 2, hasExplicitDefaultValue = true }
                };
                foreach (string name in new[] { "A", "B", "C", "D" })
                {
                    var outfit = Child(source, name); var parts = Child(source, name + "菜单");
                    var partMenuItem = parts.AddComponent<ModularAvatarMenuItem>();
                    partMenuItem.Control = new VRCExpressionsMenu.Control { name = "旧名字", type = VRCExpressionsMenu.Control.ControlType.SubMenu };
                    partMenuItem.label = name == "C" ? "连衣裙（菜单名）" : "";
                    var marker = parts.AddComponent<WardrobeGeneratedMenu>(); marker.ClothingRoot = outfit;
                    var wear = Child(parts, "换衣"); marker.OutfitSwitchNode = wear;
                    wear.AddComponent<ModularAvatarMenuItem>().Control = new VRCExpressionsMenu.Control {
                        name = "换衣", type = VRCExpressionsMenu.Control.ControlType.Toggle,
                        parameter = new VRCExpressionsMenu.Control.Parameter { name = "Outfit" }, value = collection.Entries.Count };
                    wear.AddComponent<ModularAvatarObjectToggle>();
                    collection.Entries.Add(new WardrobeSwitchLink { OutfitRoot = outfit, PartMenuRoot = parts, WearNode = wear, PartsNode = Child(menu, name + "入口") });
                }
                collection.DefaultOutfit = collection.Entries[0].OutfitRoot;
                var menuChoices = AvatarOutfitMenus.Read(source);
                Assert(menuChoices.Count == 4 && menuChoices[0].Outfit == collection.Entries[0].OutfitRoot && menuChoices[0].Menu == collection.Entries[0].PartMenuRoot,
                    "直接从 Avatar 总菜单读取四套衣服及分件菜单关联");
                Assert(menuChoices[0].Name == "A菜单" && menuChoices[2].Name == "连衣裙（菜单名）",
                    "衣服列表显示对应菜单名，支持 MA 自定义标签，不使用衣服物体名或过时 Control.name");
                var shapeChanger = collection.Entries[0].OutfitRoot.AddComponent<ModularAvatarShapeChanger>();
                shapeChanger.Shapes = new List<ChangedShape> {
                    new ChangedShape { Object = new AvatarObjectReference(collection.Entries[2].OutfitRoot), ShapeName = "排除衣服" },
                    new ChangedShape { Object = new AvatarObjectReference(collection.Entries[0].OutfitRoot), ShapeName = "保留衣服" }
                };
                var a = AvatarSplitBuilder.CreateVariant(source, collection.Entries.Take(2).Select(e => e.OutfitRoot), "测试1", "", scene, out var removedAB, out var issuesAB);
                var b = AvatarSplitBuilder.CreateVariant(source, collection.Entries.Skip(2).Select(e => e.OutfitRoot), "测试2", "avtr_22222222-2222-2222-2222-222222222222", scene, out _, out var issuesCD);
                Assert(a.transform.Find("A") && a.transform.Find("B") && !a.transform.Find("C") && !a.transform.Find("D"), "AB 模型实际移除 CD");
                Assert(b.transform.Find("C") && b.transform.Find("D") && !b.transform.Find("A") && !b.transform.Find("B"), "CD 模型实际移除 AB");
                Assert(!a.transform.Find("C菜单") && !a.transform.Find("衣服菜单/C入口"), "排除衣服的分件菜单和入口已移除");
                Assert(source.transform.Find("C") && collection.Entries.Count == 4 && source.GetComponent<PipelineManager>().blueprintId.EndsWith("111111111111"), "源模型衣服、菜单和蓝图未修改");
                Assert(string.IsNullOrEmpty(a.GetComponent<PipelineManager>().blueprintId), "首次生成不继承源蓝图");
                Assert(b.GetComponent<PipelineManager>().blueprintId.EndsWith("222222222222"), "再次生成沿用分组独立蓝图");
                Assert(!a.activeSelf && !b.activeSelf && source.activeSelf, "生成副本默认关闭，源角色仍启用，避免多个副本同时预览");
                Assert(a.GetComponentInChildren<ModularAvatarShapeChanger>(true).Shapes.Count == 1 &&
                    a.GetComponentInChildren<ModularAvatarShapeChanger>(true).Shapes[0].ShapeName == "保留衣服" && shapeChanger.Shapes.Count == 2,
                    "仅清理副本中针对被删除衣服的 MA Shape Changer 条目，不影响保留衣服及源模型");
                foreach (var avatar in new[] { a, b })
                {
                    var c = AvatarSplitBuilder.Collection(avatar);
                    Assert(c.Entries.Count == 2 && c.PlaceholderOutfit && c.PlaceholderOutfit.transform.IsChildOf(avatar.transform), "保留占位衣服与两个换衣项：" + avatar.name);
                    Assert(c.GetComponent<ModularAvatarParameters>().parameters[0].defaultValue == 0, "重设参数默认值：" + avatar.name);
                    Assert(c.Entries.Select(e => e.WearNode.GetComponent<ModularAvatarMenuItem>().Control.value).SequenceEqual(new[] { 0f, 1f }), "换衣参数连续编号：" + avatar.name);
                    Assert(c.Entries.All(e => e.WearNode.GetComponent<ModularAvatarObjectToggle>().Objects.Count == 3), "互斥开关仅引用本组与占位衣服：" + avatar.name);
                }
                Assert(issuesAB.Count == 0 && issuesCD.Count == 0, "正常拆分无残留引用：" + string.Join(";", issuesAB.Concat(issuesCD)));
                var noPlaceholderSource = Object.Instantiate(source);
                var noPlaceholderCollection = noPlaceholderSource.GetComponentInChildren<WardrobeGeneratedSwitch>(true);
                Object.DestroyImmediate(noPlaceholderCollection.PlaceholderOutfit);
                noPlaceholderCollection.PlaceholderOutfit = null; noPlaceholderCollection.UsesPlaceholderOutfit = false;
                Assert(AvatarOutfitMenus.Read(noPlaceholderSource, out var noPlaceholder).Count == 4 && !noPlaceholder,
                    "没有占位衣服的 Avatar 可以读取全部衣服菜单");
                var noPlaceholderCopy = AvatarSplitBuilder.CreateVariant(noPlaceholderSource,
                    noPlaceholderCollection.Entries.Skip(2).Select(e => e.OutfitRoot), "无占位拆分", "", scene, out _, out _);
                var noPlaceholderResult = AvatarSplitBuilder.Collection(noPlaceholderCopy);
                Assert(!noPlaceholderResult.UsesPlaceholderOutfit && !noPlaceholderResult.PlaceholderOutfit &&
                    noPlaceholderResult.Entries.Count == 2 && !noPlaceholderCopy.transform.Find("A") && !noPlaceholderCopy.transform.Find("B"),
                    "无占位模式仅保留所选衣服，不添加或要求占位物体");
                Assert(noPlaceholderResult.Entries.All(e => e.WearNode.GetComponent<ModularAvatarObjectToggle>().Objects.Count == 2) &&
                    noPlaceholderResult.Entries.Count(e => e.OutfitRoot.activeSelf) == 1 && noPlaceholderResult.DefaultOutfit.activeSelf &&
                    noPlaceholderResult.DefaultOutfit.name == "C",
                    "无占位模式互斥引用完整，排除原默认衣服后启用组内第一套");
                noPlaceholderCollection.UsesPlaceholderOutfit = true;
                var missingPlaceholderCopy = AvatarSplitBuilder.CreateVariant(noPlaceholderSource,
                    noPlaceholderCollection.Entries.Take(2).Select(e => e.OutfitRoot), "占位引用缺失", "", scene, out _, out _);
                var missingPlaceholderResult = AvatarSplitBuilder.Collection(missingPlaceholderCopy);
                Assert(!missingPlaceholderResult.UsesPlaceholderOutfit && missingPlaceholderResult.DefaultOutfit.name == "A" &&
                    missingPlaceholderResult.DefaultOutfit.activeSelf && noPlaceholderCollection.UsesPlaceholderOutfit,
                    "占位引用已丢失时副本按无占位模式生成，保留有效默认衣服且不修改源配置");
                Object.DestroyImmediate(noPlaceholderCopy); Object.DestroyImmediate(missingPlaceholderCopy); Object.DestroyImmediate(noPlaceholderSource);
                var constraint = source.AddComponent<UnityEngine.Animations.ParentConstraint>();
                constraint.AddSource(new UnityEngine.Animations.ConstraintSource { sourceTransform = collection.Entries[2].OutfitRoot.transform, weight = 1 });
                var bad = AvatarSplitBuilder.CreateVariant(source, collection.Entries.Take(2).Select(e => e.OutfitRoot), "外部引用测试", "", scene, out _, out var badIssues);
                Assert(badIssues.Count == 0, "默认生成不遍历所有组件和动画，也不将普通空引用作为阻断项");
                Object.DestroyImmediate(constraint);
                var clip = new AnimationClip { name = "保留动画引用排除衣服" };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(removedAB.First(x => x == "C"), typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
                var controller = new UnityEditor.Animations.AnimatorController();
                controller.AddLayer("Test"); controller.layers[0].stateMachine.AddState("Test").motion = clip;
                var animator = a.GetComponent<Animator>();
                if (!animator) animator = a.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                Assert(AvatarSplitBuilder.Inspect(a, removedAB).Any(x => x.Contains(clip.name)), "手动详细检查仍可检测共享动画引用");
                Object.DestroyImmediate(controller); Object.DestroyImmediate(clip);
                bool rejected = false;
                try { AvatarSplitBuilder.CreateVariant(source, Array.Empty<GameObject>(), "空组", "", scene, out _, out _); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "空分组被拒绝");
                Assert(CauBridge.Available, "检测到已安装 CAU（自检不会启动上传）");
                CauBridge.ValidateApi();
                lines.Add("PASS: CAU 队列、平台、场景引用及自动保存回调接口匹配");
                var plan = ScriptableObject.CreateInstance<AvatarSplitPlan>();
                try
                {
                    var group = new SplitGroup(); plan.groups.Add(group);
                    b.GetComponent<PipelineManager>().blueprintId = "avtr_33333333-3333-3333-3333-333333333333";
                    CauBridge.RecordBlueprint(plan, group, b, false);
                    Assert(group.blueprintId.EndsWith("333333333333") && !group.uploadSucceeded, "失败时保留已预留 ID，不标记为成功");
                    CauBridge.RecordBlueprint(plan, group, b, true);
                    Assert(group.uploadSucceeded, "成功回调写回蓝图与成功状态");
                    b.GetComponent<PipelineManager>().blueprintId = "avtr_44444444-4444-4444-4444-444444444444";
                    bool mismatch = false;
                    try { CauBridge.RecordBlueprint(plan, group, b, true); } catch (InvalidOperationException) { mismatch = true; }
                    Assert(mismatch && group.blueprintId.EndsWith("333333333333"), "蓝图被意外更换时不覆盖分组记录");
                }
                finally { Object.DestroyImmediate(plan); }
                // Persist only a disposable fixture to verify real scene IDs and
                // repeat-generation replacement, without touching user avatars.
                testFolder = AvatarSplitBuilder.Root + "/_SelfTest_" + Guid.NewGuid().ToString("N");
                AvatarSplitBuilder.EnsureFolder(testFolder);
                Assert(EditorSceneManager.SaveScene(scene, testFolder + "/Fixture.unity"), "保存独立自检夹具场景");
                source.name = "原角色";
                EditorSceneManager.MarkSceneDirty(scene);
                SplitSourceStore.EnsureSaved(source);
                Assert(!scene.isDirty, "源场景修改自动保存，无需保存提示或额外确认");
                var persistentPlan = ScriptableObject.CreateInstance<AvatarSplitPlan>();
                AssetDatabase.CreateAsset(persistentPlan, testFolder + "/Plan.asset");
                persistentPlan.sourceId = SplitIds.Of(source); persistentPlan.sourceScene = scene.path;
                persistentPlan.groups.Add(new SplitGroup { name = "AB", outfitIds = collection.Entries.Take(2).Select(e => SplitIds.Of(e.OutfitRoot)).ToList() });
                persistentPlan.groups.Add(new SplitGroup { name = "CD", outfitIds = collection.Entries.Skip(2).Select(e => SplitIds.Of(e.OutfitRoot)).ToList() });
                SplitIds.Save(persistentPlan);
                SplitNaming.Apply(persistentPlan, source);
                Assert(persistentPlan.groups[0].name == "原角色V1" && persistentPlan.groups[1].name == "原角色V2",
                    "按源 Avatar 名称自动命名 V1、V2");
                persistentPlan.groups.Reverse(); SplitNaming.Apply(persistentPlan, source);
                Assert(persistentPlan.groups[0].name == "原角色V2" && persistentPlan.groups[1].name == "原角色V1", "调整分组顺序不改变版本号");
                persistentPlan.groups.Reverse();
                var third = SplitNaming.Add(persistentPlan, source);
                Assert(third.name == "原角色V3" && string.IsNullOrEmpty(third.blueprintId), "新分组递增版本号，保持独立蓝图");
                persistentPlan.groups.Remove(third);
                var fixtureLegacy = persistentPlan;
                persistentPlan = SplitConfigStore.Attach(source, fixtureLegacy);
                Assert(persistentPlan.config && !AssetDatabase.Contains(persistentPlan) && source.transform.Find("衣服拆分配置"),
                    "方案迁到 Avatar 子层级，不新增方案资产");
                Assert(persistentPlan.config.References[0].Outfits[0] == collection.Entries[0].OutfitRoot,
                    "衣服勾选保存为 Avatar 内部直接引用");
                persistentPlan = SplitConfigStore.Read(persistentPlan.config, true);
                Assert(persistentPlan.groups[0].outfitIds.Count == 2 && persistentPlan.groups.Count == 2,
                    "重新读取组件后恢复分组及衣服选择");
                int sceneCount = SceneManager.sceneCount;
                AvatarSplitBuilder.Generate(persistentPlan);
                var output = (SplitIds.Resolve(persistentPlan.outputRootId) as GameObject).GetComponent<AvatarSplitOutputRoot>();
                var firstCopies = output.Generated.ToArray();
                Assert(SceneManager.sceneCount == sceneCount && output.gameObject.scene == source.scene && output.Generated.Count == 2,
                    "首次生成只在源场景创建结果节点，没有增加场景");
                var manualChild = Child(output.gameObject, "手动放入的其他对象");
                firstCopies[0].GetComponent<PipelineManager>().blueprintId = "avtr_55555555-5555-5555-5555-555555555555";
                AvatarSplitBuilder.Generate(persistentPlan);
                Assert(SceneManager.sceneCount == sceneCount && output && output.Generated.Count == 2 && firstCopies.All(old => !old),
                    "连续生成替换原来两个副本，不堆叠模型或场景");
                Assert(manualChild && manualChild.transform.parent == output.transform,
                    "替换只删除工具记录的旧副本，保留手动添加的其他对象");
                Assert(output.Generated[0].GetComponent<PipelineManager>().blueprintId.EndsWith("555555555555") &&
                    persistentPlan.groups[0].blueprintId.EndsWith("555555555555"), "替换前自动保存旧副本蓝图，新副本沿用该蓝图");
                Assert(collection.Entries.Count == 4 && source.transform.Find("A") && source.transform.Find("D"), "重复生成后源 Avatar 仍完整");
                Assert(output.Generated.All(c => c.GetComponentsInChildren<AvatarSplitConfig>(true).Length == 0) && persistentPlan.config,
                    "生成副本移除配置节点，源 Avatar 配置保留");
                var manualId = "avtr_66666666-6666-6666-6666-666666666666";
                CauBridge.AssignBlueprint(persistentPlan, persistentPlan.groups[0], "  " + manualId + "  ");
                Assert(persistentPlan.groups[0].blueprintId == manualId && output.Generated[0].GetComponent<PipelineManager>().blueprintId == manualId,
                    "手动分配蓝图同步更新方案与已有副本");
                bool invalidRejected = false, duplicateRejected = false;
                try { CauBridge.AssignBlueprint(persistentPlan, persistentPlan.groups[0], "bad"); } catch (InvalidOperationException) { invalidRejected = true; }
                try { CauBridge.AssignBlueprint(persistentPlan, persistentPlan.groups[1], manualId); } catch (InvalidOperationException) { duplicateRejected = true; }
                Assert(invalidRejected && duplicateRejected && persistentPlan.groups[0].blueprintId == manualId,
                    "格式错误或分组重复的蓝图号被拒绝，原记录保留");
                CauBridge.AssignBlueprint(persistentPlan, persistentPlan.groups[0], "");
                Assert(string.IsNullOrEmpty(persistentPlan.groups[0].blueprintId) && string.IsNullOrEmpty(output.Generated[0].GetComponent<PipelineManager>().blueprintId),
                    "清空蓝图同时清空副本 Pipeline，下次可新建 Avatar");
                var duplicated = Object.Instantiate(source); duplicated.name = "复制角色";
                SceneManager.MoveGameObjectToScene(duplicated, scene); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
                var copiedConfig = duplicated.GetComponentInChildren<AvatarSplitConfig>(true);
                var copiedPlan = SplitConfigStore.Read(copiedConfig);
                Assert(copiedPlan.groups[0].outfitIds.All(id => (SplitIds.Resolve(id) as GameObject).transform.IsChildOf(duplicated.transform)),
                    "完整复制 Avatar 后衣服引用自动映射至副本内部");
                Assert(copiedConfig.Key != persistentPlan.config.Key && !copiedConfig.Output && copiedPlan.groups.All(g => string.IsNullOrEmpty(g.generatedAvatarId)),
                    "复制 Avatar 使用独立结果身份，不会替换原 Avatar 的生成副本");
                Object.DestroyImmediate(duplicated);
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, testFolder + "/Portable.prefab");
                var fromPrefab = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
                var prefabPlan = SplitConfigStore.Read(fromPrefab.GetComponentInChildren<AvatarSplitConfig>(true));
                Assert(prefabPlan.groups.Count == 2 && prefabPlan.groups[0].outfitIds.All(id =>
                    (SplitIds.Resolve(id) as GameObject).transform.IsChildOf(fromPrefab.transform)),
                    "保存 Prefab 后重新实例化，衣服与配置仍绑定到新 Avatar");
                Object.DestroyImmediate(fromPrefab);
                var view = ScriptableObject.CreateInstance<AvatarSplitWindow>();
                try
                {
                    // BaseField only dispatches value changes on an attached panel.
                    // Show a disposable editor window for the interaction check.
                    view.titleContent = new GUIContent("分组界面自检");
                    view.Show();
                    view.ConfigureForTest(persistentPlan);
                    var root = view.rootVisualElement;
                    var outfitToggles = root.Query<Toggle>().ToList().Where(t => (t.name ?? "").StartsWith("split-outfit-", StringComparison.Ordinal)).ToArray();
                    Assert(outfitToggles.Length == 4 && outfitToggles.Any(t => t.text == "连衣裙（菜单名）"), "新界面直接展示对应菜单名的四个衣服选项");
                    EditorSceneManager.MarkSceneDirty(scene);
                    var originalConfig = persistentPlan.config.Data;
                    int checkboxSceneSaves = 0;
                    void CountCheckboxSave(Scene savedScene, string path) { if (savedScene == scene) checkboxSceneSaves++; }
                    EditorSceneManager.sceneSaving += CountCheckboxSave;
                    try
                    {
                        var checkbox = outfitToggles.First(t => t.value);
                        checkbox.value = false; checkbox.value = true; checkbox.value = false;
                        SplitIds.Flush();
                        Assert(checkboxSceneSaves == 0 && scene.isDirty && persistentPlan.config.References[0].Outfits.Count == 1,
                            "连续勾选及合并写入不会触发任何场景保存，配置正确记录到 Avatar");
                        persistentPlan.config.Data = originalConfig;
                        persistentPlan = SplitConfigStore.Read(persistentPlan.config, true);
                        Assert(persistentPlan.groups[0].outfitIds.Count == 1,
                            "无需保存大场景，本地恢复记录能找回最新衣服选择");
                        Assert(checkboxSceneSaves == 0, "恢复配置同样不会强制保存场景");
                    }
                    finally { EditorSceneManager.sceneSaving -= CountCheckboxSave; }
                    Assert(persistentPlan.groups[0].outfitIds.Count == 1 && !root.Q<Button>("split-upload").enabledSelf,
                        "源场景未保存时仍可勾选衣服；修改后需重新生成（保留数 " + persistentPlan.groups[0].outfitIds.Count + "，面板连接 " + (root.panel != null) + "）");
                    view.LayoutForTest(960);
                    Assert(root.Q("split-workspace").style.flexDirection.value == FlexDirection.Row && root.Q("split-groups").style.width.value.value == 210,
                        "宽窗口显示分组列表与衣服详情两列");
                    view.LayoutForTest(560);
                    Assert(root.Q("split-workspace").style.flexDirection.value == FlexDirection.Column,
                        "窄窗口自动改为上下布局，保留相同操作");
                    SplitIds.Flush();
                }
                finally { Object.DestroyImmediate(view); }
                var lifecycleView = ScriptableObject.CreateInstance<AvatarSplitWindow>();
                try
                {
                    lifecycleView.Show(); lifecycleView.ConfigureForTest(persistentPlan); SplitIds.Flush();
                    var editConfig = persistentPlan.config;
                    var savedData = editConfig.Data; var savedKey = editConfig.Key;
                    var savedBlueprint = persistentPlan.groups[0].blueprintId;
                    SplitIds.PlayModeChanged(PlayModeStateChange.ExitingEditMode);
                    SplitIds.PlayModeChanged(PlayModeStateChange.EnteredPlayMode);
                    persistentPlan.groups[0].blueprintId = "avtr_77777777-7777-7777-7777-777777777777";
                    SplitIds.Save(persistentPlan); SplitIds.SaveLater(persistentPlan); SplitConfigStore.Write(persistentPlan, false);
                    bool scanBlocked = false, readBlocked = false;
                    try { AvatarOutfitMenus.Read(source); } catch (InvalidOperationException) { scanBlocked = true; }
                    try { SplitConfigStore.Read(editConfig); } catch (InvalidOperationException) { readBlocked = true; }
                    Assert(scanBlocked && readBlocked && editConfig.Data == savedData && editConfig.Key == savedKey,
                        "模拟进入游戏模式：暂停扫描与配置读写，不更换配置身份或覆盖蓝图");
                    SplitIds.PlayModeChanged(PlayModeStateChange.ExitingPlayMode);
                    SplitIds.PlayModeChanged(PlayModeStateChange.EnteredEditMode);
                    lifecycleView.RestoreAfterPlayForTest();
                    var restored = SplitConfigStore.Read(editConfig);
                    Assert(!ReferenceEquals(restored, persistentPlan) && restored.groups[0].blueprintId == savedBlueprint &&
                        restored.groups[0].outfitIds.Count == 1 && AvatarOutfitMenus.Read(source).Count == 4,
                        "模拟退出游戏模式：丢弃旧缓存，恢复编辑模式蓝图、分组和四套衣服菜单");
                    Assert(lifecycleView.rootVisualElement.Q<UnityEditor.UIElements.ObjectField>("split-source").value == source.GetComponent<VRCAvatarDescriptor>() &&
                        lifecycleView.rootVisualElement.Query<Toggle>().ToList().Count(t => (t.name ?? "").StartsWith("split-outfit-")) == 4,
                        "窗口丢失临时引用后仍从编辑对象标识重新绑定源 Avatar 并恢复衣服勾选列表");
                    persistentPlan = restored;
                }
                finally
                {
                    SplitIds.PlayModeChanged(PlayModeStateChange.EnteredEditMode);
                    Object.DestroyImmediate(lifecycleView);
                }
                lines.Add("PASS: 所有自检通过；没有执行网络上传。");
                Debug.Log("[分组拆分测试] " + string.Join("\n", lines));
            }
            catch (Exception e) { lines.Add("FAIL: " + e); Debug.LogException(e); }
            finally
            {
                Undo.RevertAllDownToGroup(testUndoGroup);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
                if (testFolder != null && AssetDatabase.IsValidFolder(testFolder)) AssetDatabase.DeleteAsset(testFolder);
                Selection.objects = previousSelection;
                File.WriteAllLines(Result, lines);
            }
            void Assert(bool ok, string description)
            { if (!ok) throw new Exception(description); lines.Add("PASS: " + description); }
        }
        static GameObject Child(GameObject parent, string name)
        { var child = new GameObject(name); child.transform.SetParent(parent.transform, false); return child; }
    }
}
#endif
