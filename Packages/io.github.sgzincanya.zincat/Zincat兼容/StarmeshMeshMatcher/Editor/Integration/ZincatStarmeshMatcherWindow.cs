#if ZINCAT_STARMESH_PACKAGE && ZINCAT_HAIMESHLIB_PACKAGE
// Zincat Starmesh Mesh Matcher 0.2.8. Requires Starmesh 1.7.0.
// Uses the installed Starmesh API; no third-party source is included.
#pragma warning disable CS0612
using System;
using System.Collections.Generic;
using System.Linq;
using Hai.Starmesh.Editor;
using Hai.Starmesh.Runtime;
using HaiMeshLib.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zincat.StarmeshTools
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Zincat.StarmeshTools", "Assembly-CSharp-Editor", "ZincatStarmeshMatcherWindow")]
    public sealed class ZincatStarmeshMatcherWindow : EditorWindow
    {
        [SerializeField] private Transform avatar;
        [SerializeField] private StarmeshSelectMeshes target;
        [SerializeField] private MatcherOptions options = new MatcherOptions();
        [SerializeField] private int selectedTab;
        private Matrix4x4 lastBoxMatrix;
        private Dictionary<SkinnedMeshRenderer, MeshRule> rules = new Dictionary<SkinnedMeshRenderer, MeshRule>();
        private List<MeshMatch> results = new List<MeshMatch>();
        private bool hasAnalysis;
        private Vector2 scroll;
        private Vector2 filterScroll;
        private string search = "";
        private string message = "选择 SelectMeshes 或效果对象，再点击“从当前选择读取”。窗口会保持锁定，方便你选择衣服。";
        private MessageType messageType = MessageType.Info;

        public static void Open()
        {
            var window = GetWindow<ZincatStarmeshMatcherWindow>("Starmesh 目标助手");
            window.minSize = new Vector2(650, 600);
            window.ReadSelection();
        }

        private void OnEnable()
        {
            minSize = new Vector2(650, 600);
            if (target)
            {
                rules = MatcherSettings.Current.Load(target);
                options = MatcherSettings.Current.LoadOptions(target);
            }
            Undo.undoRedoPerformed += Refresh;
            SceneView.duringSceneGui += DrawScene;
            EditorSceneManager.sceneSaved += SceneSaved;
        }
        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Refresh; SceneView.duringSceneGui -= DrawScene;
            EditorSceneManager.sceneSaved -= SceneSaved;
        }
        private void SceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            if (target && target.gameObject.scene == scene) MatcherSettings.Current.Store(target, rules, options);
        }
        private void Refresh() { results.Clear(); hasAnalysis = false; Repaint(); }

        private void SetTarget(StarmeshSelectMeshes value)
        {
            target = value;
            rules = MatcherSettings.Current.Load(target);
            options = MatcherSettings.Current.LoadOptions(target);
            results.Clear(); hasAnalysis = false;
        }

        private void ReadSelection()
        {
            var go = Selection.activeGameObject;
            if (!go) return;
            var selector = go.GetComponent<StarmeshSelectMeshes>();
            var op = go.GetComponent<StarmeshOperator>() ?? go.GetComponentInParent<StarmeshOperator>();
            if (!selector && op) selector = op.SafeSelectors().OfType<StarmeshSelectMeshes>().FirstOrDefault();
            if (!selector)
            {
                var found = go.GetComponentsInChildren<StarmeshSelectMeshes>(true);
                if (found.Length == 1) selector = found[0];
            }
            if (!selector) { message = "请选择 SelectMeshes 或引用它的效果对象；多个列表时请直接指定目标组件。"; return; }
            SetTarget(selector);
            var animator = selector.GetComponentInParent<Animator>();
            avatar = animator ? animator.transform : null;
            if (avatar)
            {
                var user = MatcherEngine.Users(avatar, selector).FirstOrDefault();
                var av = user ? user.AvatarOptional() : null;
                if (av && av.avatarRoot) avatar = av.avatarRoot;
            }
            message = "目标已锁定。点击“一键自动匹配并应用”即可更新模型列表。";
        }

        private bool Ready => avatar && target && target.transform.IsChildOf(avatar) &&
            avatar.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(avatar) &&
            PrefabStageUtility.GetCurrentPrefabStage() == null && !EditorApplication.isPlayingOrWillChangePlaymode;

        private void SaveFilters()
        {
            MatcherSettings.Current.Store(target, rules, options);
            results.Clear(); hasAnalysis = false;
            SceneView.RepaintAll();
            Repaint();
        }

        public static Transform CreateBox(Transform avatar, StarmeshSelectMeshes target)
        {
            if (!avatar || !target || !target.transform.IsChildOf(avatar))
                throw new InvalidOperationException("请先指定 Avatar 和目标组件");
            var go = new GameObject("Zincat_Starmesh匹配框") { tag = "EditorOnly" };
            go.transform.SetParent(target.transform, false);
            var wave = MatcherEngine.Users(avatar, target).OfType<StarmeshOperatorRippleWave>().FirstOrDefault();
            if (wave)
            {
                go.transform.position = wave.transform.TransformPoint(wave.origin);
                go.transform.rotation = wave.transform.rotation;
            }
            go.transform.localScale = Vector3.one * .3f;
            Undo.RegisterCreatedObjectUndo(go, "创建 Starmesh 匹配框");
            EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
            return go.transform;
        }

        private void DrawScene(SceneView view)
        {
            if (!Ready || !options.useBox || !options.box) return;
            var box = options.box;
            if (lastBoxMatrix != box.localToWorldMatrix)
            {
                lastBoxMatrix = box.localToWorldMatrix;
                results.Clear(); hasAnalysis = false; Repaint();
            }
            using (new Handles.DrawingScope(new Color(.1f, .85f, 1f, .9f), box.localToWorldMatrix))
            {
                Handles.DrawWireCube(Vector3.zero, Vector3.one);
                for (int axis = 0; axis < 3; axis++)
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        var corners = new Vector3[4];
                        for (int i = 0; i < 4; i++)
                        {
                            var corner = Vector3.zero;
                            corner[axis] = sign * .5f;
                            corner[(axis + 1) % 3] = (i == 0 || i == 3) ? -.5f : .5f;
                            corner[(axis + 2) % 3] = i < 2 ? -.5f : .5f;
                            corners[i] = corner;
                        }
                        Handles.DrawSolidRectangleWithOutline(corners, new Color(.1f, .85f, 1f, .035f), Color.clear);
                    }
            }
            Handles.Label(box.position, "Starmesh 匹配框（仅过滤模型）");
        }

        private void DrawObjectList(string label, List<Transform> list, bool white)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label + "（包含子物体）", EditorStyles.boldLabel);
            if (GUILayout.Button("加入层级所选", GUILayout.Width(100)))
            {
                foreach (var go in Selection.gameObjects)
                    if (avatar && go.transform.IsChildOf(avatar) && !list.Contains(go.transform)) list.Add(go.transform);
                if (white && list.Any(t => t)) options.useWhitelist = true;
                SaveFilters();
            }
            if (GUILayout.Button("+", GUILayout.Width(25))) { list.Add(null); SaveFilters(); }
            EditorGUILayout.EndHorizontal();
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                var next = (Transform)EditorGUILayout.ObjectField(list[i], typeof(Transform), true);
                if (next != list[i]) { list[i] = next; SaveFilters(); }
                bool remove = GUILayout.Button("移除", GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();
                if (remove) { list.RemoveAt(i--); SaveFilters(); }
            }
        }

        private void DrawFilters()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            using (new EditorGUI.DisabledScope(!Ready))
            {
                EditorGUI.BeginChangeCheck();
                options.body = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Body / 基础身体", options.body, typeof(SkinnedMeshRenderer), true);
                EditorGUILayout.LabelField("指定的身体始终参与效果，不受匹配框、白名单和顶点门槛限制。", EditorStyles.wordWrappedMiniLabel);
                options.useBox = EditorGUILayout.Toggle("启用匹配框", options.useBox);
                options.box = (Transform)EditorGUILayout.ObjectField("匹配框", options.box, typeof(Transform), true);
                options.minimumVertices = Mathf.Max(1, EditorGUILayout.IntField("最少有效顶点数", options.minimumVertices));
                options.useWhitelist = EditorGUILayout.Toggle("仅处理白名单", options.useWhitelist);
                if (EditorGUI.EndChangeCheck()) SaveFilters();
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(options.box ? "新建另一个匹配框" : "创建匹配框"))
                {
                    options.box = CreateBox(avatar, target); options.useBox = true;
                    lastBoxMatrix = options.box.localToWorldMatrix;
                    Selection.activeGameObject = options.box.gameObject;
                    SaveFilters();
                }
                using (new EditorGUI.DisabledScope(!options.box))
                    if (GUILayout.Button("选中匹配框（W / E / R 调整）"))
                        Selection.activeGameObject = options.box.gameObject;
                EditorGUILayout.EndHorizontal();
                DrawObjectList("白名单", options.whitelist, true);
                DrawObjectList("黑名单", options.blacklist, false);
            }
            EditorGUILayout.HelpBox("白名单外不新增，已有引用保持不动。黑名单 / 始终排除优先；始终包含仅在白名单范围内生效，可跳过框和顶点门槛。框只筛选模型，不裁剪原效果。", MessageType.Info);
            if (options.useWhitelist && !options.whitelist.Any(t => t))
                EditorGUILayout.HelpBox(options.body ? "白名单为空：仅加入指定的 Body，不会自动新增衣服。" : "白名单为空：不会新增任何模型。请指定 Body，并将衣服或父物体加入白名单。", MessageType.Warning);
            if (options.body && (options.IsBlacklisted(options.body.transform) || MatcherEngine.RuleFor(rules, options.body) == MeshRule.Exclude))
                EditorGUILayout.HelpBox("Body 与排除规则冲突，应用将被阻止。请将身体从黑名单移除，并取消“始终排除”。", MessageType.Error);
            EditorGUILayout.EndVertical();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Starmesh 自动匹配 Mesh", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("按选择规则匹配；波纹、压缩和 TRS 进一步检查实际位移。包含隐藏衣服。", EditorStyles.wordWrappedMiniLabel);
            var newAvatar = (Transform)EditorGUILayout.ObjectField("Avatar 根物体", avatar, typeof(Transform), true);
            if (newAvatar != avatar) { avatar = newAvatar; results.Clear(); hasAnalysis = false; }
            var newTarget = (StarmeshSelectMeshes)EditorGUILayout.ObjectField("锁定的 Select Meshes", target, typeof(StarmeshSelectMeshes), true);
            if (newTarget != target) SetTarget(newTarget);
            if (GUILayout.Button("从当前选择读取")) ReadSelection();
            if (!Ready) EditorGUILayout.HelpBox("请指定场景中的 Avatar 和它内部的 Select Meshes；退出播放模式和 Prefab 编辑模式后使用。", MessageType.Info);
            if (target && string.IsNullOrEmpty(target.gameObject.scene.path))
                EditorGUILayout.HelpBox("当前场景未保存。规则暂存于窗口中；保存场景后再次修改规则即可持久保存。", MessageType.Warning);
            if (Ready)
            {
                int count = MatcherEngine.Users(avatar, target).Length;
                EditorGUILayout.LabelField("引用此列表的效果", count + " 个（共用时合并匹配结果）");
            }
            EditorGUILayout.Space(6);
            selectedTab = GUILayout.Toolbar(selectedTab, new[] { "匹配设置 · 框与名单", "模型结果 · 手动规则" }, GUILayout.Height(28));
            EditorGUILayout.Space(6);
            if (selectedTab == 0)
            {
                filterScroll = EditorGUILayout.BeginScrollView(filterScroll, GUILayout.ExpandHeight(true));
                DrawFilters();
                EditorGUILayout.EndScrollView();
            }
            else DrawResults();
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(message, messageType);
            using (new EditorGUI.DisabledScope(!Ready))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("一键自动匹配并应用", GUILayout.Height(32))) Match(true);
                if (GUILayout.Button("仅分析", GUILayout.Height(32), GUILayout.Width(100))) Match(false);
                EditorGUILayout.EndHorizontal();
                    if (GUILayout.Button(new GUIContent("清空目标并清理闲置 Mesh Data", "清空当前目标，并清理 Avatar 内未被其他 Starmesh 列表或形状转移源引用的原始数据组件。保留匹配框和所有名单规则。可 Ctrl+Z 撤销。"), GUILayout.Height(24)))
                        ClearTargets();
            }
            EditorGUILayout.LabelField("场景变更支持 Ctrl+Z；手动规则单独保存。调整完成后请保存场景。", EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawResults()
        {
            using (new EditorGUI.DisabledScope(!Ready))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("将层级所选设为始终包含")) SetSelected(MeshRule.Include);
                if (GUILayout.Button("将层级所选设为始终排除")) SetSelected(MeshRule.Exclude);
                EditorGUILayout.EndHorizontal();
            }
            search = EditorGUILayout.TextField("搜索模型 / 路径", search);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            var rows = hasAnalysis ? results : avatar
                ? MatcherEngine.Candidates(avatar, options).Select(r => new MeshMatch { renderer = r }).ToList()
                : new List<MeshMatch>();
            if (rows.Count == 0)
                EditorGUILayout.HelpBox(options.useWhitelist ? "白名单内没有可扫描的 Mesh。名单外对象不会扫描或显示。" : "当前 Avatar 没有可扫描的 Mesh。", MessageType.Info);
            foreach (var row in rows)
            {
                if (!row.renderer) continue;
                string path = AnimationUtility.CalculateTransformPath(row.renderer.transform, avatar);
                if (!string.IsNullOrEmpty(search) && path.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(row.renderer.name, EditorStyles.linkLabel)) EditorGUIUtility.PingObject(row.renderer.gameObject);
                var mode = MatcherEngine.RuleFor(rules, row.renderer);
                var next = (MeshRule)EditorGUILayout.Popup((int)mode, new[] { "自动", "始终包含", "始终排除" }, GUILayout.Width(100));
                if (next != mode)
                {
                    if (next == MeshRule.Auto) rules.Remove(row.renderer); else rules[row.renderer] = next;
                    MatcherSettings.Current.Store(target, rules, options);
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(path + (row.renderer.gameObject.activeInHierarchy ? "" : "  [隐藏]"), EditorStyles.wordWrappedMiniLabel);
                if (results.Count > 0)
                {
                    string status = row.isBody && string.IsNullOrEmpty(row.error) ? "Body：固定加入，不受框、白名单及顶点门槛限制"
                        : row.blacklisted ? "黑名单排除" : row.outsideWhitelist ? "白名单外，保持原状态"
                        : !string.IsNullOrEmpty(row.error) ? "异常：" + row.error + (row.previouslyIncluded ? "（自动模式保留原引用）" : "")
                        : "匹配的有效顶点：" + row.selectedVertices + " / 门槛 " + row.minimumVertices;
                    EditorGUILayout.LabelField((MatcherEngine.ShouldInclude(row, rules) ? "加入 / 保留  ·  " : "不加入  ·  ") + status,
                        EditorStyles.wordWrappedMiniLabel);
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private void SetSelected(MeshRule mode)
        {
            if (!target) return;
            foreach (var go in Selection.gameObjects)
                foreach (var renderer in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (renderer.transform.IsChildOf(avatar)) rules[renderer] = mode;
            MatcherSettings.Current.Store(target, rules, options);
            Repaint();
        }

        private void ClearTargets()
        {
            try
            {
                int count = target.skinnedMeshes == null ? 0 : target.skinnedMeshes.Length;
                var cleanup = MatcherEngine.ClearAndCleanup(avatar, target);
                results.Clear(); hasAnalysis = false;
                message = $"已清空 {count} 条目标；移除闲置 Mesh Data {cleanup.removed} 个，其他效果仍引用 {cleanup.preserved} 个，无法恢复而保留 {cleanup.skipped} 个。匹配框和名单保留，可 Ctrl+Z 撤销。";
                messageType = cleanup.skipped == 0 ? MessageType.Info : MessageType.Warning;
                SceneView.RepaintAll();
            }
            catch (Exception ex) { message = ex.Message; messageType = MessageType.Error; Debug.LogException(ex); }
        }

        private void Match(bool apply)
        {
            try
            {
                var analyzed = MatcherEngine.Analyze(avatar, target, (i, n) =>
                    EditorUtility.DisplayCancelableProgressBar("Starmesh 自动匹配", $"分析模型 {i + 1} / {n}", n == 0 ? 0 : (float)i / n), options);
                results = analyzed;
                hasAnalysis = true;
                selectedTab = 1;
                if (apply) MatcherEngine.Apply(avatar, target, results, rules, options);
                MatcherSettings.Current.Store(target, rules, options);
                int errors = results.Count(r => !string.IsNullOrEmpty(r.error));
                int included = results.Count(r => MatcherEngine.ShouldInclude(r, rules));
                message = $"{(apply ? "已应用" : "分析完成，尚未应用")}：{(options.useWhitelist ? "仅扫描白名单内" : "扫描")} {results.Count} 个，范围内目标 {included} 个，异常 {errors} 个。";
                messageType = errors == 0 ? MessageType.Info : MessageType.Warning;
                SceneView.RepaintAll();
            }
            catch (OperationCanceledException) { message = "已取消，本次没有修改目标列表。"; messageType = MessageType.Info; }
            catch (Exception ex) { message = ex.Message; messageType = MessageType.Error; Debug.LogException(ex); }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
#pragma warning restore CS0612

#endif
