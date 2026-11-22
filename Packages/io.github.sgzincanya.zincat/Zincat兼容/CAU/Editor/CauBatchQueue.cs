#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
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
            name == other.name && uploadName == other.uploadName && generatedId == other.generatedId && revision == other.revision &&
            coverHash == other.coverHash && coverGuid == other.coverGuid &&
            (blueprintId == other.blueprintId || (string.IsNullOrEmpty(blueprintId) && !string.IsNullOrEmpty(completedId) && other.blueprintId == completedId));
        public void Validate(string returnedId = null)
        {
            var plan = Plan(); var group = Group(plan);
            if (group == null) throw new InvalidOperationException(name + "：请打开源场景，确认原 Avatar 配置仍存在。");
            var now = Capture(plan, group);
            if (now.name != name || now.uploadName != uploadName ||
                (now.blueprintId != blueprintId && !(string.IsNullOrEmpty(blueprintId) && now.blueprintId == returnedId)) || now.generatedId != generatedId ||
                now.revision != revision || now.coverHash != coverHash || now.coverGuid != coverGuid)
                throw new InvalidOperationException(name + "：配置已变化，请在源 Avatar 重新加入清单。");
            var avatar = SplitIds.Avatar(group);
            if (!avatar || group.generatedRevision != group.revision || avatar.name != group.name.Trim())
                throw new InvalidOperationException(name + "：请重新生成拆分结果，再加入清单。");
            string expectedId = string.IsNullOrEmpty(blueprintId) && !string.IsNullOrEmpty(returnedId) ? returnedId : blueprintId;
            if ((avatar.GetComponent<PipelineManager>()?.blueprintId ?? "") != expectedId)
                throw new InvalidOperationException(name + "：模型蓝图与清单不一致。");
            if (!string.IsNullOrEmpty(blueprintId) && !CauExtraUploads.HasBlueprint(group))
                throw new InvalidOperationException(name + "：蓝图号格式无效。");
            if (!string.IsNullOrEmpty(coverGuid) && !SplitPhoto.UploadCover(plan, group))
                throw new InvalidOperationException(name + "：上传封面已失效，请重新确认并发送。");
        }
    }
    [Serializable] internal sealed class CauQueueState { public List<CauQueueItem> items = new List<CauQueueItem>(); }
    internal static class CauBatchQueue
    {
        internal const string PathName = "ProjectSettings/ZincatUploadQueue.json";
        static CauQueueState state;
        public static bool Preparing { get; private set; }
        public static string Message { get; private set; }
        public static int Version { get; private set; }
        public static List<CauQueueItem> Items
        {
            get
            {
                if (state == null)
                {
                    state = File.Exists(PathName) ? JsonUtility.FromJson<CauQueueState>(File.ReadAllText(PathName)) : new CauQueueState();
                    if (state == null || state.items == null) throw new InvalidOperationException("上传清单文件损坏，请保留 ProjectSettings/ZincatUploadQueue.json 后检查。");
                }
                return state.items;
            }
        }
        public static bool Locked => Preparing || CauBridge.Busy || CauExtraUploads.Busy || !SplitIds.Editing || EditorApplication.isCompiling;
        public static void Save()
        {
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
            Save(); Message = "已加入 " + entries.Length + " 个版本。可切换到其他 Avatar 继续加入。";
        }
        // Only a user action calls Send. No build, upload, or metadata write is invoked here.
        public static async void Send(CauQueueItem[] items)
        {
            if (Locked) { Message = "请等待当前任务完成。"; Version++; return; }
            Preparing = true; Version++;
            try
            {
                SplitIds.Flush(); Validate(items); CauBridge.ValidateApi();
                string user = APIUser.CurrentUser?.id;
                if (string.IsNullOrEmpty(user)) throw new InvalidOperationException("请先登录 VRChat SDK。");
                Message = "正在读取服务器版本基线…";
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                    await CauUploadVerification.CaptureBaselines(items, new CauSdkExtraBackend(), timeout.Token);
                if (APIUser.CurrentUser?.id != user || !SplitIds.Editing || CauBridge.Busy || CauExtraUploads.Busy)
                    throw new InvalidOperationException("账号或编辑器状态已变化，请重新交接。");
                Validate(items); // Recheck after the asynchronous read, including source edits.
                Message = CauBridge.PrepareQueueCore(items)
                    ? "准备完成。请检查 CAU 队列，再亲自点击开始上传。"
                    : "已取消交接，上传清单已保留。";
            }
            catch (OperationCanceledException) { Message = "服务器读取超时，未交接；请稍后重试。"; }
            catch (Exception e) { Message = e.Message; Debug.LogWarning("[上传清单] " + e.Message); }
            finally { Preparing = false; Version++; }
        }
    }

    public sealed class CauBatchQueueWindow : EditorWindow
    {
        VisualElement rows; Label message; Button send; int queueVersion = -1, extraVersion = -1;
        [MenuItem("Zincat/衣服菜单/上传清单", false, 1022)]
        public static void Open() { var window = GetWindow<CauBatchQueueWindow>("上传清单"); window.minSize = new Vector2(620, 430); window.Show(); }
        public void CreateGUI()
        {
            var root = rootVisualElement; root.Clear(); root.style.backgroundColor = SplitUi.Background;
            root.style.paddingTop = root.style.paddingBottom = 16; root.style.paddingLeft = root.style.paddingRight = 16;
            root.Add(SplitUi.Label("上传清单", 22));
            root.Add(SplitUi.Label("在模型拆分中加入多个角色的版本，再统一交给 CAU。", 13, true));
            var tools = SplitUi.Row(); tools.style.flexWrap = Wrap.Wrap; tools.style.marginTop = 12;
            tools.Add(SplitUi.Button("全选", () => Act(() => { foreach (var i in CauBatchQueue.Items) i.selected = true; CauBatchQueue.Save(); })));
            tools.Add(SplitUi.Button("仅选未通过项", () => Act(() => {
                foreach (var i in CauBatchQueue.Items) { var j = Latest(i); i.selected = j == null || j.verification != CauVerificationStatus.Passed; }
                CauBatchQueue.Save(); })));
            tools.Add(SplitUi.Button("移除已通过项", () => Act(() => {
                CauBatchQueue.Items.RemoveAll(i => Latest(i)?.verification == CauVerificationStatus.Passed); CauBatchQueue.Save(); })));
            tools.Add(SplitUi.Button("重新核验全部", () => Act(() => {
                foreach (var job in CauBatchQueue.Items.Select(Latest).Where(j => j != null && j.modelSucceeded).Distinct())
                    CauUploadVerification.Retry(job);
            })));
            root.Add(tools);
            rows = new ScrollView(ScrollViewMode.Vertical); rows.style.flexGrow = 1; rows.style.marginTop = 10; root.Add(rows);
            message = SplitUi.Label("", 12, true); message.style.marginTop = 8; root.Add(message);
            send = SplitUi.Button("将所选版本交给 CAU…", () => CauBatchQueue.Send(CauBatchQueue.Items.Where(i => i.selected).ToArray()), true);
            root.Add(send); Draw(); SplitUi.Apply(root);
            root.schedule.Execute(() => { if (queueVersion != CauBatchQueue.Version || extraVersion != CauExtraUploads.Version) Draw(); send.SetEnabled(!CauBatchQueue.Locked); }).Every(750);
        }
        static CauExtraJob Latest(CauQueueItem item)
        {
            var job = CauExtraUploads.State.jobs.LastOrDefault(j => !j.metadataOnly && j.sourceKey == item.sourceKey && j.groupId == item.groupId);
            return job != null && (job.queueItem == null || job.queueItem.SameVersion(item, job.modelSucceeded ? job.avatarId : null)) ? job : null;
        }
        void Act(Action action)
        {
            if (CauBatchQueue.Locked) return;
            try { action(); Draw(); } catch (Exception e) { message.text = e.Message; }
        }
        void Draw()
        {
            queueVersion = CauBatchQueue.Version; extraVersion = CauExtraUploads.Version; rows.Clear();
            try
            {
                foreach (var item in CauBatchQueue.Items.ToArray())
                {
                    var card = new VisualElement(); SplitUi.Box(card, 10); card.style.marginBottom = 8;
                    var top = SplitUi.Row(); var check = new Toggle { value = item.selected, tooltip = "包含在本次上传队列中" };
                    check.RegisterValueChangedCallback(e => { if (CauBatchQueue.Locked) { check.SetValueWithoutNotify(item.selected); return; } item.selected = e.newValue; CauBatchQueue.Save(); }); top.Add(check);
                    var title = SplitUi.Label(item.sourceName + " / " + item.name, 14); title.style.flexGrow = 1; top.Add(title);
                    top.Add(SplitUi.Button("定位", () => Act(() => {
                        var group = item.Group(item.Plan()); if (group == null) throw new InvalidOperationException("请先打开源场景：" + item.sourceScene);
                        Selection.activeGameObject = SplitIds.Avatar(group); EditorGUIUtility.PingObject(Selection.activeObject);
                    })));
                    top.Add(SplitUi.Button("更新条目", () => Act(() => {
                        var plan = item.Plan(); var group = item.Group(plan);
                        if (group == null) throw new InvalidOperationException("请先打开源场景：" + item.sourceScene);
                        var updated = CauQueueItem.Capture(plan, group); updated.selected = item.selected; updated.Validate();
                        CauBatchQueue.Items[CauBatchQueue.Items.IndexOf(item)] = updated; CauBatchQueue.Save();
                    })));
                    top.Add(SplitUi.Button("移除", () => Act(() => { CauBatchQueue.Items.Remove(item); CauBatchQueue.Save(); }))); card.Add(top);
                    card.Add(SplitUi.Label("VRC 名称：" + item.uploadName + "\n" + (string.IsNullOrEmpty(item.blueprintId) ? "新建私有 Avatar" : item.blueprintId), 12, true));
                    try { item.Validate(); } catch (Exception e) {
                        card.Add(SplitUi.Label(e.Message, 12));
                        if (item.Plan() == null && !string.IsNullOrEmpty(item.sourceScene))
                            card.Add(SplitUi.Button("打开源场景", () => Act(() => {
                                EditorSceneManager.OpenScene(item.sourceScene, OpenSceneMode.Additive);
                            })));
                    }
                    var job = Latest(item);
                    if (job != null)
                    {
                        card.Add(SplitUi.Label(CauUploadVerification.Describe(job), 12));
                        card.Add(SplitUi.Label("名称与封面：" + CauExtraUploads.Status(job), 12, true));
                        if (job.modelSucceeded && job.verification != CauVerificationStatus.Checking)
                            card.Add(SplitUi.Button("重新核验（不上传）", () => Act(() => CauUploadVerification.Retry(job))));
                    }
                    rows.Add(card);
                }
                if (CauBatchQueue.Items.Count == 0) rows.Add(SplitUi.Label("清单为空。在模型拆分窗口点击“加入上传清单”。", 14, true));
                message.text = CauBatchQueue.Message ?? "清单随项目保存；交接不会开始上传。";
            }
            catch (Exception e) { message.text = e.Message; }
            SplitUi.Apply(rows);
        }
    }
}
#endif
