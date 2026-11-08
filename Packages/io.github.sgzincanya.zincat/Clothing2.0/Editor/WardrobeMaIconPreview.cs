#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;
using nadena.dev.modular_avatar.core;

namespace Zincat.Clothing.Editor
{
    // 独立预览会话仅处理选中网格，不修改来源 Avatar，也不运行上传构建。
    internal sealed class WardrobeMaIconPreview : IDisposable
    {
        internal static WardrobeMaIconPreview Active { get; private set; }
        private static bool preparing;
        internal static bool IsSupportedRenderer(Renderer renderer) => renderer is MeshRenderer || renderer is SkinnedMeshRenderer;
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly PreviewSession session = new PreviewSession();
        private readonly HashSet<Renderer> selected;
        private readonly MethodInfo preCull = typeof(PreviewSession).GetMethod("OnPreCull", F);
        private readonly PropertyInfo mapping = typeof(PreviewSession).GetProperty("OriginalToProxyRenderer", F);
        private readonly FieldInfo proxySession = typeof(PreviewSession).GetField("_proxySession", F);
        private bool disposed;
        internal static int PreparedCount { get; private set; }
        private RendererState[] baseline;
        private string currentTargets;
        private IReadOnlyDictionary<Renderer, Renderer> proxyMap;
        private sealed class RendererState
        {
            private readonly Renderer renderer;
            private readonly Material[] materials;
            private readonly float[] weights;
            internal RendererState(Renderer renderer)
            {
                this.renderer = renderer; materials = renderer.sharedMaterials;
                if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                    weights = Enumerable.Range(0, skin.sharedMesh.blendShapeCount).Select(skin.GetBlendShapeWeight).ToArray();
            }
            internal void Restore()
            {
                if (renderer == null) return;
                renderer.sharedMaterials = materials;
                if (renderer is SkinnedMeshRenderer skin && weights != null && skin.sharedMesh != null)
                    for (int i = 0; i < Math.Min(weights.Length, skin.sharedMesh.blendShapeCount); i++) skin.SetBlendShapeWeight(i, weights[i]);
            }
        }
        private sealed class BaselineFilter : IRenderFilter
        {
            private readonly HashSet<Renderer> renderers;
            internal BaselineFilter(HashSet<Renderer> renderers) { this.renderers = renderers; }
            public bool CanEnableRenderers => true;
            public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context) => ImmutableList.Create(RenderGroup.For(renderers));
            public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context) => Task.FromResult<IRenderFilterNode>(new Node());
            private sealed class Node : IRenderFilterNode { public RenderAspects WhatChanged => 0; }
        }

        private sealed class ScopedFilter : IRenderFilter
        {
            private readonly IRenderFilter inner;
            private readonly HashSet<Renderer> selected;
            public bool CanEnableRenderers => true;
            public bool StrictRenderGroup => true;
            internal ScopedFilter(IRenderFilter inner, HashSet<Renderer> selected) { this.inner = inner; this.selected = selected; }
            public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
            {
                // RenderGroup.Filter 保留原组携带的数据，供 MA Instantiate 使用。
                var filter = typeof(RenderGroup).GetMethod("Filter", F);
                return inner.GetTargetGroups(context).Where(g => g.Renderers.Any(selected.Contains))
                    .Select(g => (RenderGroup)filter.Invoke(g, new object[] { selected })).Where(g => !g.IsEmpty).ToImmutableList();
            }
            public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context)
                => inner.Instantiate(group, pairs, context);
        }

        private WardrobeMaIconPreview(IEnumerable<GameObject> roots)
        {
            selected = roots.Where(t => t != null).SelectMany(t => t.GetComponentsInChildren<Renderer>(true)).Where(IsSupportedRenderer).ToHashSet();
            // 同步的源网格可能位于衣服外（如身体），仅加入预览计算，不加入相机绘制集合。
            var queue = new Queue<Renderer>(selected);
            while (queue.Count > 0)
            {
                var renderer = queue.Dequeue();
                foreach (var sync in renderer.GetComponents<ModularAvatarBlendshapeSync>())
                    foreach (var binding in sync.Bindings)
                    {
                        var obj = binding.ReferenceMesh?.Get(sync);
                        var source = obj == null ? null : obj.GetComponent<SkinnedMeshRenderer>();
                        if (source != null && selected.Add(source)) queue.Enqueue(source);
                    }
            }
            if (preCull == null || mapping == null || proxySession == null) throw new InvalidOperationException("当前 NDMF 版本缺少截图预览接口，未使用原始网格代替。");
            string[] names = { "ScaleAdjusterPreview", "MeshDeleterPreview", "ShapeChangerPreview", "MaterialSetterPreview", "RemoveVertexColorPreview" };
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("nadena.dev.modular_avatar.core.editor.ScaleAdjusterPreview") != null);
            if (assembly == null) throw new InvalidOperationException("未找到 MA 外观预览模块。");
            session.AddMutator(new SequencePoint(), new BaselineFilter(selected));
            foreach (var name in names)
            {
                var type = assembly.GetType("nadena.dev.modular_avatar.core.editor." + name);
                if (type == null) throw new InvalidOperationException("MA 版本缺少 " + name + "，未生成不完整截图。");
                session.AddMutator(new SequencePoint(), new ScopedFilter((IRenderFilter)System.Activator.CreateInstance(type, true), selected));
            }
        }

        internal static bool IsNeeded(IEnumerable<GameObject> targets)
        {
            string[] names = { "ModularAvatarScaleAdjuster", "ModularAvatarShapeChanger", "ModularAvatarMaterialSetter", "ModularAvatarMeshDeleter", "ModularAvatarRemoveVertexColor", "ModularAvatarBlendshapeSync" };
            foreach (var t in targets.Where(t => t != null))
            {
                var root = t.transform;
                while (root.parent != null && root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null) root = root.parent;
                if (root.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c != null && names.Contains(c.GetType().Name))) return true;
            }
            return false;
        }

        internal static async Task<WardrobeMaIconPreview> PrepareAsync(IEnumerable<GameObject> targets)
        {
            var roots = targets.Where(t => t != null).ToArray();
            if (!roots.SelectMany(t => t.GetComponentsInChildren<Renderer>(true)).Any(IsSupportedRenderer)) return null;
            if (!IsNeeded(roots)) return null;
            if (Active != null || preparing) throw new InvalidOperationException("另一次图标预览仍在处理中。");
            preparing = true;
            WardrobeMaIconPreview scope = null;
            try
            {
                PropCacheDebug.InvalidateAllCaches(); ComputeContext.FlushInvalidates();
                await NextUpdate();
                scope = new WardrobeMaIconPreview(roots);
                double end = EditorApplication.timeSinceStartup + 90;
                while (true)
                {
                    scope.preCull.Invoke(scope.session, new object[] { true });
                    var ps = scope.proxySession.GetValue(scope.session);
                    var active = ps.GetType().GetField("_active", F).GetValue(ps);
                    if (active != null && (bool)active.GetType().GetProperty("IsReady").GetValue(active) &&
                        !(bool)active.GetType().GetProperty("IsInvalidated", F).GetValue(active))
                    {
                        // 新管线切换完成后再推进一帧，更新骨骼、权重、材质。
                        scope.preCull.Invoke(scope.session, new object[] { true });
                        scope.proxyMap = (IReadOnlyDictionary<Renderer, Renderer>)scope.mapping.GetValue(scope.session);
                        scope.baseline = scope.proxyMap.Values.Where(r => r != null).Distinct().Select(r => new RendererState(r)).ToArray();
                        scope.SelectTargets(roots);
                        PreparedCount++;
                        Active = scope; return scope;
                    }
                    var next = ps.GetType().GetField("_next", F).GetValue(ps);
                    if (next != null && (bool)next.GetType().GetProperty("IsFailed").GetValue(next)) throw new InvalidOperationException("MA 截图预览处理失败，未保存原始网格截图。");
                    if (EditorApplication.timeSinceStartup >= end) throw new InvalidOperationException("等待 MA 外观预览超时，未更新图标。");
                    await NextUpdate();
                }
            }
            catch { scope?.Dispose(); throw; }
            finally { preparing = false; }
        }

        private static Task NextUpdate()
        {
            var task = new TaskCompletionSource<bool>();
            void Tick() { EditorApplication.update -= Tick; task.TrySetResult(true); }
            EditorApplication.update += Tick; EditorApplication.QueuePlayerLoopUpdate(); return task.Task;
        }

        internal Renderer Resolve(Renderer original)
        {
            var map = proxyMap ?? (IReadOnlyDictionary<Renderer, Renderer>)mapping.GetValue(session);
            return map.TryGetValue(original, out var proxy) && proxy != null ? proxy : original;
        }

        internal void SelectTargets(IEnumerable<GameObject> targets)
        {
            if (disposed) throw new ObjectDisposedException(nameof(WardrobeMaIconPreview));
            var roots = targets.Where(t => t != null).Distinct().OrderBy(t => t.GetInstanceID()).ToArray();
            if (roots.SelectMany(r => r.GetComponentsInChildren<Renderer>(true)).Where(IsSupportedRenderer).Any(r => !selected.Contains(r)))
                throw new InvalidOperationException("截图对象不在本套衣服的预览范围内，未复用错误网格。");
            string key = string.Join(",", roots.Select(r => r.GetInstanceID()));
            if (key == currentTargets) return;
            currentTargets = null;
            foreach (var state in baseline) state.Restore();
            // 复用骨骼和网格，但每张图从原始预览状态重新计算虚拟开关和同步权重。
            ApplyVirtualRootState(roots);
            ApplyBlendshapeSync();
            currentTargets = key;
        }

        private void ApplyBlendshapeSync()
        {
            var links = new Dictionary<(SkinnedMeshRenderer, string), (SkinnedMeshRenderer source, string shape, AnimationCurve curve)>();
            foreach (var renderer in selected.OfType<SkinnedMeshRenderer>())
                foreach (var sync in renderer.GetComponents<ModularAvatarBlendshapeSync>())
                    foreach (var binding in sync.Bindings)
                    {
                        var obj = binding.ReferenceMesh?.Get(sync); var source = obj == null ? null : obj.GetComponent<SkinnedMeshRenderer>();
                        string local = string.IsNullOrWhiteSpace(binding.LocalBlendshape) ? binding.Blendshape : binding.LocalBlendshape;
                        if (source?.sharedMesh == null || renderer.sharedMesh == null || renderer.sharedMesh.GetBlendShapeIndex(local) < 0 || source.sharedMesh.GetBlendShapeIndex(binding.Blendshape) < 0) continue;
                        links[(renderer, local)] = (source, binding.Blendshape, binding.RemapCurve);
                    }
            var visiting = new HashSet<(SkinnedMeshRenderer, string)>(); var values = new Dictionary<(SkinnedMeshRenderer, string), float>();
            float Current((SkinnedMeshRenderer renderer, string shape) key)
            {
                var proxy = Resolve(key.renderer) as SkinnedMeshRenderer; int index = proxy?.sharedMesh == null ? -1 : proxy.sharedMesh.GetBlendShapeIndex(key.shape);
                return index >= 0 ? proxy.GetBlendShapeWeight(index) : key.renderer.GetBlendShapeWeight(key.renderer.sharedMesh.GetBlendShapeIndex(key.shape));
            }
            float Evaluate((SkinnedMeshRenderer renderer, string shape) key)
            {
                if (values.TryGetValue(key, out float value)) return value;
                if (!visiting.Add(key)) throw new InvalidOperationException("MA 形态键同步存在循环依赖：" + key.renderer.name + "/" + key.shape);
                value = Current(key);
                if (links.TryGetValue(key, out var link))
                {
                    var sourceKey = (link.source, link.shape);
                    value = sourceKey == key ? Current(key) : Evaluate(sourceKey);
                    if (link.curve != null && link.curve.length >= 2)
                    {
                        // 使用 MA 自己的分段线性及区间外映射规则。
                        var type = typeof(ModularAvatarBlendshapeSync).GetNestedType("RemapCurve", BindingFlags.NonPublic);
                        var curve = System.Activator.CreateInstance(type, new object[] { link.curve });
                        var point = type.GetMethod("GetPointOnCurve").Invoke(curve, new object[] { value });
                        value = (float)point.GetType().GetProperty("MappedValue").GetValue(point);
                    }
                }
                visiting.Remove(key); return values[key] = value;
            }
            foreach (var key in links.Keys)
            {
                var proxy = Resolve(key.Item1) as SkinnedMeshRenderer;
                if (proxy == null || proxy == key.Item1) throw new InvalidOperationException("同步目标没有独立预览网格，未修改来源网格。");
                int index = proxy.sharedMesh == null ? -1 : proxy.sharedMesh.GetBlendShapeIndex(key.Item2);
                if (index >= 0) proxy.SetBlendShapeWeight(index, Evaluate(key));
            }
        }

        private static object Member(object obj, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return obj.GetType().GetField(name, flags)?.GetValue(obj) ?? obj.GetType().GetProperty(name, flags)?.GetValue(obj);
        }
        private void ApplyVirtualRootState(GameObject[] roots)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("nadena.dev.modular_avatar.core.editor.ReactiveObjectAnalyzer")).FirstOrDefault(t => t != null);
            if (type == null) throw new InvalidOperationException("缺少 MA 外观规则分析器。");
            foreach (var group in roots.GroupBy(r => r.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true)))
            {
                if (group.Key == null) continue;
                // 用MA局部分析器模拟穿上这件衣服，不开关原场景，也不改全局模拟器。
                var analyzer = System.Activator.CreateInstance(type, new object[] { ComputeContext.NullContext });
                var overrides = ImmutableDictionary<string, float>.Empty;
                foreach (var root in group)
                {
                    var owner = root.transform;
                    while (owner.parent != null && owner.parent != group.Key.transform) owner = owner.parent;
                    foreach (var forced in new[] { owner.gameObject, root }.Distinct())
                    {
                        string property = (string)type.GetMethod("GetGameObjectStateProperty").Invoke(analyzer, new object[] { forced });
                        if (property != null) overrides = overrides.SetItem(property, 1);
                    }
                }
                type.GetProperty("ForcePropertyOverrides").SetValue(analyzer, overrides);
                var result = type.GetMethod("Analyze").Invoke(analyzer, new object[] { group.Key.gameObject });
                var properties = (System.Collections.IDictionary)Member(result, "Shapes");
                foreach (var property in properties.Values)
                {
                    var target = Member(property, "TargetProp");
                    if (!(Member(target, "TargetObject") is Renderer renderer) || !selected.Contains(renderer)) continue;
                    var proxy = Resolve(renderer); if (proxy == renderer) continue;
                    var name = (string)Member(target, "PropertyName");
                    var rules = ((System.Collections.IEnumerable)Member(property, "actionGroups")).Cast<object>();
                    var active = rules.LastOrDefault(r => (bool)Member(r, "InitiallyActive"));
                    var value = active == null ? null : Member(active, "Value");
                    if (name.StartsWith("blendShape.", StringComparison.Ordinal) && proxy is SkinnedMeshRenderer skin && renderer is SkinnedMeshRenderer original)
                    {
                        string shape = name.Substring("blendShape.".Length); int index = skin.sharedMesh.GetBlendShapeIndex(shape), sourceIndex = original.sharedMesh.GetBlendShapeIndex(shape);
                        if (index >= 0 && sourceIndex >= 0) skin.SetBlendShapeWeight(index, value is float weight ? Mathf.Clamp(weight, 0, 100) : original.GetBlendShapeWeight(sourceIndex));
                    }
                    const string prefix = "m_Materials.Array.data[";
                    if (name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        int index = int.Parse(name.Substring(prefix.Length, name.IndexOf(']') - prefix.Length)); var mats = proxy.sharedMaterials;
                        if (index >= 0 && index < mats.Length && index < renderer.sharedMaterials.Length)
                        { mats[index] = value is Material mat ? mat : renderer.sharedMaterials[index]; proxy.sharedMaterials = mats; }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (Active == this) Active = null;
            session.Dispose();
            baseline = null; proxyMap = null;
        }
    }
}
#endif
