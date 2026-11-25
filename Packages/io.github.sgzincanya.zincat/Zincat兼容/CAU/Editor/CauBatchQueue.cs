#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using VRC.SDKBase.Editor.Api;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.Core;

namespace Zincat.AvatarSplitTest
{
    [Serializable] internal sealed class CauQueueItem
    {
        public string sourceKey, sourceId, sourceScene, sourceName, groupId, name, uploadName, blueprintId, generatedId, coverHash, coverGuid;
        public int revision;
        public bool selected = true;
        public static CauQueueItem Capture(AvatarSplitPlan plan, SplitGroup group)
        {
            var source = SplitIds.Resolve(plan.sourceId) as GameObject;
            return new CauQueueItem { sourceKey = SplitConfigStore.Key(plan), sourceId = plan.sourceId, sourceScene = plan.sourceScene,
                sourceName = source ? source.name : plan.name, groupId = group.id, name = group.name, uploadName = group.UploadName,
                blueprintId = group.blueprintId ?? "", generatedId = group.generatedAvatarId, revision = group.revision,
                coverHash = group.photoCoverHash ?? "", coverGuid = group.photoCoverGuid ?? "" };
        }
        public AvatarSplitPlan Plan()
        {
            return SplitConfigStore.LoadedPlans().FirstOrDefault(p => p.sourceId == sourceId && SplitConfigStore.Key(p) == sourceKey);
        }
        public SplitGroup Group(AvatarSplitPlan plan) => plan ? plan.groups.FirstOrDefault(g => g.id == groupId) : null;
        internal bool SameVersion(CauQueueItem other, string completedId = null) => other != null &&
            sourceKey == other.sourceKey && sourceId == other.sourceId && groupId == other.groupId &&
            name == other.name && generatedId == other.generatedId && revision == other.revision &&
            (blueprintId == other.blueprintId || (string.IsNullOrEmpty(blueprintId) && !string.IsNullOrEmpty(completedId) && other.blueprintId == completedId));
        string CompletedBlueprint()
        {
            var job = CauExtraUploads.State.jobs.LastOrDefault(j => !j.metadataOnly && j.modelSucceeded &&
                j.sourceKey == sourceKey && j.groupId == groupId);
            return job?.queueItem != null && job.queueItem.SameVersion(this, job.avatarId) ? job.avatarId : null;
        }

        bool MatchesBlueprint(string currentId, string returnedId) =>
            (currentId ?? "") == (blueprintId ?? "") ||
            (string.IsNullOrEmpty(blueprintId) && !string.IsNullOrEmpty(returnedId) && currentId == returnedId);

        internal CauQueueItem RefreshLocal()
        {
            var plan = Plan();
            var group = Group(plan);
            if (group == null || group.generatedRevision != group.revision) return this;
            if (!MatchesBlueprint(group.blueprintId, string.IsNullOrEmpty(blueprintId) ? CompletedBlueprint() : null)) return this;
            var avatar = SplitIds.Avatar(group);
            if (!avatar || avatar.name != group.name.Trim() ||
                (avatar.GetComponent<PipelineManager>()?.blueprintId ?? "") != (group.blueprintId ?? "")) return this;
            if (generatedId == group.generatedAvatarId && revision == group.revision &&
                name == group.name && uploadName == group.UploadName && (blueprintId ?? "") == (group.blueprintId ?? "") &&
                (coverHash ?? "") == (group.photoCoverHash ?? "") && (coverGuid ?? "") == (group.photoCoverGuid ?? "")) return this;
            var current = Capture(plan, group);
            current.selected = selected;
            return current;
        }

        public void Validate(string returnedId = null)
        {
            if (string.IsNullOrEmpty(blueprintId) && string.IsNullOrEmpty(returnedId)) returnedId = CompletedBlueprint();
            var plan = Plan();
            var group = Group(plan);
            if (group == null) throw new InvalidOperationException(name + "：请打开源场景，确认原 Avatar 配置仍存在。");
            if (!MatchesBlueprint(group.blueprintId, returnedId))
                throw new InvalidOperationException(name + "：目标蓝图已变化，请在 ⋯ 中刷新本地设置并确认。");
            var avatar = SplitIds.Avatar(group);
            if (!avatar || group.generatedRevision != group.revision || avatar.name != group.name.Trim())
                throw new InvalidOperationException(name + "：请在模型拆分中重新生成。");
            if (group.name != name || group.generatedAvatarId != generatedId || group.revision != revision)
                throw new InvalidOperationException(name + "：拆分结果已变化，请刷新清单后再继续。");
            string expectedId = string.IsNullOrEmpty(blueprintId) && !string.IsNullOrEmpty(returnedId) ? returnedId : blueprintId;
            if ((avatar.GetComponent<PipelineManager>()?.blueprintId ?? "") != expectedId)
                throw new InvalidOperationException(name + "：模型蓝图与清单不一致。");
            if (!string.IsNullOrEmpty(blueprintId) && !CauExtraUploads.HasBlueprint(group))
                throw new InvalidOperationException(name + "：蓝图号格式无效。");
        }
    }
    [Serializable] internal sealed class CauQueueState
    {
        public List<CauServerRecord> servers = new List<CauServerRecord>();
    }

