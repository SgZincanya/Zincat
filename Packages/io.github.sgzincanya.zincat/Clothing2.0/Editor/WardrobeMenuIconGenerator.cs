#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Zincat.Clothing.Editor
{
    internal static class WardrobeMenuIconGenerator
    {
        internal sealed class Result
        {
            public int Generated, Manual, Failed;
            public readonly List<string> Errors = new List<string>();
            public override string ToString() => "生成／更新 " + Generated + " 张 · 保留手动图片 " + Manual + " 项 · 失败 " + Failed + " 项" +
                (Errors.Count == 0 ? "" : "\n" + string.Join("\n", Errors.Take(5)));
        }

        internal static async Task<Texture2D> CaptureOutfit(GameObject clothing, Texture2D overwrite = null)
        {
            if (clothing == null) throw new InvalidOperationException("衣服本体引用丢失。");
            if (WardrobeMaIconPreview.Active != null)
            {
                WardrobeMaIconPreview.Active.SelectTargets(new[] { clothing });
                return WardrobePartIconRenderer.Capture(new[] { clothing }, Rotation(clothing), 335, 22, .1f, overwrite, true, WardrobeIconStorage.Folder(clothing));
            }
            using (await WardrobeMaIconPreview.PrepareAsync(new[] { clothing }))
                return WardrobePartIconRenderer.Capture(new[] { clothing }, Rotation(clothing), 335, 22, .1f, overwrite, true, WardrobeIconStorage.Folder(clothing));
        }
        private static Quaternion Rotation(GameObject clothing) => clothing.GetComponentInParent<VRCAvatarDescriptor>(true)?.transform.rotation ?? Quaternion.identity;
        private static bool CanReplace(ModularAvatarMenuItem item, Texture2D generated) => item != null && (item.Control.icon == null || item.Control.icon == generated && generated != null);
        internal static void Assign(ModularAvatarMenuItem item, Texture2D icon)
        {
            Undo.RecordObject(item, "更新菜单图标"); item.Control.icon = icon; EditorUtility.SetDirty(item);
            if (item.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(item.gameObject.scene);
        }
        internal static void ApplyOutfit(WardrobeGeneratedMenu marker, Texture2D icon)
        {
            var root = marker.GetComponent<ModularAvatarMenuItem>();
            var wear = marker.OutfitSwitchNode == null ? null : marker.OutfitSwitchNode.GetComponent<ModularAvatarMenuItem>();
            Undo.RecordObject(marker, "记录衣服自动图标");
            if (CanReplace(root, marker.GeneratedOutfitIcon)) { Assign(root, icon); marker.GeneratedOutfitIcon = icon; }
            if (CanReplace(wear, marker.GeneratedWearIcon)) { Assign(wear, icon); marker.GeneratedWearIcon = icon; }
            EditorUtility.SetDirty(marker);
            SyncCollectionsFor(marker);
        }
        internal static bool SyncCollectionIcon(WardrobeGeneratedSwitch collection)
        {
            if (collection == null || !collection.IsSeparateCollection || collection.DefaultOutfit == null) return false;
            var item = collection.GetComponent<ModularAvatarMenuItem>(); if (item?.Control == null) return false;
            var link = (collection.Entries ?? new List<WardrobeSwitchLink>()).FirstOrDefault(e => e != null && e.OutfitRoot == collection.DefaultOutfit);
            var root = link?.PartMenuRoot ?? link?.MenuNode;
            var marker = root == null ? null : root.GetComponent<WardrobeGeneratedMenu>();
            if (marker == null || marker.ClothingRoot != collection.DefaultOutfit)
            {
                var matches = collection.AvatarRoot == null ? Array.Empty<WardrobeGeneratedMenu>() : collection.AvatarRoot.GetComponentsInChildren<WardrobeGeneratedMenu>(true)
                    .Where(m => m.ClothingRoot == collection.DefaultOutfit).ToArray();
                if (matches.Length != 1) return false;
                marker = matches[0];
            }
            var icon = marker.GetComponent<ModularAvatarMenuItem>()?.Control?.icon;
            if (icon == null && marker.OutfitSwitchNode != null) icon = marker.OutfitSwitchNode.GetComponent<ModularAvatarMenuItem>()?.Control?.icon;
            if (item.Control.icon == icon) return false;
            Assign(item, icon); return true;
        }
        internal static void SyncCollectionsFor(WardrobeGeneratedMenu marker)
        {
            if (marker == null || marker.ClothingRoot == null) return;
            foreach (var group in Resources.FindObjectsOfTypeAll<WardrobeGeneratedSwitch>())
                if (!EditorUtility.IsPersistent(group) && group.gameObject.scene.IsValid() && group.gameObject.scene.isLoaded && !EditorSceneManager.IsPreviewScene(group.gameObject.scene) &&
                    group.DefaultOutfit == marker.ClothingRoot) SyncCollectionIcon(group);
        }
        internal static async Task<Result> GenerateAll(IEnumerable<WardrobeGeneratedMenu> markers, Action<string> progress = null)
        {
            var result = new Result(); var seen = new HashSet<string>();
            foreach (var marker in markers.Where(m => m != null).Distinct().ToArray())
            {
                if (marker.ClothingRoot == null) { result.Failed++; result.Errors.Add(marker.name + "：衣服引用丢失。"); continue; }
                try
                {
                    var root = marker.GetComponent<ModularAvatarMenuItem>();
                    var wear = marker.OutfitSwitchNode == null ? null : marker.OutfitSwitchNode.GetComponent<ModularAvatarMenuItem>();
                    bool rootAuto = CanReplace(root, marker.GeneratedOutfitIcon), wearAuto = CanReplace(wear, marker.GeneratedWearIcon);
                    if (root != null && !rootAuto) result.Manual++;
                    if (wear != null && !wearAuto) result.Manual++;
                    var parts = (marker.Slots ?? new List<WardrobeSlotLink>()).Where(s => s != null && s.MenuNode != null).ToArray();
                    var targetsForPreview = new[] { marker.ClothingRoot }.Concat(parts.SelectMany(link =>
                    {
                        var toggle = link.MenuNode.GetComponent<ModularAvatarObjectToggle>();
                        return toggle == null ? Array.Empty<GameObject>() : toggle.Objects.Select(o => o.Object?.Get(toggle)).Where(o => o != null);
                    })).Distinct().ToArray();
                    bool anyPart = parts.Any(link =>
                    {
                        var item = link.MenuNode.GetComponent<ModularAvatarMenuItem>();
                        return item != null && (item.Control.icon == null || (link.IconSourceVersion > 0 ? item.Control.icon == link.GeneratedIcon : WardrobePartIconRenderer.IsLegacyGeneratedIcon(item.Control.icon)));
                    });
                    if (rootAuto || wearAuto || anyPart) progress?.Invoke(marker.ClothingRoot.name + " · 准备 MA 预览");
                    using (rootAuto || wearAuto || anyPart ? await WardrobeMaIconPreview.PrepareAsync(targetsForPreview) : null)
                    {
                        if (rootAuto || wearAuto)
                        {
                            try
                            {
                                progress?.Invoke(marker.ClothingRoot.name + " · 整套衣服");
                                var previous = rootAuto && root.Control.icon != null ? root.Control.icon : wearAuto ? wear.Control.icon : null;
                                string path = previous == null ? "" : AssetDatabase.GetAssetPath(previous);
                                if (path.Length == 0 || seen.Add(path))
                                { var icon = await CaptureOutfit(marker.ClothingRoot, previous); ApplyOutfit(marker, icon); result.Generated++; }
                                else ApplyOutfit(marker, previous);
                            }
                            catch (Exception e) { result.Failed++; result.Errors.Add(marker.name + "：" + e.GetBaseException().Message); }
                        }
                        foreach (var link in parts)
                        {
                            var item = link.MenuNode.GetComponent<ModularAvatarMenuItem>();
                            if (item == null) continue;
                            bool automatic = link.IconSourceVersion > 0 ? item.Control.icon != null && item.Control.icon == link.GeneratedIcon : WardrobePartIconRenderer.IsLegacyGeneratedIcon(item.Control.icon);
                            if (item.Control.icon != null && !automatic) { result.Manual++; continue; }
                            try
                            {
                                progress?.Invoke(marker.ClothingRoot.name + " · " + item.name);
                                string path = item.Control.icon == null ? "" : AssetDatabase.GetAssetPath(item.Control.icon);
                                if (path.Length > 0 && !seen.Add(path)) continue;
                                var toggle = link.MenuNode.GetComponent<ModularAvatarObjectToggle>();
                                var targets = toggle == null ? Array.Empty<GameObject>() : toggle.Objects.Select(o => o.Object?.Get(toggle)).Where(o => o != null).Distinct().ToArray();
                                if (targets.Length == 0) throw new InvalidOperationException("没有可截图的分件对象。");
                                Texture2D icon;
                                WardrobeMaIconPreview.Active?.SelectTargets(targets);
                                icon = WardrobePartIconRenderer.Capture(targets, Rotation(marker.ClothingRoot), link.IconViewVersion > 0 ? link.IconYaw : 335, link.IconViewVersion > 0 ? link.IconPitch : 22, link.IconViewVersion > 0 ? link.IconPadding : .1f,
                                        automatic ? item.Control.icon : null, link.IconTransparentCompatibility, WardrobeIconStorage.Folder(marker.ClothingRoot));
                                Assign(item, icon); Undo.RecordObject(marker, "记录分件自动图标"); link.GeneratedIcon = icon; link.IconSourceVersion = 1; EditorUtility.SetDirty(marker); result.Generated++;
                            }
                            catch (Exception e) { result.Failed++; result.Errors.Add(marker.name + "/" + item.name + "：" + e.GetBaseException().Message); }
                        }
                    }
                }
                catch (Exception e) { result.Failed++; result.Errors.Add(marker.name + "：预览准备失败，" + e.GetBaseException().Message); }
            }
            return result;
        }
    }
}
#endif
