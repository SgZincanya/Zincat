#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Zincat.AvatarSplitTest
{
    public sealed class AvatarSplitPlan : ScriptableObject
    {
        [NonSerialized] public AvatarSplitConfig config;
        public SplitPhotoSettings photoSettings = new SplitPhotoSettings();
        public string sourceId;
        public string sourceScene;
        public string outputRootId;
        public List<SplitGroup> groups = new List<SplitGroup>();
    }

    [Serializable]
    public sealed class SplitGroup
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "测试模型";
        public string uploadName;
        // Store exclusion so older saved groups and all new groups are selected by default.
        public bool excludeFromUpload;
        public bool UploadSelected => !excludeFromUpload;
        public string UploadName => string.IsNullOrWhiteSpace(uploadName) ? name : uploadName.Trim();
        public int version;
        public List<string> outfitIds = new List<string>();
        public List<string> outfitNames = new List<string>();
        public bool photoSelectionConfigured;
        public List<string> photoOutfitIds = new List<string>();
        public string blueprintId;
        public bool uploadSucceeded;
        public int revision;
        public int generatedRevision = -1;
        public string generatedAvatarId;
        public string generatedScene;
        public string cauSettingGuid;
        public string photoCoverGuid, photoCoverSignature, photoCoverHash;
        public List<string> removedPaths = new List<string>();
        public List<string> issues = new List<string>();
        public void Invalidate() { revision++; }
    }

    internal static class SplitIds
    {
        static readonly HashSet<AvatarSplitPlan> Pending = new HashSet<AvatarSplitPlan>();
        static double flushAt;
        internal static bool Reloading;
        const string PlayModeSessionKey = "Zincat.AvatarSplit.PlayModeSuspended";
        internal static bool PlayModeSuspended = SessionState.GetBool(PlayModeSessionKey, false) || EditorApplication.isPlayingOrWillChangePlaymode;
        internal static bool Editing => !PlayModeSuspended && !EditorApplication.isPlayingOrWillChangePlaymode;
        static readonly Dictionary<string, UnityEngine.Object> Resolved = new Dictionary<string, UnityEngine.Object>();
        static SplitIds()
        {
            EditorApplication.update += SaveWhenIdle;
            AssemblyReloadEvents.beforeAssemblyReload += () => { Reloading = true; Flush(); };
            EditorApplication.quitting += Flush;
            EditorSceneManager.sceneSaving += (_, __) => Flush();
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }
        internal static void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                Flush(); PlayModeSuspended = true;
            }
            else if (state == PlayModeStateChange.EnteredEditMode) PlayModeSuspended = false;
            else PlayModeSuspended = true;
            SessionState.SetBool(PlayModeSessionKey, PlayModeSuspended);
            Resolved.Clear(); SplitConfigStore.ClearCache();
        }
        public static string Of(UnityEngine.Object obj)
        {
            if (!obj) return "";
            var id = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString(); Resolved[id] = obj; return id;
        }
        public static UnityEngine.Object Resolve(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (Resolved.TryGetValue(id, out var cached) && cached) return cached;
            var result = GlobalObjectId.TryParse(id, out var value) ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(value) : null;
            if (result) Resolved[id] = result; else Resolved.Remove(id);
            return result;
        }
        public static GameObject Avatar(SplitGroup group) => Resolve(group.generatedAvatarId) as GameObject;
        public static void Save(AvatarSplitPlan plan)
        {
            if (EditorApplication.isPlaying || PlayModeSuspended) return;
            Pending.Remove(plan); EditorUtility.SetDirty(plan);
            if (plan.config) SplitConfigStore.Write(plan);
            else if (AssetDatabase.Contains(plan)) AssetDatabase.SaveAssetIfDirty(plan);
        }
        public static void SaveLater(AvatarSplitPlan plan)
        {
            if (!Editing) return;
            EditorUtility.SetDirty(plan); Pending.Add(plan); flushAt = EditorApplication.timeSinceStartup + 0.8;
        }
        public static bool IsPending(AvatarSplitPlan plan) => plan && Pending.Contains(plan);
        static void SaveWhenIdle() { if (Pending.Count > 0 && EditorApplication.timeSinceStartup >= flushAt) Flush(); }
        public static void Flush()
        {
            if (EditorApplication.isPlaying || PlayModeSuspended) return;
            if (Pending.Count == 0) return;
            var plans = new AvatarSplitPlan[Pending.Count]; Pending.CopyTo(plans); Pending.Clear();
            foreach (var plan in plans)
            {
                if (!plan) continue;
                if (plan.config)
                {
                    // Editing checkboxes must never save the full Avatar scene.
                    SplitConfigStore.Write(plan, false);
                    SplitConfigRecovery.Backup(plan);
                }
                else Save(plan);
            }
        }
    }
}
#endif