    internal static class CauBatchQueue
    {
        internal const string PathName = "ProjectSettings/ZincatUploadQueue.json";
        static CauQueueState state;
        public static bool Preparing { get; private set; }
        public static string Message { get; private set; }
        public static int Version { get; private set; }
        // Keep pending items for this Unity session, including across window close and reopen.
        public static List<CauQueueItem> Items { get; } = new List<CauQueueItem>();
        internal static bool HandoffLocked => Preparing || CauBridge.Busy || CauExtraUploads.Busy ||
            !SplitIds.Editing || EditorApplication.isCompiling;
        public static bool Locked => HandoffLocked || CauServerQueries.Busy;
        internal static List<CauServerRecord> Servers
        {
            get
            {
                if (state == null)
                {
                    state = File.Exists(PathName) ? JsonUtility.FromJson<CauQueueState>(File.ReadAllText(PathName)) : new CauQueueState();
                    if (state == null) throw new InvalidOperationException("无法读取上传清单的线上信息缓存。");
                }
                return state.servers ?? (state.servers = new List<CauServerRecord>());
            }
        }

        internal static void Clear(string message = null)
        {
            Items.Clear();
            SetMessage(message);
        }

        public static void SetMessage(string message) { Message = message; Version++; }
        internal static void ListChanged() => Version++;

