#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Zincat.Clothing.Editor
{
    // Shipped artwork, separate from user-generated screenshots in Cache/MenuIcons.
    internal static class WardrobeBatchIcons
    {
        private const string FillMenuPath = "Zincat/衣服菜单/补齐所选菜单图标";

        internal static Texture2D Load(string name)
        {
            return Resources.Load<Texture2D>("ZincatClothing/BatchIcons/" + name);
        }

        [MenuItem(FillMenuPath)]
        private static void FillSelection()
        {
            int count = FillMissing(Selection.gameObjects);
            string message = count > 0 ? "已补齐 " + count + " 个菜单图标，可撤销；保存场景后保留。"
                : "所选层级中没有需要补齐的批量菜单图标。";
            Debug.Log("[Zincat 衣服菜单] " + message, Selection.activeGameObject);
            SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(message));
        }

        [MenuItem(FillMenuPath, true)]
        private static bool CanFillSelection()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode && !BuildPipeline.isBuildingPlayer &&
                Selection.gameObjects.Any(IsSceneObject);
        }

        private static bool IsSceneObject(GameObject target)
        {
            return target != null && !EditorUtility.IsPersistent(target) && target.scene.IsValid() && target.scene.isLoaded &&
                !EditorSceneManager.IsPreviewScene(target.scene) && (target.hideFlags & HideFlags.DontSave) == 0;
        }

        // Read ownership records locally, then limit every write to the selected subtrees.
        internal static int FillMissing(IEnumerable<GameObject> selection)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer) return 0;
            var roots = (selection ?? Enumerable.Empty<GameObject>()).Where(IsSceneObject).Distinct().ToArray();
            var owners = roots.SelectMany(root => root.GetComponentsInChildren<WardrobeGeneratedMenu>(true)
                .Concat(root.GetComponentsInParent<WardrobeGeneratedMenu>(true))).Distinct();
            var changes = new Dictionary<ModularAvatarMenuItem, Texture2D>();
            void Add(GameObject node, string iconName)
            {
                if (!IsSceneObject(node) || !roots.Any(root => node.transform.IsChildOf(root.transform))) return;
                var item = node.GetComponent<ModularAvatarMenuItem>();
                if (item?.Control == null || item.Control.icon != null) return;
                var icon = Load(iconName);
                if (icon != null) changes[item] = icon;
            }
            foreach (var owner in owners)
            {
                var batch = owner.BatchControlNode;
                if (!IsSceneObject(batch) || !batch.transform.IsChildOf(owner.transform) ||
                    batch.GetComponent<ModularAvatarMenuItem>()?.Control?.type != VRCExpressionsMenu.Control.ControlType.SubMenu)
                    continue;
                Add(batch, "BatchControl");
                if (string.IsNullOrEmpty(owner.PartParameterPrefix)) continue;
                foreach (Transform child in batch.transform)
                {
                    var control = child.GetComponent<ModularAvatarMenuItem>()?.Control;
                    if (control?.type != VRCExpressionsMenu.Control.ControlType.Button ||
                        control.parameter?.name != owner.PartParameterPrefix + "Batch") continue;
                    float value = control.value;
                    if (value == 1) Add(child.gameObject, "ShowAll");
                    else if (value == 2) Add(child.gameObject, "HideAll");
                    else if (value >= 3 && value <= 2 + (owner.PartPresets?.Count ?? 0) && value == Mathf.Floor(value))
                        Add(child.gameObject, "Preset");
                }
            }
            if (changes.Count == 0) return 0;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("补齐所选菜单图标");
            foreach (var change in changes)
            {
                Undo.RecordObject(change.Key, "补齐所选菜单图标");
                change.Key.Control.icon = change.Value;
                PrefabUtility.RecordPrefabInstancePropertyModifications(change.Key);
                EditorUtility.SetDirty(change.Key);
                EditorSceneManager.MarkSceneDirty(change.Key.gameObject.scene);
            }
            Undo.CollapseUndoOperations(group);
            Undo.IncrementCurrentGroup();
            return changes.Count;
        }
    }
}
#endif
