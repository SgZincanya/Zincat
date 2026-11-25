#if UNITY_EDITOR
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Zincat.AvatarSplitTest
{
    internal interface ICauServerDetailsBackend : ICauExtraBackend
    {
        Task<CauExtraRemote> Details(string id, CancellationToken token);
    }
    [Serializable] internal sealed class CauServerRecord
    {
        public string avatarId, userId, name, modelUpdatedAt, metadataUpdatedAt, queriedAt, error, imageUrl, thumbnailUrl, imageMd5;
        public bool imageChecked;
        public int assetVersion;
    }
    internal static class CauServerQueries
    {
        public static bool Busy { get; private set; }
        public static int Version { get; private set; }
        static CancellationTokenSource active;
        static CauServerQueries()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => active?.Cancel();
            EditorApplication.quitting += () => active?.Cancel();
            EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.ExitingEditMode) active?.Cancel(); };
        }
        // The version is taken from the active Windows package URL, never the avatar's
        // general updated_at timestamp (which also changes after a cover/name edit).
        internal static bool TryModelFile(string url, out string fileId, out int version)
        {
            fileId = null; version = 0;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            for (int i = 0; i + 3 < parts.Length; i++)
            {
                if (parts[i] != "file" || !parts[i+1].StartsWith("file_", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(parts[i+1].Substring(5), "D", out _) || parts[i+3] != "file" ||
                    !int.TryParse(parts[i+2], out int parsed) || parsed < 1) continue;
                fileId = parts[i+1]; version = parsed; return true;
            }
            return false;
        }
        internal static string AvatarId(CauQueueItem item)
        {
            var plan = item.Plan(); var group = item.Group(plan);
            if (group != null) return CauExtraUploads.HasBlueprint(group) ? group.blueprintId.Trim().ToLowerInvariant() : "";
            var job = CauExtraUploads.State.jobs.LastOrDefault(j => !j.metadataOnly && j.sourceKey == item.sourceKey && j.groupId == item.groupId && j.modelSucceeded);
            return job?.avatarId ?? item.blueprintId;
        }
        internal static CauServerRecord Cached(string id, string user) => CauBatchQueue.Servers.LastOrDefault(r => r.avatarId == id && r.userId == user);
        internal static async Task<CauServerRecord> Fetch(string id, ICauServerDetailsBackend api, CancellationToken token)
        {
            string user = api.UserId;
            if (string.IsNullOrEmpty(user)) throw new InvalidOperationException("请先登录 VRChat SDK。");
            if (string.IsNullOrEmpty(id) || !id.StartsWith("avtr_", StringComparison.Ordinal) || !Guid.TryParseExact(id.Substring(5), "D", out _))
                throw new InvalidOperationException("此版本还没有有效蓝图，请先完成首次上传。");
            var remote = await api.Details(id, token); token.ThrowIfCancellationRequested();
            if (api.UserId != user || remote == null || remote.id != id || remote.authorId != user)
                throw new InvalidOperationException("账号或服务器蓝图不匹配，未保存查询结果。");
            return new CauServerRecord { avatarId=id, userId=user, name=remote.name, modelUpdatedAt=remote.modelUpdatedAt,
                metadataUpdatedAt=remote.metadataUpdatedAt, assetVersion=remote.assetVersion, error=remote.modelTimeError,
                imageUrl=remote.imageUrl, thumbnailUrl=remote.thumbnailUrl, imageMd5=remote.imageMd5, imageChecked=true,
                queriedAt=DateTime.UtcNow.ToString("o") };
        }
        internal static void RecordApplied(CauExtraJob job)
        {
            var record = Cached(job.avatarId, job.userId);
            if (record == null || (!job.nameApplied && !job.imageApplied)) return;
            if (job.nameApplied) record.name = job.desiredName.Trim();
            if (job.imageApplied)
            {
                if (record.imageUrl != job.appliedImageUrl) record.thumbnailUrl = null;
                record.imageUrl = job.appliedImageUrl;
                record.imageChecked = true;
                record.imageMd5 = job.appliedImageMd5;
            }
            CauBatchQueue.Save();
        }

        public static async void Query(CauQueueItem[] items)
        {
            if (CauBatchQueue.Locked) return;
            Busy = true; Version++;
            try
            {
                var ids = items.Select(AvatarId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();
                if (ids.Length == 0) throw new InvalidOperationException("所选版本还没有蓝图号。");
                var api = new CauSdkExtraBackend();
                string originalUser = api.UserId; int failures = 0; string lastError = null;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(180, 30 * ids.Length))))
                {
                    active = timeout;
                    foreach (var id in ids)
                    {
                        if (!SplitIds.Editing) throw new OperationCanceledException();
                        if (api.UserId != originalUser) throw new InvalidOperationException("账号已切换，查询已停止。");
                        CauBatchQueue.SetMessage("正在查询服务器 " + (Array.IndexOf(ids,id)+1) + "/" + ids.Length + "…");
                        try
                        {
                            var record = await Fetch(id, api, timeout.Token);
                            if (!SplitIds.Editing) throw new OperationCanceledException();
                            CauBatchQueue.Servers.RemoveAll(r => r.avatarId == record.avatarId && r.userId == record.userId);
                            CauBatchQueue.Servers.Add(record); CauBatchQueue.Save();
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { failures++; lastError = e.Message; CauBatchQueue.SetMessage("查询未完成："+e.Message); }
                    }
                }
                CauBatchQueue.SetMessage(failures == 0 ? "服务器查询完成，未执行上传。" : "服务器查询结束，" + failures + " 项未完成：" + lastError);
            }
            catch (Exception e) { CauBatchQueue.SetMessage(e is OperationCanceledException ? "查询已取消或超时，请重试。" : e.Message); }
            finally { active = null; Busy = false; Version++; }
        }
        internal static string LocalTime(string value)
        {
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time)
                ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "服务器未提供";
        }
    }
}
#endif