        public static void Save()
        {
            _ = Servers;
            var temp = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp"; Directory.CreateDirectory("ProjectSettings");
            try
            {
                File.WriteAllText(temp, JsonUtility.ToJson(state, true));
                CauExtraUploads.ReplaceLocalFile(temp, PathName); Version++;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static CauQueueItem[] Capture(AvatarSplitPlan plan)
        {
            if (!plan) throw new InvalidOperationException("请先选择源 Avatar。");
            var entries = CauBridge.SelectedUploads(plan).Select(g => CauQueueItem.Capture(plan, g)).ToArray();
            Validate(entries); return entries;
        }
        public static void Validate(CauQueueItem[] items)
        {
            if (items.Length == 0) throw new InvalidOperationException("请至少选择一个版本。");
            foreach (var item in items) item.Validate();
            if (items.GroupBy(i => i.sourceKey + ":" + i.groupId).Any(g => g.Count() > 1) ||
                items.GroupBy(i => i.generatedId).Any(g => g.Count() > 1)) throw new InvalidOperationException("上传清单包含重复模型。");
            if (items.Where(i => !string.IsNullOrEmpty(i.blueprintId)).GroupBy(i => i.blueprintId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new InvalidOperationException("多个版本使用同一蓝图，已阻止交接，避免相互覆盖。");
        }
        public static void Add(AvatarSplitPlan plan)
        {
            if (Locked) throw new InvalidOperationException("请等待任务结束，并退出运行模式。");
            SplitIds.Flush(); var entries = Capture(plan);
            foreach (var item in entries)
            {
                int index = Items.FindIndex(i => i.sourceKey == item.sourceKey && i.groupId == item.groupId);
                if (index < 0) Items.Add(item); else { item.selected = Items[index].selected; Items[index] = item; }
            }
            SetMessage("已加入 " + entries.Length + " 个版本。可切换到其他 Avatar 继续加入。");
        }
        internal static void RefreshLocalItems()
        {
            if (Items.Count == 0 || HandoffLocked) return;
            bool changed = false;
            for (int i = 0; i < Items.Count; i++)
            {
                var current = Items[i].RefreshLocal();
                if (ReferenceEquals(current, Items[i])) continue;
                Items[i] = current;
                changed = true;
            }
            if (changed) SetMessage("已同步最新拆分结果。");
        }

        // Only a user action calls Send. No build, upload, or metadata write is invoked here.
        public static void Send(CauQueueItem[] items)
        {
            if (HandoffLocked) { SetMessage("请等待当前上传或更新结束。"); return; }
            Preparing = true; Version++;
            try
            {
                SplitIds.Flush();
                items = items.Select(item => item.RefreshLocal()).ToArray();
                Validate(items);
                if (string.IsNullOrEmpty(APIUser.CurrentUser?.id)) throw new InvalidOperationException("请先登录 VRChat SDK。");
                foreach (var current in items) { int index = Items.FindIndex(i => i.sourceKey == current.sourceKey && i.groupId == current.groupId); if (index >= 0) Items[index] = current; }
                ListChanged();
                Message = CauBridge.PrepareQueueCore(items)
                    ? "准备完成。请检查 CAU 队列，再亲自点击开始上传。"
                    : "已取消交接，上传清单已保留。";
            }
            catch (Exception e) { Message = e.Message; Debug.LogWarning("[上传清单] " + e.Message); }
            finally { Preparing = false; Version++; }
        }
    }

    public sealed class CauBatchQueueWindow : EditorWindow
    {
        sealed class Change
        {
            public CauQueueItem Item;
            public AvatarSplitPlan Plan;
            public SplitGroup Group;
            public CauServerRecord Online;
            public Texture2D Cover;
            public string CoverIssue;
            public bool NameChanged, CoverChanged, CoverUnknown;
            public bool HasChanges => NameChanged || CoverChanged;
        }

        readonly Dictionary<string, Texture2D> thumbnails = new Dictionary<string, Texture2D>();
        readonly Dictionary<string, Task<Texture2D>> imageLoads = new Dictionary<string, Task<Texture2D>>();
        readonly Dictionary<string, string> coverHashes = new Dictionary<string, string>();
        readonly SemaphoreSlim imageSlots = new SemaphoreSlim(2);
        CancellationTokenSource imageRequests;
        ScrollView rows;
        VisualElement review;
        Label message, count;
        Button send, update, clear;
        string lastUser;
        int queueVersion, extraVersion, serverVersion;

        [MenuItem("Zincat/衣服菜单/上传清单", false, 1022)]
        public static void Open()
        {
            var window = GetWindow<CauBatchQueueWindow>("上传清单");
            window.minSize = new Vector2(520, 430);
            window.Show();
        }

        void OnDisable() => ResetImages();
        void OnFocus() { if (rows != null && review.childCount == 0) Draw(); }

        void ResetImages()
        {
            imageRequests?.Cancel();
            imageRequests?.Dispose();
            imageRequests = null;
            foreach (var texture in thumbnails.Values)
                if (texture) DestroyImmediate(texture);
            thumbnails.Clear();
            imageLoads.Clear();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.backgroundColor = SplitUi.Background;
            root.style.paddingLeft = root.style.paddingRight = 16;
            root.style.paddingTop = root.style.paddingBottom = 14;
            var heading = SplitUi.Row();
            var title = SplitUi.Label("上传清单", 22);
            title.style.flexGrow = 1;
            heading.Add(title);
            count = SplitUi.Label("", 12, true);
            heading.Add(count);
            root.Add(heading);

            var tools = SplitUi.Row();
            tools.style.flexWrap = Wrap.Wrap;
            tools.Add(SplitUi.Button("全选", () => Act(() => Select(_ => true))));
            clear = SplitUi.Button("清空清单", () => Act(() =>
            {
                review.Clear();
                rows.style.display = DisplayStyle.Flex;
                CauBatchQueue.Clear("已清空清单。");
            }));
            clear.tooltip = "移除清单中所有版本。";
            tools.Add(clear);
            tools.Add(SplitUi.Button("刷新线上信息", () => Act(() => RefreshOnline(Selected()))));
            tools.Add(SplitUi.Button("更多…", More));
            root.Add(tools);

            rows = new ScrollView(ScrollViewMode.Vertical);
            rows.style.flexGrow = 1;
            rows.style.marginTop = 8;
            root.Add(rows);
            review = new VisualElement();
            root.Add(review);
            message = SplitUi.Label("", 12, true);
            message.style.marginTop = 6;
            root.Add(message);
            var footer = SplitUi.Row();
            footer.style.flexWrap = Wrap.Wrap;
            update = SplitUi.Button("更新所选名称／封面…", () => Act(() => Review(Selected())));
            send = SplitUi.Button("打开批量上传…", () => CauBatchQueue.Send(Selected()), true);
            footer.Add(update);
            footer.Add(send);
            root.Add(footer);
            Draw();
            ApplyQueueStyles(root);
            root.schedule.Execute(() =>
            {
                if (lastUser != APIUser.CurrentUser?.id) { review.Clear(); Draw(); }
                else if (queueVersion != CauBatchQueue.Version || extraVersion != CauExtraUploads.Version ||
                         serverVersion != CauServerQueries.Version) Draw();
                RefreshButtons();
            }).Every(750);
        }

        static CauQueueItem[] Selected() => CauBatchQueue.Items.Where(item => item.selected).ToArray();
        static void Select(Func<CauQueueItem, bool> predicate)
        {
            foreach (var item in CauBatchQueue.Items) item.selected = predicate(item);
            CauBatchQueue.ListChanged();
        }

        void RefreshButtons()
        {
            bool hasSelection = review.childCount == 0 && Selected().Length > 0;
            SetActionEnabled(send, !CauBatchQueue.HandoffLocked && hasSelection);
            SetActionEnabled(update, !CauBatchQueue.Locked && hasSelection);
            SetActionEnabled(clear, !CauBatchQueue.Locked && CauBatchQueue.Items.Count > 0);
        }

        static void SetActionEnabled(Button button, bool enabled)
        {
            button.SetEnabled(enabled);
            // The shared action style sets opacity only when it is applied.
            button.style.opacity = enabled ? 1f : .45f;
        }

        void Act(Action action)
        {
            if (CauBatchQueue.Locked)
            {
                message.text = "请等当前操作结束。";
                return;
            }
            try { action(); Draw(); }
            catch (Exception error) { message.text = error.Message; }
        }

        static CauExtraJob Latest(CauQueueItem item, bool metadata)
        {
            var job = CauExtraUploads.State.jobs.LastOrDefault(j => j.metadataOnly == metadata &&
                j.sourceKey == item.sourceKey && j.groupId == item.groupId &&
                (!metadata || j.userId == APIUser.CurrentUser?.id && j.avatarId == CauServerQueries.AvatarId(item)));
            return job != null && (job.queueItem == null ||
                job.queueItem.SameVersion(item, job.modelSucceeded ? job.avatarId : null)) ? job : null;
        }

        // A later save can resolve an old post-upload error even when the SDK
        // has not restored CurrentUser. Match the account recorded on both jobs.
        static bool HasUnresolvedMetadataError(CauExtraJob job, List<CauExtraJob> history)
        {
            if (job?.status != CauExtraStatus.Failed) return false;
            bool needsName = !job.preserveName && !job.nameApplied;
            bool needsCover = !string.IsNullOrEmpty(job.imagePath) && !job.imageApplied;
            if (string.IsNullOrEmpty(job.userId) || string.IsNullOrEmpty(job.avatarId))
                return needsName || needsCover;
            int index = history.IndexOf(job);
            if (index < 0) return needsName || needsCover;
            for (int i = index + 1; i < history.Count && (needsName || needsCover); i++)
            {
                var later = history[i];
                if (later.userId != job.userId || later.sourceKey != job.sourceKey ||
                    later.groupId != job.groupId ||
                    !string.Equals(later.avatarId, job.avatarId, StringComparison.OrdinalIgnoreCase)) continue;
                if (later.nameApplied || (later.status == CauExtraStatus.Done && !later.preserveName))
                    needsName = false;
                if (later.imageApplied || (later.status == CauExtraStatus.Done && !string.IsNullOrEmpty(later.imagePath)))
                    needsCover = false;
            }
            return needsName || needsCover;
        }

        static bool Failed(CauExtraJob job) => job?.status == CauExtraStatus.Failed && !job.modelSucceeded;
        static string ModelStatus(CauExtraJob job, string id)
        {
            if (job?.modelSucceeded == true) return "上传完成";
            if (Failed(job)) return "上传失败";
            if (job?.status == CauExtraStatus.AwaitingModel)
                return CauBridge.Busy ? "上传中" : "上传结果待确认";
            return string.IsNullOrEmpty(id) ? "未上传" : "暂无本次上传记录";
        }

        static string Explain(string error)
        {
            var value = (error ?? "").ToLowerInvariant();
            if (value.Contains("账号") || value.Contains("登录"))
                return "更新时 SDK 的登录状态或账号未通过校验。";
            if (value.Contains("nameresolutionfailure") || value.Contains("dns") || value.Contains("域名解析"))
                return "域名解析失败。检查网络或代理后重试。";
            if (value.Contains("timed out") || value.Contains("timeout") || value.Contains("超时"))
                return "请求超时。可以先刷新线上信息，再决定是否重试。";
            if (value.Contains("http 401") || value.Contains("http 403") || value.Contains("unauthorized") || value.Contains("forbidden"))
                return "服务器拒绝了请求。检查 SDK 登录和模型权限。";
            if (value.Contains("file was already uploaded"))
                return "服务器报告已有相同文件。可刷新线上信息，确认当前模型版本。";
            if (value.Contains("validation") || value.Contains("build failed") || value.Contains("构建失败"))
                return "模型检查或构建未通过。查看报错中的组件或资源。";
            if (value.Contains("sending the request") || value.Contains("connection") || value.Contains("网络"))
                return "请求连接失败。检查网络后重试。";
            if (value.Contains("蓝图") || value.Contains("配置发生变化"))
                return "模型和清单记录不一致。检查拆分结果后刷新本地设置。";
            return "暂时无法判断原因。可复制报错查看完整信息。";
        }

        Change ReadChange(CauQueueItem item)
        {
            var plan = item.Plan();
            var group = item.Group(plan);
            var online = CauServerQueries.Cached(CauServerQueries.AvatarId(item), APIUser.CurrentUser?.id);
            var change = new Change { Item = item, Plan = plan, Group = group, Online = online };
            if (group == null || online == null || (group.blueprintId ?? "") != (item.blueprintId ?? "")) return change;
            change.NameChanged = online.name != null && group.UploadName.Trim() != online.name;
            if (string.IsNullOrEmpty(group.photoCoverHash)) return change;
            try { change.Cover = SplitPhoto.UploadCover(plan, group); }
            catch (InvalidOperationException error)
            {
                change.CoverIssue = error.Message;
                return change;
            }
            catch (IOException error)
            {
                change.CoverIssue = "封面文件读取失败：" + error.Message;
                return change;
            }
            if (!change.Cover) return change;
            if (string.IsNullOrEmpty(online.imageMd5))
            {
                change.CoverChanged = online.imageChecked && string.IsNullOrEmpty(online.imageUrl);
                change.CoverUnknown = !change.CoverChanged;
                return change;
            }
            if (!coverHashes.TryGetValue(group.photoCoverHash, out var hash))
            {
                string prepared = null;
                try
                {
                    prepared = CauCoverPreparation.Prepare(AssetDatabase.GetAssetPath(change.Cover));
                    hash = CauCoverPreparation.FileMd5(prepared);
                }
                catch (Exception) { hash = null; }
                finally { if (prepared != null && File.Exists(prepared)) File.Delete(prepared); }
                coverHashes[group.photoCoverHash] = hash;
            }
            change.CoverUnknown = hash == null;
            change.CoverChanged = hash != null && !string.Equals(hash, online.imageMd5, StringComparison.Ordinal);
            return change;
        }

        static bool TrustedImage(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            return uri.Host == "vrchat.com" || uri.Host.EndsWith(".vrchat.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.EndsWith(".vrchat.cloud", StringComparison.OrdinalIgnoreCase);
        }

        VisualElement Frame(Image image, string placeholder)
        {
            var frame = new VisualElement();
            frame.style.width = 96;
            frame.style.height = 72;
            frame.style.flexShrink = 0;
            frame.style.backgroundColor = SplitUi.Background;
            frame.style.justifyContent = Justify.Center;
            frame.style.alignItems = Align.Center;
            image.scaleMode = ScaleMode.ScaleToFit;
            image.style.width = new Length(100, LengthUnit.Percent);
            image.style.height = new Length(100, LengthUnit.Percent);
            frame.Add(image);
            if (!string.IsNullOrEmpty(placeholder))
            {
                var label = SplitUi.Label(placeholder, 11, true);
                label.pickingMode = PickingMode.Ignore;
                label.style.position = Position.Absolute;
                frame.Add(label);
            }
            return frame;
        }

        void RefreshOnline(CauQueueItem[] items)
        {
            if (items.Length == 0)
            {
                CauBatchQueue.SetMessage("请先选择要刷新的版本。");
                return;
            }
            review.Clear();
            rows.style.display = DisplayStyle.Flex;
            ResetImages();
            coverHashes.Clear();
            CauServerQueries.Query(items);
        }

        VisualElement OnlineCover(CauServerRecord record)
        {
            var image = new Image();
            if (record == null) return Frame(image, "尚未读取");
            string primary = record.thumbnailUrl;
            string fallback = record.imageUrl;
            if (!TrustedImage(primary)) primary = fallback;
            if (!TrustedImage(primary)) return Frame(image, "封面不可用");
            if (CauServerQueries.Busy) return Frame(image, "刷新中…");
            if (thumbnails.TryGetValue(primary, out var cached))
            {
                image.image = cached;
                return Frame(image, null);
            }
            var frame = Frame(image, "读取封面…");
            LoadCover(primary, fallback, image, frame.Q<Label>(), imageRequests.Token);
            return frame;
        }

        async void LoadCover(string primary, string fallback, Image image, Label label, CancellationToken token)
        {
            try
            {
                if (!imageLoads.TryGetValue(primary, out var load))
                {
                    load = DownloadCover(primary, fallback, token);
                    imageLoads[primary] = load;
                }
                var texture = await load;
                if (token.IsCancellationRequested) return;
                image.image = texture;
                label.text = texture ? "" : "封面未加载";
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested) label.text = "封面未加载";
            }
        }

        async Task<Texture2D> DownloadCover(string primary, string fallback, CancellationToken token)
        {
            await imageSlots.WaitAsync(token);
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    var texture = await TryDownloadCover(primary, timeout.Token);
                    if (!texture && !timeout.IsCancellationRequested && fallback != primary && TrustedImage(fallback))
                        texture = await TryDownloadCover(fallback, timeout.Token);
                    if (token.IsCancellationRequested)
                    {
                        if (texture) DestroyImmediate(texture);
                        return null;
                    }
                    if (texture) thumbnails[primary] = texture;
                    return texture;
                }
            }
            finally { imageSlots.Release(); }
        }

        static async Task<Texture2D> TryDownloadCover(string url, CancellationToken token)
        {
            try { return await VRCApi.GetImage(url, forceRefresh: true, cancellationToken: token); }
            catch (Exception) { return null; }
        }

        void More()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("选择上传失败项"), false, () => Act(() => Select(i => Failed(Latest(i, false)))));
            menu.AddItem(new GUIContent("取消全选"), false, () => Act(() => Select(_ => false)));
            menu.ShowAsContext();
        }

