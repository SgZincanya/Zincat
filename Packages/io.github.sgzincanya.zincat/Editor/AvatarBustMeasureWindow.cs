using System;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Zincat.AvatarMeasurement
{
    internal sealed class AvatarBustMeasureWindow : EditorWindow
    {
        private const string MenuPath = "Zincat/Avatar 胸围测量";
        private static readonly Color TopColor = new Color(1f, 0.52f, 0.66f);
        private static readonly Color UnderColor = new Color(0.34f, 0.8f, 1f);

        [SerializeField] private Transform avatar;
        [SerializeField] private SkinnedMeshRenderer body;
        [SerializeField] private float top = 1.05f;
        [SerializeField] private float under = 0.97f;
        [SerializeField] private float topTilt;
        [SerializeField] private float underTilt;
        [SerializeField] private float foot;
        [SerializeField] private float head = 1.5f;
        [SerializeField] private bool rescale = true;
        [SerializeField] private float realHeight = 160f;
        [SerializeField] private bool showGuides = true;
        [SerializeField] private bool showSettings;

        private Vector2 scroll;
        private GUIStyle noteStyle;
        private Mesh baked;
        private Vector3[] vertices;
        private int[] triangles;
        private Mesh sourceMesh;
        private Matrix4x4 sampledTransform;
        private bool sampleDirty;
        private Vector3 anchor;
        private float guideWidth = 0.2f;
        private Section topSection;
        private Section underSection;
        private string error;

        [MenuItem(MenuPath)]
        private static void Open()
        {
            var window = GetWindow<AvatarBustMeasureWindow>("Avatar 胸围测量");
            window.minSize = new Vector2(335f, 230f);
            if (!window.avatar && Selection.activeTransform)
                window.SetAvatar(FindAvatarRoot(Selection.activeTransform));
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += DrawScene;
            Undo.undoRedoPerformed += Refresh;
            Undo.postprocessModifications += OnInspectorModifications;
            EditorApplication.hierarchyChanged += Refresh;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DrawScene;
            Undo.undoRedoPerformed -= Refresh;
            Undo.postprocessModifications -= OnInspectorModifications;
            EditorApplication.hierarchyChanged -= Refresh;
            if (baked) DestroyImmediate(baked);
        }

        private void OnGUI()
        {
            if (noteStyle == null)
                noteStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };

            using (var scope = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = scope.scrollPosition;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8f);
                    using (new EditorGUILayout.VerticalScope())
                        DrawContent();
                    GUILayout.Space(8f);
                }
            }
        }

        private void DrawContent()
        {
            GUILayout.Space(8f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("测量对象", EditorStyles.boldLabel);
                var root = (Transform)EditorGUILayout.ObjectField("Avatar 根节点", avatar, typeof(Transform), true);
                if (root != avatar) SetAvatar(root);
                var selectedBody = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Body 网格", body, typeof(SkinnedMeshRenderer), true);
                if (selectedBody != body) SetBody(selectedBody);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!Selection.activeTransform))
                    {
                        if (GUILayout.Button("使用当前选择"))
                            SetAvatar(FindAvatarRoot(Selection.activeTransform));
                    }
                    using (new EditorGUI.DisabledScope(!avatar))
                    {
                        if (GUILayout.Button("自动寻找 Body")) SetBody(FindBody());
                    }
                }
            }

            if (!avatar || !body)
            {
                EditorGUILayout.HelpBox("请选择 Avatar 根节点和包含完整躯干的 Body 网格。", MessageType.Info);
                return;
            }

            EnsureSample();
            GUILayout.Space(6f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("自动定位", GUILayout.Height(24f)))
                {
                    Undo.RecordObject(this, "自动定位胸围断面");
                    Estimate();
                }
                if (GUILayout.Button("重新测量", GUILayout.Height(24f))) Refresh();
            }
            if (showGuides)
                GUILayout.Label("在 Scene 里拖动彩色手柄微调断面。", noteStyle);

            GUILayout.Space(6f);
            DrawPlaneFields("TOP 上胸围", TopColor,
                "让断面经过两侧乳头（胸部最凸处），绕胸部和背部一周。", ref top, ref topTilt);
            GUILayout.Space(4f);
            DrawPlaneFields("UNDER 下胸围", UnderColor,
                "让断面贴着乳房下缘，沿胸部与胸廓交界处绕一周。", ref under, ref underTilt);

            GUILayout.Space(6f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                DrawResults();
            GUILayout.Space(6f);
            DrawSettings();
            GUILayout.Space(8f);
        }

        private void DrawPlaneFields(string label, Color color, string note, ref float height, ref float tilt)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect marker = GUILayoutUtility.GetRect(7f, EditorGUIUtility.singleLineHeight, GUILayout.Width(7f));
                    marker.y += (marker.height - 7f) / 2f;
                    marker.height = 7f;
                    if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(marker, color);
                    EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                }
                GUILayout.Label(note, noteStyle);
                GUILayout.Space(3f);
                EditorGUI.BeginChangeCheck();
                float nextHeight = EditorGUILayout.DelayedFloatField("高度 Y（米）", height * Unit);
                float nextTilt = EditorGUILayout.Slider("前后倾斜（°）", tilt, -60f, 60f);
                if (!EditorGUI.EndChangeCheck()) return;
                Undo.RecordObject(this, "调整胸围断面");
                if (IsFinite(nextHeight)) height = nextHeight / Unit;
                tilt = nextTilt;
                Measure();
            }
        }

        private void DrawSettings()
        {
            showSettings = EditorGUILayout.Foldout(showSettings, "身高换算与 Scene 显示", true);
            if (!showSettings) return;
            EditorGUI.BeginChangeCheck();
            bool nextGuides = EditorGUILayout.Toggle("显示 Scene 手柄", showGuides);
            bool nextRescale = EditorGUILayout.Toggle("按现实身高换算", rescale);
            float nextFoot = EditorGUILayout.DelayedFloatField("足底 Y（米）", foot * Unit);
            float nextHead = EditorGUILayout.DelayedFloatField("头顶 Y（米）", head * Unit);
            float nextReal = realHeight;
            if (nextRescale) nextReal = EditorGUILayout.DelayedFloatField("现实身高（cm）", realHeight);
            if (!EditorGUI.EndChangeCheck()) return;
            Undo.RecordObject(this, "调整胸围测量设置");
            showGuides = nextGuides;
            rescale = nextRescale;
            if (IsFinite(nextFoot)) foot = nextFoot / Unit;
            if (IsFinite(nextHead)) head = nextHead / Unit;
            if (IsFinite(nextReal)) realHeight = nextReal;
            SceneView.RepaintAll();
        }

        private void DrawResults()
        {
            if (!string.IsNullOrEmpty(error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Warning);
                return;
            }

            float avatarHeight = (head - foot) * Unit;
            bool validHeight = !rescale || (avatarHeight > 0.0001f && realHeight > 0f);
            float factor = validHeight && rescale ? realHeight / avatarHeight : 100f;
            EditorGUILayout.LabelField(validHeight && rescale ? "换算尺寸" : "模型尺寸", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("上胸围", FormatSection(topSection, factor));
            EditorGUILayout.LabelField("下胸围", FormatSection(underSection, factor));
            if (!validHeight)
                EditorGUILayout.HelpBox("换算需要头顶高于足底，且现实身高大于零。", MessageType.Info);
            if (topSection.IsEstimated || underSection.IsEstimated)
                GUILayout.Label("带 ≈ 的数值补合了断面缺口，请在 Scene 中核对测量线。", noteStyle);
            if (!topSection.IsValid || !underSection.IsValid || !validHeight) return;

            float underCm = underSection.Length * Unit * factor;
            float difference = topSection.Length * Unit * factor - underCm;
            EditorGUILayout.LabelField("上下胸围差", $"{difference:F1} cm");
            if (difference < 0f)
            {
                GUILayout.Label("上胸围小于下胸围，请检查 TOP 和 UNDER 的断面位置。", noteStyle);
                return;
            }

            string size = JapaneseSize(difference, underCm, out bool extrapolated);
            EditorGUILayout.LabelField(extrapolated ? "日规档距推算" : "日规尺码参考", size, EditorStyles.boldLabel);
            if (extrapolated)
                GUILayout.Label("按罩杯每档 2.5 cm、下围每档 5 cm 推算。实际尺码以品牌表为准。", noteStyle);
        }

        private string FormatSection(Section section, float factor)
        {
            if (!section.IsValid) return "未找到躯干断面";
            string prefix = section.IsEstimated ? "≈ " : string.Empty;
            return $"{prefix}{section.Length * Unit * factor:F1} cm";
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static string JapaneseSize(float difference, float underCm, out bool extrapolated)
        {
            const float aaUpper = 8.75f;
            const float cupStep = 2.5f;
            const float bandStep = 5f;
            double band = Math.Floor(underCm / (double)bandStep + 0.5) * bandStep;
            extrapolated = difference > 31.25f || band < 60 || band > 110;

            if (difference < 6.25f) return $"AA 以下 / {band:0}";
            if (difference <= aaUpper) return $"AA / {band:0}";
            if (difference > aaUpper + ('Z' - 'A' + 1) * cupStep) return $"Z 以上 / {band:0}";

            int index = Mathf.CeilToInt((difference - aaUpper) / cupStep) - 1;
            char cup = (char)('A' + index);
            return $"{cup}{band:0}";
        }

        private static Transform FindAvatarRoot(Transform selection)
        {
            var animator = selection.GetComponentInParent<Animator>();
            return animator ? animator.transform : selection;
        }

        private SkinnedMeshRenderer FindBody()
        {
            if (!avatar) return null;
            SkinnedMeshRenderer best = null;
            float rank = float.NegativeInfinity;
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer.sharedMesh) continue;
                bool namedBody = renderer.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0
                    || renderer.name.Contains("身体") || renderer.name.Contains("素体");
                float score = renderer.sharedMesh.vertexCount + (namedBody ? 1000000f : 0f)
                    + (renderer.enabled && renderer.gameObject.activeInHierarchy ? 100000f : 0f);
                if (score <= rank) continue;
                rank = score;
                best = renderer;
            }
            return best;
        }

        private void SetAvatar(Transform root)
        {
            Undo.RecordObject(this, "选择 Avatar");
            avatar = root;
            body = FindBody();
            Estimate();
        }

        private void SetBody(SkinnedMeshRenderer renderer)
        {
            Undo.RecordObject(this, "选择身体网格");
            if (!avatar && renderer) avatar = FindAvatarRoot(renderer.transform);
            body = renderer;
            Estimate();
        }

        private float Unit => Mathf.Max(0.00001f, avatar.TransformVector(Vector3.up).magnitude);
        private Vector3 ToWorld(Vector3 point) => avatar.position + avatar.rotation * (point * Unit);
        private Vector3 ToFrame(Vector3 point) => Quaternion.Inverse(avatar.rotation) * (point - avatar.position) / Unit;

        private void EnsureSample()
        {
            if (!avatar || !body) return;
            if (sampleDirty || sampledTransform != FrameMatrix() || sourceMesh != body.sharedMesh || (vertices == null && error == null))
                Refresh();
        }

        private UndoPropertyModification[] OnInspectorModifications(UndoPropertyModification[] changes)
        {
            if (!avatar || !body) return changes;
            foreach (var change in changes)
            {
                var target = change.currentValue?.target;
                if (target != body && !(target is ModularAvatarScaleAdjuster adjuster &&
                    adjuster.transform.IsChildOf(avatar))) continue;
                sampleDirty = true;
                Repaint();
                SceneView.RepaintAll();
                break;
            }
            return changes;
        }

        private Matrix4x4 FrameMatrix()
        {
            Matrix4x4 frame = Matrix4x4.TRS(avatar.position, avatar.rotation, Vector3.one * Unit);
            return frame.inverse * body.transform.localToWorldMatrix;
        }

        private void Refresh()
        {
            Sample();
            Measure();
        }

        private void Sample()
        {
            sampleDirty = false;
            error = null;
            vertices = null;
            sourceMesh = null;
            if (!avatar || !body) return;
            sampledTransform = FrameMatrix();
            sourceMesh = body.sharedMesh;
            if (!sourceMesh || (body.transform != avatar && !body.transform.IsChildOf(avatar)))
            {
                error = "请选择这个 Avatar 下带有有效网格的 Body。";
                return;
            }

            if (!baked) baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                baked.Clear();
                BakeBody();
                vertices = baked.vertices;
                triangles = baked.triangles;
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] = sampledTransform.MultiplyPoint3x4(vertices[i]);
                if (vertices.Length == 0 || triangles.Length == 0)
                    error = "Body 网格没有可用于测量的三角面。";
            }
            catch (Exception exception)
            {
                vertices = null;
                error = "无法读取 Body 网格。";
                Debug.LogException(exception, body);
            }
        }

        private void BakeBody()
        {
            var bones = body.bones;
            var adjustments = new Dictionary<Transform, Vector3>();
            foreach (var bone in bones)
            {
                if (bone && bone.IsChildOf(avatar) && bone.TryGetComponent(out ModularAvatarScaleAdjuster adjuster)
                    && adjuster.Scale != Vector3.one)
                    adjustments[bone] = adjuster.Scale;
            }

            // Pair the renderer-local bake with the full renderer-to-measurement matrix.
            if (adjustments.Count == 0)
            {
                body.BakeMesh(baked, true);
                return;
            }

            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var copies = new Dictionary<Transform, Transform>();
                // MA gives each adjusted skin joint a scaled child, leaving the original children alone.
                var scaledBones = new Dictionary<Transform, Transform>();
                foreach (var pair in adjustments)
                {
                    var scaled = CreateSampleTransform(scene, "Adjusted skin joint");
                    scaled.SetParent(CopySampleTransform(pair.Key, scene, copies), false);
                    scaled.localScale = pair.Value;
                    scaledBones.Add(pair.Key, scaled);
                }
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i] && scaledBones.TryGetValue(bones[i], out var scaled)) bones[i] = scaled;

                var renderer = CopySampleTransform(body.transform, scene, copies)
                    .gameObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = sourceMesh;
                renderer.quality = body.quality;
                renderer.bones = bones;
                renderer.rootBone = body.rootBone;
                for (int i = 0; i < sourceMesh.blendShapeCount; i++)
                    renderer.SetBlendShapeWeight(i, body.GetBlendShapeWeight(i));
                renderer.BakeMesh(baked, true);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static Transform CopySampleTransform(Transform source, Scene scene, Dictionary<Transform, Transform> copies)
        {
            if (copies.TryGetValue(source, out var copy)) return copy;
            copy = CreateSampleTransform(scene, source.name);
            if (source.parent) copy.SetParent(CopySampleTransform(source.parent, scene, copies), false);
            copy.localPosition = source.localPosition;
            copy.localRotation = source.localRotation;
            copy.localScale = source.localScale;
            copies.Add(source, copy);
            return copy;
        }

        private static Transform CreateSampleTransform(Scene scene, string name)
        {
            var obj = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave);
            SceneManager.MoveGameObjectToScene(obj, scene);
            return obj.transform;
        }

        private void Estimate()
        {
            Sample();
            if (vertices != null && error == null)
            {
                var bounds = new Bounds(vertices[0], Vector3.zero);
                foreach (var point in vertices) bounds.Encapsulate(point);
                foot = bounds.min.y;
                head = bounds.max.y;
                top = Mathf.Lerp(foot, head, 0.72f);
                under = Mathf.Lerp(foot, head, 0.65f);
                var animator = avatar.GetComponent<Animator>();
                if (animator && animator.avatar && animator.avatar.isValid && animator.isHuman)
                {
                    var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                    var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                    var headBone = animator.GetBoneTransform(HumanBodyBones.Head);
                    var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
                    if (headBone && neck)
                    {
                        float headBoneY = ToFrame(headBone.position).y;
                        head = Mathf.Max(head, headBoneY + Mathf.Max(0f, headBoneY - ToFrame(neck.position).y));
                    }
                    if (chest && hips)
                    {
                        float chestY = ToFrame(chest.position).y;
                        float torsoHeight = chestY - ToFrame(hips.position).y;
                        if (torsoHeight > 0.001f)
                        {
                            top = chestY + torsoHeight * 0.15f;
                            under = chestY - torsoHeight * 0.15f;
                        }
                    }
                }
                topTilt = underTilt = 0f;
            }
            Measure();
        }

        private void Measure()
        {
            topSection = underSection = default;
            if (vertices != null && error == null)
            {
                var bounds = new Bounds(vertices[0], Vector3.zero);
                foreach (var point in vertices) bounds.Encapsulate(point);
                anchor = bounds.center;
                var animator = avatar.GetComponent<Animator>();
                if (animator && animator.avatar && animator.avatar.isValid && animator.isHuman)
                {
                    var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                    if (chest) anchor = ToFrame(chest.position);
                }
                guideWidth = Mathf.Max(0.12f / Unit, bounds.extents.x * 0.5f);
                float tolerance = 0.00015f / Unit;
                topSection = BustSectionGeometry.Measure(vertices, triangles, Pivot(top), topTilt, tolerance, bounds.size.y, true);
                underSection = BustSectionGeometry.Measure(vertices, triangles, Pivot(under), underTilt, tolerance, bounds.size.y, false);
            }
            Repaint();
            SceneView.RepaintAll();
        }

        private Vector3 Pivot(float height) => new Vector3(anchor.x, height, anchor.z);

        private void DrawScene(SceneView view)
        {
            if (!showGuides || !avatar || !body) return;
            EnsureSample();
            if (vertices == null || error != null) return;
            DrawScenePlane("TOP", TopColor, ref top, ref topTilt, topSection, -1f);
            DrawScenePlane("UNDER", UnderColor, ref under, ref underTilt, underSection, 1f);
            Vector3 origin = new Vector3(anchor.x + guideWidth, foot, anchor.z);
            using (new Handles.DrawingScope(new Color(0.85f, 0.85f, 0.85f)))
            {
                Vector3 end = new Vector3(origin.x, head, origin.z);
                Handles.DrawDottedLine(ToWorld(origin), ToWorld(end), 4f);
                DrawHeightHandle("足底", ref foot, origin);
                DrawHeightHandle("头顶", ref head, end);
            }
        }

        private void DrawScenePlane(string label, Color color, ref float height, ref float tilt, Section section, float side)
        {
            Vector3 pivot = Pivot(height);
            using (new Handles.DrawingScope(color))
            {
                if (section.IsValid && Event.current.type == EventType.Repaint)
                {
                    var line = new Vector3[section.Loop.Length];
                    for (int i = 0; i < line.Length; i++) line[i] = ToWorld(section.Loop[i]);
                    Handles.DrawAAPolyLine(3f, line);
                }
                DrawHeightHandle(label, ref height, pivot + Vector3.right * (guideWidth * side));

                Vector3 worldPivot = ToWorld(pivot + Vector3.right * (guideWidth * side * 0.55f));
                Quaternion rotation = avatar.rotation * Quaternion.AngleAxis(tilt, Vector3.right);
                EditorGUI.BeginChangeCheck();
                Quaternion next = Handles.Disc(rotation, worldPivot, avatar.right, HandleUtility.GetHandleSize(worldPivot) * 0.38f, false, 0f);
                if (!EditorGUI.EndChangeCheck()) return;
                Undo.RecordObject(this, "调整断面倾斜");
                tilt = Mathf.Clamp(Mathf.DeltaAngle(0f, (Quaternion.Inverse(avatar.rotation) * next).eulerAngles.x), -60f, 60f);
                Measure();
            }
        }

        private void DrawHeightHandle(string label, ref float height, Vector3 position)
        {
            Vector3 world = ToWorld(position);
            float size = HandleUtility.GetHandleSize(world) * 0.075f;
            Handles.Label(world + avatar.up * size, label);
            EditorGUI.BeginChangeCheck();
            Vector3 next = Handles.Slider(world, avatar.up, size, Handles.DotHandleCap, 0f);
            if (!EditorGUI.EndChangeCheck()) return;
            Undo.RecordObject(this, "移动测量手柄");
            height = ToFrame(next).y;
            Measure();
        }
    }
}
