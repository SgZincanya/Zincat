#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Zincat.AvatarSplitTest
{
    internal enum CauVerificationStatus { None, Waiting, Checking, Passed, Uncertain, Failed }
    [Serializable] internal sealed class CauModelBaseline
    {
        public string settingGuid, sourceKey, groupId, avatarId, userId, assetUrl, capturedAt;
        public int version;
        public bool existing;
    }
    [InitializeOnLoad] internal static class CauUploadVerification
    {
        // No network traffic on initialization: reads start only after explicit handoff
        // or a successful model event, and rechecks never write remote data.
        static readonly Dictionary<string, CauModelBaseline> prepared = new Dictionary<string, CauModelBaseline>();
        static bool running, stopping;
        static CancellationTokenSource cancellation;
        static CauUploadVerification()
        {
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingEditMode) cancellation?.Cancel(); };
        }
        static void Stop() { stopping = true; cancellation?.Cancel(); }
        static string Key(string source, string group) => source + ":" + group;
        internal static async Task CaptureBaselines(CauQueueItem[] items, ICauExtraBackend api, CancellationToken token)
        {
            string user = api.UserId;
            if (string.IsNullOrEmpty(user)) throw new InvalidOperationException("请先登录 VRChat SDK。");
            var captured = new Dictionary<string, CauModelBaseline>();
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var baseline = new CauModelBaseline { sourceKey = item.sourceKey, groupId = item.groupId, avatarId = item.blueprintId,
                    userId = user, capturedAt = DateTime.UtcNow.ToString("o"), existing = !string.IsNullOrEmpty(item.blueprintId) };
                if (baseline.existing)
                {
                    var remote = await api.Read(item.blueprintId, token);
                    if (remote == null || remote.id != item.blueprintId || remote.authorId != user)
                        throw new InvalidOperationException(item.name + "：蓝图不存在或不属于当前账号，未交接。");
                    baseline.assetUrl = remote.assetUrl; baseline.version = remote.version;
                    // A reserved ID without an initial package is still allowed for model upload.
                }
                if (api.UserId != user) throw new InvalidOperationException("登录账号已变化，请重新交接。");
                captured[Key(item.sourceKey, item.groupId)] = baseline;
            }
            foreach (var pair in captured) prepared[pair.Key] = pair.Value;
        }
        internal static void Bind(CauExtraProfile profile)
        {
            profile.baseline = prepared.TryGetValue(Key(profile.sourceKey, profile.groupId), out var baseline)
                && (baseline.avatarId ?? "") == (profile.expectedId ?? "") ? baseline : null;
        }
        internal static void Started(CauExtraJob job, CauExtraProfile profile, string platform)
        {
            job.baseline = profile.baseline;
            job.uploadPlatform = platform;
            job.verification = CauVerificationStatus.Waiting;
            job.startedAt = DateTime.UtcNow.ToString("o");
            if (job.baseline != null && (job.baseline.userId != job.userId ||
                !DateTime.TryParse(job.baseline.capturedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var captured) ||
                DateTime.UtcNow - captured.ToUniversalTime() > TimeSpan.FromHours(12))) job.baseline = null;
        }
        internal static void ModelFailed(CauExtraJob job)
        { job.verification = CauVerificationStatus.Failed; job.verificationMessage = "CAU 未确认本次模型上传成功。"; }
        // Pure decision logic: an existing record/name alone never proves a new model was received.
        internal static CauVerificationStatus Evaluate(CauExtraJob job, CauExtraRemote remote, string user, out string message)
        {
            if (!job.modelSucceeded) { message = "缺少 CAU 本次成功结果。"; return CauVerificationStatus.Failed; }
            if (remote == null || remote.id != job.avatarId || string.IsNullOrEmpty(user) || user != job.userId || remote.authorId != user)
            { message = "服务器蓝图或所属账号不匹配。"; return CauVerificationStatus.Failed; }
            if ((!string.IsNullOrEmpty(job.expectedId) && job.expectedId != remote.id) ||
                (job.sourceId == remote.id && job.expectedId != remote.id))
            { message = "上传结果蓝图与交接记录不一致。"; return CauVerificationStatus.Failed; }
            if (job.uploadPlatform != "Windows") { message = "当前核验仅支持 Windows 模型包。"; return CauVerificationStatus.Uncertain; }
            if (remote.pendingUpload || string.IsNullOrEmpty(remote.assetUrl))
            { message = "服务器尚未提供已完成的 Windows 模型包。"; return CauVerificationStatus.Uncertain; }
            if (job.baseline == null)
            { message = "缺少有效的上传前版本基线；已确认模型记录存在，无法证明是本次更新。"; return CauVerificationStatus.Uncertain; }
            if (job.baseline.userId != user || (job.baseline.avatarId ?? "") != (job.expectedId ?? ""))
            { message = "上传前基线与本次账号或蓝图不一致。"; return CauVerificationStatus.Failed; }
            if (job.baseline.existing && (remote.assetUrl == job.baseline.assetUrl || remote.version < job.baseline.version))
            { message = "尚未检测到本次 Windows 模型包更新，请稍后重新核验。"; return CauVerificationStatus.Uncertain; }
            message = job.baseline.existing ? "服务器已收到新的 Windows 模型包。" : "首次 Windows 模型上传已完成。";
            return CauVerificationStatus.Passed;
        }
        static void Tick()
        {
            if (running || stopping || CauBatchQueue.Preparing || CauBridge.Busy || CauExtraUploads.Busy || !SplitIds.Editing || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!CauExtraUploads.HasJournal) return;
            var next = CauExtraUploads.State.jobs.FirstOrDefault(j => !j.metadataOnly && j.modelSucceeded &&
                (j.verification == CauVerificationStatus.Waiting || j.verification == CauVerificationStatus.Checking) &&
                j.status != CauExtraStatus.Pending && j.status != CauExtraStatus.Running);
            if (next != null) Check(next);
        }
        static async void Check(CauExtraJob job)
        {
            running = true; cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                job.verification = CauVerificationStatus.Checking; CauExtraUploads.Save();
                var api = new CauSdkExtraBackend();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (api.UserId != job.userId) throw new InvalidOperationException("请登录本次上传使用的账号再核验。");
                    var remote = await api.Read(job.avatarId, cancellation.Token);
                    job.verification = Evaluate(job, remote, api.UserId, out var result);
                    job.verificationMessage = result;
                    job.remoteVersion = remote?.version ?? 0;
                    if (job.verification != CauVerificationStatus.Uncertain || attempt == 2) break;
                    await Task.Delay(2000, cancellation.Token);
                }
                job.checkedAt = DateTime.UtcNow.ToString("o");
            }
            catch (OperationCanceledException) { job.verification = CauVerificationStatus.Uncertain; job.verificationMessage = "核验已暂停或超时，请重新核验。"; }
            catch (Exception e) { job.verification = CauVerificationStatus.Uncertain; job.verificationMessage = "服务器读取未完成：" + e.Message; }
            finally { running = false; cancellation.Dispose(); cancellation = null; if (!stopping) CauExtraUploads.Save(); }
        }
        internal static void Retry(CauExtraJob job)
        {
            if (running || CauBridge.Busy || CauExtraUploads.Busy || !SplitIds.Editing) throw new InvalidOperationException("请等待任务完成。");
            if (!job.modelSucceeded || job.metadataOnly) throw new InvalidOperationException("没有可核验的模型成功记录。");
            job.verification = CauVerificationStatus.Waiting; job.verificationMessage = ""; CauExtraUploads.Save();
        }
        internal static string Describe(CauExtraJob job)
        {
            switch (job.verification)
            {
                case CauVerificationStatus.Passed: return "模型核验通过 · v" + job.remoteVersion + " · " + job.verificationMessage;
                case CauVerificationStatus.Failed: return "模型上传未通过 · " + job.verificationMessage;
                case CauVerificationStatus.Uncertain: return "待核验 · " + job.verificationMessage;
                case CauVerificationStatus.Checking: return "正在回读服务器模型版本…";
                case CauVerificationStatus.Waiting: return job.modelSucceeded ? "等待服务器核验" : "等待本次模型上传结果";
                default: return "历史记录未核验";
            }
        }
    }
}
#endif