        void ItemMenu(CauQueueItem item, Change change, CauExtraJob job)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("刷新线上信息"), false, () => Act(() => RefreshOnline(new[] { item })));
            if (change.Group != null)
            {
                menu.AddItem(new GUIContent("编辑本地名称"), false, () => Act(() => EditName(item)));
                menu.AddItem(new GUIContent("拍摄封面…"), false, () => Act(() =>
                {
                    var source = SplitIds.Resolve(change.Plan.sourceId) as GameObject;
                    if (source) SplitPhotoWindow.Open(source, change.Group.id);
                }));
                menu.AddItem(new GUIContent("定位模型"), false, () => Act(() =>
                {
                    Selection.activeGameObject = SplitIds.Avatar(change.Group);
                    EditorGUIUtility.PingObject(Selection.activeObject);
                }));
                menu.AddItem(new GUIContent("刷新本地设置"), false, () => Act(() =>
                {
                    var updated = CauQueueItem.Capture(change.Plan, change.Group);
                    updated.selected = item.selected;
                    updated.Validate();
                    if ((item.blueprintId ?? "") != (updated.blueprintId ?? "") &&
                        !EditorUtility.DisplayDialog("确认更换目标", "清单中的目标蓝图已变化。\n\n原目标：" +
                            (string.IsNullOrEmpty(item.blueprintId) ? "新建 Avatar" : item.blueprintId) +
                            "\n新目标：" + (string.IsNullOrEmpty(updated.blueprintId) ? "新建 Avatar" : updated.blueprintId),
                            "使用新目标", "取消")) return;
                    CauBatchQueue.Items[CauBatchQueue.Items.IndexOf(item)] = updated;
                    CauBatchQueue.ListChanged();
                }));
            }
            else if (!string.IsNullOrEmpty(item.sourceScene))
                menu.AddItem(new GUIContent("打开源场景"), false, () => Act(() =>
                    EditorSceneManager.OpenScene(item.sourceScene, OpenSceneMode.Additive)));
            menu.AddItem(new GUIContent("上传详情"), false, () => EditorUtility.DisplayDialog("上传详情",
                "模型 ID：" + (CauServerQueries.AvatarId(item) ?? "—") + "\n" +
                (job != null ? CauUploadVerification.Describe(job) + "\n" : "") +
                (change.Online == null ? "" : "模型文件 v" + change.Online.assetVersion + "\n线上信息读取于 " +
                 CauServerQueries.LocalTime(change.Online.queriedAt) + "\n" + change.Online.error), "关闭"));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("移除"), false, () => Act(() => { CauBatchQueue.Items.Remove(item); CauBatchQueue.ListChanged(); }));
            menu.ShowAsContext();
        }

        void ShowReview(VisualElement body)
        {
            review.Clear();
            review.Add(body);
            rows.style.display = DisplayStyle.None;
            SplitUi.Apply(review);
        }

        void CloseReview()
        {
            review.Clear();
            rows.style.display = DisplayStyle.Flex;
            Draw();
        }

        void EditName(CauQueueItem item)
        {
            var change = ReadChange(item);
            var body = new VisualElement();
            SplitUi.Box(body, 12);
            var field = new TextField("本地新名称") { value = change.Group.UploadName };
            body.Add(field);
            var actions = SplitUi.Row();
            actions.Add(SplitUi.Button("取消", CloseReview));
            actions.Add(SplitUi.Button("保存本地名称", () => Act(() =>
            {
                var plan = item.Plan();
                var group = item.Group(plan);
                if (group == null) throw new InvalidOperationException("请先打开源场景。");
                Undo.RecordObject(plan, "修改 VRC 模型名称");
                group.uploadName = field.value.Trim();
                SplitIds.SaveLater(plan);
                CloseReview();
            }), true));
            body.Add(actions);
            ShowReview(body);
            field.Focus();
        }

        void Review(IEnumerable<CauQueueItem> items)
        {
            var changes = items.Select(ReadChange).Where(change => change.HasChanges).ToArray();
            if (changes.Length == 0)
            {
                CauBatchQueue.SetMessage("没有已确认的差异。可先刷新线上信息。");
                return;
            }
            var body = new VisualElement();
            SplitUi.Box(body, 12);
            body.Add(SplitUi.Label("查看更改", 18));
            var list = new ScrollView(ScrollViewMode.Vertical);
            list.style.maxHeight = 300;
            body.Add(list);
            var choices = new List<(Change change, Toggle name, Toggle cover)>();
            foreach (var change in changes)
            {
                list.Add(SplitUi.Label(change.Item.name, 14));
                Toggle name = null, cover = null;
                if (change.NameChanged)
                {
                    name = new Toggle("更新名称") { value = true };
                    list.Add(name);
                    list.Add(SplitUi.Label(change.Online.name + " → " + change.Group.UploadName, 12));
                }
                if (change.CoverChanged)
                {
                    cover = new Toggle("更新封面") { value = false };
                    list.Add(cover);
                    var covers = SplitUi.Row();
                    covers.Add(OnlineCover(change.Online));
                    covers.Add(SplitUi.Label("  →  "));
                    covers.Add(Frame(new Image { image = change.Cover }, null));
                    list.Add(covers);
                }
                choices.Add((change, name, cover));
            }
            var actions = SplitUi.Row();
            actions.Add(SplitUi.Button("取消", CloseReview));
            var confirm = SplitUi.Button("保存到 VRChat", () => Act(() =>
            {
                SplitIds.Flush();
                var jobs = new List<CauExtraJob>();
                foreach (var choice in choices)
                {
                    bool name = choice.name?.value == true, cover = choice.cover?.value == true;
                    if (!name && !cover) continue;
                    var now = ReadChange(choice.change.Item);
                    if (now.Online != choice.change.Online ||
                        (name && (!now.NameChanged || now.Group.UploadName != (string)choice.name.userData)) ||
                        (cover && (!now.CoverChanged || now.Group.photoCoverHash != (string)choice.cover.userData)))
                        throw new InvalidOperationException("名称、封面或线上记录已变化，请重新查看更改。");
                    jobs.AddRange(CauExtraUploads.BuildDirect(now.Plan, new[] { now.Group }, name, cover));
                }
                if (jobs.Count == 0) return;
                CauExtraUploads.QueueReviewed(jobs);
                CauBatchQueue.SetMessage("已提交 " + jobs.Count + " 个版本的名称或封面更新。");
                CloseReview();
            }), true);
            void UpdateConfirm()
            {
                int names = choices.Count(c => c.name?.value == true), covers = choices.Count(c => c.cover?.value == true);
                confirm.text = $"保存到 VRChat（{names} 个名称，{covers} 张封面）";
                SetActionEnabled(confirm, names + covers > 0);
            }
            foreach (var choice in choices)
            {
                if (choice.name != null)
                {
                    choice.name.userData = choice.change.Group.UploadName;
                    choice.name.RegisterValueChangedCallback(_ => UpdateConfirm());
                }
                if (choice.cover != null)
                {
                    choice.cover.userData = choice.change.Group.photoCoverHash;
                    choice.cover.RegisterValueChangedCallback(_ => UpdateConfirm());
                }
            }
            UpdateConfirm();
            actions.Add(confirm);
            body.Add(actions);
            ShowReview(body);
        }

        Button Difference(string field, Action action)
        {
            var button = SplitUi.Button("!", action);
            button.tooltip = field + "有变化，查看更改";
            button.AddToClassList("zincat-queue-difference");
            return button;
        }

        static void ApplyQueueStyles(VisualElement root)
        {
            SplitUi.Apply(root);
            root.Query<Button>(className: "zincat-queue-difference").ForEach(button =>
            {
                button.style.color = EditorGUIUtility.isProSkin ? new Color(1, .43f, .40f) : new Color(.6f, .08f, .06f);
                button.style.unityFontStyleAndWeight = FontStyle.Bold;
            });
        }

        void AddError(VisualElement body, string title, CauExtraJob job, Action retry = null)
        {
            body.Add(SplitUi.Label(title + " · " + Explain(job.error), 12));
            var buttons = SplitUi.Row();
            buttons.Add(SplitUi.Button("查看报错…", () =>
            {
                string error = job.error ?? "CAU 未提供详细报错。";
                if (EditorUtility.DisplayDialog(title, error.Length > 1200 ? error.Substring(0, 1200) + "…" : error,
                    "复制完整报错", "关闭")) EditorGUIUtility.systemCopyBuffer = error;
            }));
            if (retry != null) buttons.Add(SplitUi.Button("重试更新", retry));
            body.Add(buttons);
        }

        void Draw()
        {
            if (review.childCount == 0) CauBatchQueue.RefreshLocalItems();
            if (lastUser != APIUser.CurrentUser?.id || imageRequests == null)
            {
                ResetImages();
                imageRequests = new CancellationTokenSource();
                if (review.childCount == 0) rows.style.display = DisplayStyle.Flex;
            }
            lastUser = APIUser.CurrentUser?.id;
            queueVersion = CauBatchQueue.Version;
            extraVersion = CauExtraUploads.Version;
            serverVersion = CauServerQueries.Version;
            message.text = CauBatchQueue.Message ?? "";
            if (review.childCount > 0) { RefreshButtons(); return; }
            rows.Clear();
            try
            {
                var items = CauBatchQueue.Items;
                count.text = $"已选 {items.Count(i => i.selected)} / {items.Count}";
                foreach (var section in items.GroupBy(item => item.sourceKey))
                {
                    var label = SplitUi.Label(section.First().sourceName, 13, true);
                    label.style.marginTop = label.style.marginBottom = 6;
                    rows.Add(label);
                    foreach (var item in section) rows.Add(Card(item));
                }
                if (items.Count == 0) rows.Add(SplitUi.Label("从模型拆分窗口添加要上传的版本。", 14, true));
                RefreshButtons();
            }
            catch (Exception error) { message.text = error.Message; SetActionEnabled(send, false); SetActionEnabled(update, false); SetActionEnabled(clear, false); }
            ApplyQueueStyles(rows);
        }

        VisualElement Card(CauQueueItem item)
        {
            var change = ReadChange(item);
            var job = Latest(item, false);
            var extra = Latest(item, true);
            string id = CauServerQueries.AvatarId(item);
            var card = SplitUi.Row();
            SplitUi.Box(card, 12);
            card.style.alignItems = Align.FlexStart;
            card.style.marginBottom = 8;
            var check = new Toggle { value = item.selected, tooltip = "加入本次上传" };
            check.style.flexShrink = 0;
            check.RegisterValueChangedCallback(e =>
            {
                if (CauBatchQueue.Locked) { check.SetValueWithoutNotify(item.selected); return; }
                Act(() => { item.selected = e.newValue; CauBatchQueue.ListChanged(); });
            });
            card.Add(check);
            var cover = OnlineCover(change.Online);
            if (change.CoverChanged)
            {
                var mark = Difference("封面", () => Act(() => Review(new[] { item })));
                mark.style.position = Position.Absolute;
                mark.style.right = 0;
                mark.style.top = 0;
                cover.Add(mark);
            }
            card.Add(cover);

            var body = new VisualElement();
            body.style.flexGrow = 1;
            body.style.minWidth = 0;
            body.style.marginLeft = 12;
            var top = SplitUi.Row();
            var title = SplitUi.Label(change.Online?.name ?? (string.IsNullOrEmpty(id) ? item.name : item.name + "（线上名称尚未读取）"), 14);
            title.style.flexGrow = 1;
            title.style.minWidth = 0;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexShrink = 1;
            top.Add(title);
            if (change.NameChanged) top.Add(Difference("名称", () => Act(() => Review(new[] { item }))));
            top.Add(SplitUi.Button("⋯", () => ItemMenu(item, change, job)));
            body.Add(top);
            var time = change.Online == null ? (string.IsNullOrEmpty(id) ? "—" : "尚未读取") :
                CauServerQueries.LocalTime(change.Online.modelUpdatedAt);
            body.Add(SplitUi.Label("模型上次更新：" + time, 12, true));
            body.Add(SplitUi.Label(ModelStatus(job, id), 12));
            if (!string.IsNullOrEmpty(change.CoverIssue))
                body.Add(SplitUi.Label(change.CoverIssue, 12, true));
            else if (change.CoverUnknown) body.Add(SplitUi.Label("封面待检查", 12, true));
            try { item.Validate(); }
            catch (InvalidOperationException error) { body.Add(SplitUi.Label(error.Message, 12, true)); }
            if (Failed(job))
            {
                AddError(body, "模型上传失败", job);
                var retry = SplitUi.Button("重新打开此项上传…", () => CauBatchQueue.Send(new[] { item }));
                retry.style.alignSelf = Align.FlexStart;
                body.Add(retry);
            }
            var history = CauExtraUploads.State.jobs;
            if (HasUnresolvedMetadataError(extra, history))
                AddError(body, "名称或封面更新失败", extra, () => Act(() => CauExtraUploads.Retry(extra)));
            else if (extra?.status == CauExtraStatus.Pending || extra?.status == CauExtraStatus.Running)
                body.Add(SplitUi.Label("正在更新线上信息", 12, true));
            if (job?.modelSucceeded == true && HasUnresolvedMetadataError(job, history))
                AddError(body, "上传后的资料更新失败", job, () => Act(() => CauExtraUploads.Retry(job)));
            if (change.HasChanges)
            {
                var button = SplitUi.Button("查看更改…", () => Act(() => Review(new[] { item })));
                button.style.alignSelf = Align.FlexEnd;
                body.Add(button);
            }
            card.Add(body);
            return card;
        }
    }
}
#endif
