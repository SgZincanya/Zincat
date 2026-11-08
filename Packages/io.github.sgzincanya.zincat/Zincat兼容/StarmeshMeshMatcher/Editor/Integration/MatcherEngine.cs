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
    public sealed class MeshMatch
    {
        public SkinnedMeshRenderer renderer;
        public int selectedVertices;
        public string error;
        public bool previouslyIncluded;
        public bool outsideWhitelist;
        public bool blacklisted;
        public int minimumVertices = 1;
        public bool isBody;
    }

    public static class MatcherEngine
    {
        public static SkinnedMeshRenderer[] Candidates(Transform root, MatcherOptions options = null)
        {
            if (!root) return Array.Empty<SkinnedMeshRenderer>();
            if (options == null || !options.useWhitelist)
                return root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var roots = options.whitelist.Where(t => t && t.IsChildOf(root)).Distinct().ToArray();
            // Do not traverse overlapping whitelist subtrees more than once.
            return roots.Where(t => !roots.Any(parent => parent != t && t.IsChildOf(parent)))
                .SelectMany(t => t.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                .Concat(options.body && options.body.transform.IsChildOf(root) ? new[] { options.body } : Array.Empty<SkinnedMeshRenderer>())
                .Distinct().ToArray();
        }

        public sealed class CleanupResult
        {
            public int removed;
            public int preserved;
            public int skipped;
        }

        public static CleanupResult ClearAndCleanup(Transform root, StarmeshSelectMeshes target)
        {
            if (!root || !target || !target.transform.IsChildOf(root))
                throw new InvalidOperationException("目标组件必须位于所选 Avatar 内");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("清空 Starmesh 目标并清理闲置数据");
            try
            {
                Apply(root, target, new List<MeshMatch>(), new Dictionary<SkinnedMeshRenderer, MeshRule>());
                var used = new HashSet<SkinnedMeshRenderer>(Resources.FindObjectsOfTypeAll<StarmeshSelectMeshes>()
                    .Where(s => s && !EditorUtility.IsPersistent(s) && s.gameObject.scene.IsValid())
                    .SelectMany(s => s.SafeSkinnedMeshRenderers()));
                foreach (var transfer in Resources.FindObjectsOfTypeAll<StarmeshOperatorTransferBlendshape>())
                    if (transfer && !EditorUtility.IsPersistent(transfer) && transfer.gameObject.scene.IsValid() && transfer.sourceMesh)
                        used.Add(transfer.sourceMesh);
                var result = new CleanupResult();
                foreach (var data in root.GetComponentsInChildren<StarmeshOriginalMeshData>(true))
                {
                    var renderer = data.GetComponent<SkinnedMeshRenderer>();
                    if (renderer && used.Contains(renderer)) { result.preserved++; continue; }
                    if (renderer && (!renderer.sharedMesh || !EditorUtility.IsPersistent(renderer.sharedMesh)))
                    {
                        if (!data.originalMesh || !EditorUtility.IsPersistent(data.originalMesh))
                        {
                            result.skipped++;
                            Debug.LogWarning("[Starmesh 目标助手] 无法确认可恢复的原始 Mesh，保留数据组件：" + data.name, data);
                            continue;
                        }
                        Undo.RecordObject(renderer, "恢复原始 Mesh");
                        renderer.sharedMesh = data.originalMesh;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }
                    Undo.DestroyObjectImmediate(data);
                    result.removed++;
                }
                EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
                Undo.CollapseUndoOperations(group);
                return result;
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }

        // Test actual posed/skinned vertex positions against the oriented unit box.
        // Bounds overlap alone is deliberately insufficient.
        public static Vector3[] WorldVertices(SkinnedMeshRenderer renderer, Mesh mesh, Vector3[] posed)
        {
            var result = new Vector3[posed.Length];
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var matrices = new Matrix4x4[bindposes.Length];
            for (int i = 0; i < matrices.Length; i++)
                matrices[i] = i < bones.Length && bones[i] ? bones[i].localToWorldMatrix * bindposes[i] : renderer.localToWorldMatrix;
            using (var counts = mesh.GetBonesPerVertex())
            using (var weights = mesh.GetAllBoneWeights())
            {
                int offset = 0;
                for (int i = 0; i < posed.Length; i++)
                {
                    int count = i < counts.Length ? counts[i] : 0;
                    if (count == 0) result[i] = renderer.localToWorldMatrix.MultiplyPoint3x4(posed[i]);
                    for (int j = 0; j < count; j++)
                    {
                        var weight = weights[offset++];
                        if (weight.boneIndex < 0 || weight.boneIndex >= matrices.Length)
                            throw new InvalidOperationException("骨骼权重索引超出 Bind Pose 范围");
                        result[i] += matrices[weight.boneIndex].MultiplyPoint3x4(posed[i]) * weight.weight;
                    }
                }
            }
            return result;
        }

        public static bool InsideBox(Transform box, Vector3 world)
        {
            var local = box.InverseTransformPoint(world);
            return Mathf.Abs(local.x) <= .500001f && Mathf.Abs(local.y) <= .500001f && Mathf.Abs(local.z) <= .500001f;
        }

        private static IEnumerable<int> EffectiveVertices(StarmeshOperator op,
            HMeshLibSpaceTransformer space, Vector3[] posed, Dictionary<int, float> selection,
            Func<bool> cancel)
        {
            selection = selection.Where(v => v.Value > 0f && !float.IsNaN(v.Value))
                .ToDictionary(v => v.Key, v => v.Value);
            if (selection.Count == 0) return Array.Empty<int>();
            string passName = null;
            int frames = 1;
            if (op is StarmeshOperatorRippleWave wave)
            { passName = "RippleWavePass"; frames = Mathf.Clamp(wave.frameCount, 1, 25); }
            else if (op is StarmeshOperatorCompressiveDeform compression)
            { passName = "CompressiveDeformPass"; frames = Mathf.Clamp(compression.frameCount, 1, 25); }
            else if (op is StarmeshOperatorTRS) passName = "TRSDeformPass";
            // Bone painting and shape transfer retain selection-based matching.
            if (passName == null) return selection.Keys;
            var type = typeof(MeshConstructor).Assembly.GetType("Hai.Starmesh.Editor." + passName, true);
            var pass = Activator.CreateInstance(type, op, space, posed, selection);
            var mutate = (Action<float, Vector3[]>)Delegate.CreateDelegate(typeof(Action<float, Vector3[]>),
                pass, type.GetMethod("MutateVertices"));
            var linear = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), pass, type.GetMethod("IsLinearInterpolation"));
            if (linear()) frames = 1;
            var moved = new HashSet<int>();
            var deltas = new Vector3[posed.Length];
            for (int frame = 1; frame <= frames; frame++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                Array.Clear(deltas, 0, deltas.Length);
                mutate(100f * frame / frames, deltas);
                foreach (int vertex in selection.Keys)
                {
                    var delta = deltas[vertex];
                    if (float.IsNaN(delta.x) || float.IsNaN(delta.y) || float.IsNaN(delta.z) ||
                        float.IsInfinity(delta.x) || float.IsInfinity(delta.y) || float.IsInfinity(delta.z))
                        throw new InvalidOperationException("效果计算产生无效位移，请检查效果大小和曲线设置");
                    if (delta.sqrMagnitude > 0f) moved.Add(vertex);
                }
            }
            return moved;
        }

        public static StarmeshOperator[] Users(Transform root, StarmeshSelectMeshes target)
        {
            return root.GetComponentsInChildren<StarmeshOperator>(true)
                .Where(op => op.SafeSelectors().Contains(target)).ToArray();
        }

        public static bool HasFilter(StarmeshOperator op, Transform root)
        {
            var selectors = op.SafeSelectors();
            return HSmSelectorUtil.AllBones(root, selectors).Count > 0 || selectors.Any(s =>
                s is StarmeshSelectConnectedPolygons ||
                s.enabled && (s is StarmeshSelectPointRadius || s is StarmeshSelectLineRadius ||
                              s is StarmeshSelectBlendshapes));
        }

        public static Mesh SourceMesh(SkinnedMeshRenderer renderer, StarmeshSelectMeshes target)
        {
            var data = renderer.GetComponent<StarmeshOriginalMeshData>();
            if (data && data.originalMesh) return data.originalMesh;
            var existing = (target.skinnedMeshes ?? Array.Empty<StarmeshMeshPair>())
                .FirstOrDefault(p => p.targetMesh == renderer);
            return existing.originalMesh ? existing.originalMesh : renderer.sharedMesh;
        }

        private static void ValidateRenderer(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            if (!mesh) throw new InvalidOperationException("没有 Mesh");
            if (mesh.vertexCount == 0) throw new InvalidOperationException("Mesh 没有顶点");
            if (!EditorUtility.IsPersistent(mesh))
                throw new InvalidOperationException("无法确认原始 Mesh；请退出 Starmesh 预览后重试");
            if (renderer.bones.Length == 0 || mesh.bindposes.Length == 0)
                throw new InvalidOperationException("缺少蒙皮骨骼或 Bind Pose");
        }

        // Use the same active-shape semantics as the installed Starmesh 1.7.0.
        // The target's shape settings must also apply to newly discovered candidates.
        private static Vector3[] PosedVertices(Mesh mesh, SkinnedMeshRenderer renderer,
            StarmeshSelectMeshes target, StarmeshSelector[] selectors)
        {
            var vertices = mesh.vertices;
            var active = selectors.OfType<StarmeshSelectMeshes>()
                .Where(s => s == target || s.SafeSkinnedMeshRenderers().Contains(renderer))
                .SelectMany(s => s.activeBlendshapes ?? Array.Empty<StarmeshActiveBlendshapes>());
            foreach (var shape in active)
            {
                int index = mesh.GetBlendShapeIndex(shape.blendshapeName ?? "");
                if (index < 0 || mesh.GetBlendShapeFrameCount(index) == 0) continue;
                var deltas = new Vector3[vertices.Length];
                mesh.GetBlendShapeFrameVertices(index, mesh.GetBlendShapeFrameCount(index) - 1,
                    deltas, null, null);
                for (int i = 0; i < vertices.Length; i++) vertices[i] += deltas[i] * (shape.weight / 100f);
            }
            return vertices;
        }

        public static List<MeshMatch> Analyze(Transform root, StarmeshSelectMeshes target,
            Func<int, int, bool> cancel = null, MatcherOptions options = null)
        {
            if (!root || !target || !target.transform.IsChildOf(root))
                throw new InvalidOperationException("目标组件必须位于所选 Avatar 内");
            options?.Validate(root);
            var users = Users(root, target);
            if (users.Length == 0) throw new InvalidOperationException("没有效果引用这个 Select Meshes；请先关联效果");
            if (users.Any(op => !HasFilter(op, root)) && !(options != null && options.useBox))
                throw new InvalidOperationException("有引用此列表的效果未设置有效骨骼或范围等筛选条件。为避免匹配整个 Avatar，请先配置选择器。");
            foreach (var op in users)
            {
                var av = op.AvatarOptional();
                if (av && av.avatarRoot && av.avatarRoot != root)
                    throw new InvalidOperationException("效果指定的 Starmesh Avatar Root 与当前 Avatar 不一致");
            }
            var renderers = Candidates(root, options);
            var previous = new HashSet<SkinnedMeshRenderer>(target.SafeSkinnedMeshRenderers());
            var results = new List<MeshMatch>();
            for (int i = 0; i < renderers.Length; i++)
            {
                if (cancel != null && cancel(i, renderers.Length)) throw new OperationCanceledException();
                var renderer = renderers[i];
                var row = new MeshMatch {
                    renderer = renderer, previouslyIncluded = previous.Contains(renderer),
                    isBody = options != null && options.body == renderer,
                    outsideWhitelist = options != null && options.body != renderer && !options.IsWhitelisted(renderer.transform),
                    blacklisted = options != null && options.IsBlacklisted(renderer.transform),
                    minimumVertices = options == null ? 1 : Mathf.Max(1, options.minimumVertices)
                };
                if (row.blacklisted || row.outsideWhitelist) { results.Add(row); continue; }
                try
                {
                    var mesh = SourceMesh(renderer, target);
                    ValidateRenderer(renderer, mesh);
                    if (row.isBody) { results.Add(row); continue; }
                    var selected = new HashSet<int>();
                    foreach (var op in users)
                    {
                        var selectors = op.SafeSelectors();
                        var data = renderer.GetComponent<StarmeshOriginalMeshData>();
                        var pair = (target.skinnedMeshes ?? Array.Empty<StarmeshMeshPair>())
                            .FirstOrDefault(p => p.targetMesh == renderer);
                        var bone = data && data.overrideBoneZero ? data.overrideBoneZero : pair.overrideBoneZero;
                        var fallback = data ? StarmeshMeshPair.CreateFallbackLocalToWorldTRS(data) : Matrix4x4.identity;
                        var space = new HMeshLibSpaceTransformer();
                        space.Define(root, renderer, mesh, bone, fallback);
                        var posed = PosedVertices(mesh, renderer, target, selectors);
                        var selection = HSmSelectionAlgos.BuildSelection(root, renderer, mesh, selectors, space, posed);
                        var effective = EffectiveVertices(op, space, posed, selection,
                            cancel == null ? (Func<bool>)null : () => cancel(i, renderers.Length));
                        if (options != null && options.useBox)
                        {
                            var world = WorldVertices(renderer, mesh, posed);
                            effective = effective.Where(v => InsideBox(options.box, world[v]));
                        }
                        selected.UnionWith(effective);
                    }
                    row.selectedVertices = selected.Count;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { row.error = ex.InnerException?.Message ?? ex.Message; }
                results.Add(row);
            }
            return results;
        }

        public static MeshRule RuleFor(Dictionary<SkinnedMeshRenderer, MeshRule> rules, SkinnedMeshRenderer renderer)
            => rules.TryGetValue(renderer, out var rule) ? rule : MeshRule.Auto;

        public static bool ShouldInclude(MeshMatch row, Dictionary<SkinnedMeshRenderer, MeshRule> rules)
        {
            var rule = RuleFor(rules, row.renderer);
            if (rule == MeshRule.Exclude || row.blacklisted) return false;
            if (row.outsideWhitelist) return row.previouslyIncluded;
            if (!string.IsNullOrEmpty(row.error)) return row.previouslyIncluded;
            return row.isBody || rule == MeshRule.Include || row.selectedVertices >= row.minimumVertices;
        }

        public static void Apply(Transform root, StarmeshSelectMeshes target, List<MeshMatch> rows,
            Dictionary<SkinnedMeshRenderer, MeshRule> rules, MatcherOptions options = null)
        {
            if (!root || !target || !target.transform.IsChildOf(root))
                throw new InvalidOperationException("目标组件必须位于所选 Avatar 内");
            if (rows.Any(r => !r.renderer || !r.renderer.transform.IsChildOf(root)))
                throw new InvalidOperationException("分析结果已失效，请重新匹配");
            if (rows.Any(r => !r.blacklisted && !r.outsideWhitelist && RuleFor(rules, r.renderer) == MeshRule.Include && !string.IsNullOrEmpty(r.error)))
                throw new InvalidOperationException("始终包含的模型存在错误，请先修复或改为自动/排除");
            options?.Validate(root);
            if (options != null && options.body && RuleFor(rules, options.body) == MeshRule.Exclude)
                throw new InvalidOperationException("Body 已被设为“始终排除”。请改为自动或始终包含，避免身体漏选。");
            if (rows.Any(r => r.isBody && !string.IsNullOrEmpty(r.error)))
                throw new InvalidOperationException("Body 数据异常：" + rows.First(r => r.isBody && !string.IsNullOrEmpty(r.error)).error);
            var existing = target.skinnedMeshes ?? Array.Empty<StarmeshMeshPair>();
            var retainedOutside = new HashSet<SkinnedMeshRenderer>(existing.Where(p => p.targetMesh &&
                    options != null && options.useWhitelist && !options.IsWhitelisted(p.targetMesh.transform) &&
                    options.body != p.targetMesh &&
                    !options.IsBlacklisted(p.targetMesh.transform) && RuleFor(rules, p.targetMesh) != MeshRule.Exclude)
                .Select(p => p.targetMesh));
            var keep = rows.Where(r => ShouldInclude(r, rules)).Select(r => r.renderer)
                .Concat(retainedOutside).Distinct().ToArray();
            // Keep out-of-scope references rather than silently deleting another Avatar's data.
            if (existing.Any(p => p.targetMesh && !p.targetMesh.transform.IsChildOf(root)))
                throw new InvalidOperationException("现有目标包含所选 Avatar 外的对象，请先手动处理这些引用");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Starmesh 自动匹配 Mesh");
            try
            {
                var pairs = new List<StarmeshMeshPair>();
                foreach (var renderer in keep)
                {
                    var pair = existing.FirstOrDefault(p => p.targetMesh == renderer);
                    pair.targetMesh = renderer;
                    if (retainedOutside.Contains(renderer) || rows.Any(r => r.renderer == renderer && r.outsideWhitelist))
                    { pairs.Add(pair); continue; }
                    var data = renderer.GetComponent<StarmeshOriginalMeshData>();
                    if (!data || !data.originalMesh)
                    {
                        var mesh = SourceMesh(renderer, target);
                        ValidateRenderer(renderer, mesh);
                        if (!data) data = Undo.AddComponent<StarmeshOriginalMeshData>(renderer.gameObject);
                        Undo.RecordObject(data, "登记原始 Mesh");
                        data.originalMesh = mesh;
                        data.overrideBoneZero = pair.overrideBoneZero;
                        var bone = data.overrideBoneZero ? data.overrideBoneZero : renderer.bones[0];
                        if (bone)
                        {
                            data.overrideLocalToWorldPosition = bone.position;
                            data.overrideLocalToWorldRotation = bone.rotation;
                            data.overrideLocalToWorldScale = bone.lossyScale;
                        }
                        EditorUtility.SetDirty(data);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(data);
                    }
                    pairs.Add(pair);
                }
                Undo.RecordObject(target, "更新 Starmesh 目标");
                target.skinnedMeshes = pairs.ToArray();
                EditorUtility.SetDirty(target);
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                // Restore removed preview meshes only if no other selector still uses them.
                var used = new HashSet<SkinnedMeshRenderer>(root.GetComponentsInChildren<StarmeshSelectMeshes>(true)
                    .SelectMany(s => s.SafeSkinnedMeshRenderers()));
                foreach (var pair in existing)
                {
                    var renderer = pair.targetMesh;
                    if (!renderer || used.Contains(renderer)) continue;
                    var mesh = SourceMesh(renderer, target);
                    if (!mesh || !EditorUtility.IsPersistent(mesh)) mesh = pair.originalMesh;
                    if (mesh && EditorUtility.IsPersistent(mesh) && renderer.sharedMesh != mesh)
                    {
                        Undo.RecordObject(renderer, "恢复原始 Mesh");
                        renderer.sharedMesh = mesh;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }
                }
                EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
                Undo.CollapseUndoOperations(group);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
    }

}
#pragma warning restore CS0612

#endif
