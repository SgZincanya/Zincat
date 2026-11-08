#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Zincat.ColorOverview
{
    public sealed partial class ColorOverviewWindow
    {
        [Serializable] private sealed class StoredVariant
        {
            public string Name, Guid;
            public bool Enabled;
            public List<MatSwap> Swaps = new List<MatSwap>();
        }
        [Serializable] private sealed class StoredSet
        {
            public string Name;
            public bool Enabled;
            public List<StoredVariant> Variants = new List<StoredVariant>();
        }
        private List<StoredSet> clearUndo;
        private List<StoredSet> StoreList()
        {
            return sets.Select(set => new StoredSet
            {
                Name = set.Name, Enabled = set.Enabled,
                Variants = set.Variants.Select(v => new StoredVariant
                {
                    Name = v.Name, Guid = string.IsNullOrEmpty(v.PrefabGuid) ? AssetDatabase.AssetPathToGUID(v.Path) : v.PrefabGuid,
                    Enabled = v.Enabled,
                    Swaps = v.Swaps.Select(s => new MatSwap { From = s.From, To = s.To }).ToList()
                }).ToList()
            }).ToList();
        }
        private void RestoreList(List<StoredSet> stored)
        {
            sets.Clear();
            foreach (var item in stored ?? new List<StoredSet>())
            {
                var set = new Set { Name = item.Name, Enabled = item.Enabled };
                foreach (var v in item.Variants)
                    set.Variants.Add(new Variant
                    {
                        Name = v.Name, PrefabGuid = v.Guid, Path = AssetDatabase.GUIDToAssetPath(v.Guid), Enabled = v.Enabled,
                        Swaps = v.Swaps.Select(s => new MatSwap { From = s.From, To = s.To }).ToList()
                    });
                sets.Add(set);
            }
        }
        private void SaveCurrentList()
        {
            if (aiSnapshot == null) aiSnapshot = new AiSnapshot();
            aiSnapshot.HasSavedSets = true; aiSnapshot.SavedSets = StoreList();
            aiSnapshot.Accepted = null;
            SaveAiSession();
        }
        private void BeginClear()
        {
            clearUndo = StoreList();
            status = null; aiStatus = null; aiError = false;
        }
        private void UndoClear()
        {
            if (clearUndo == null) return;
            AiAction(() => { RestoreList(clearUndo); clearUndo = null; SaveCurrentList(); status = null; aiStatus = null; });
            Repaint();
        }
    }
}
#endif
