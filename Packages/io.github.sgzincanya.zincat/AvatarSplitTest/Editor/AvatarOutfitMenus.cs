#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEngine;
using Zincat.Clothing;

namespace Zincat.AvatarSplitTest
{
    internal sealed class OutfitMenuChoice
    {
        public GameObject Outfit;
        public GameObject Menu;
        public string Name;
        public string Id;
        public GUIContent Label;
    }

    internal static class AvatarOutfitMenus
    {
        public static List<OutfitMenuChoice> Read(GameObject avatar)
            => Read(avatar, out _);

        public static List<OutfitMenuChoice> Read(GameObject avatar, out GameObject placeholder)
        {
            if (!SplitIds.Editing) throw new InvalidOperationException("运行期间衣服菜单可能已被处理，请退出游戏模式后扫描。");
            var collection = AvatarSplitBuilder.Collection(avatar);
            placeholder = collection.UsesPlaceholderOutfit ? collection.PlaceholderOutfit : null;
            var result = new List<OutfitMenuChoice>();
            foreach (var entry in collection.Entries)
            {
                var marker = entry.PartMenuRoot.GetComponent<WardrobeGeneratedMenu>();
                var menu = entry.PartMenuRoot.GetComponent<ModularAvatarMenuItem>();
                if (!marker || marker.ClothingRoot != entry.OutfitRoot || !menu ||
                    !entry.PartMenuRoot.transform.IsChildOf(avatar.transform) || !entry.OutfitRoot.transform.IsChildOf(avatar.transform))
                    throw new InvalidOperationException("衣服菜单与衣服的绑定不完整：" + entry.PartMenuRoot.name + "。请先在原插件修复该菜单。");
                // Match the existing clothing plugin: MA label overrides the node
                // name; an empty label means “use object name”. Control.name may
                // be stale after renaming the hierarchy and must not be used.
                var name = string.IsNullOrEmpty(menu.label) ? entry.PartMenuRoot.name : menu.label;
                result.Add(new OutfitMenuChoice {
                    Outfit = entry.OutfitRoot, Menu = entry.PartMenuRoot,
                    Name = name, Id = SplitIds.Of(entry.OutfitRoot),
                    Label = new GUIContent(name, "衣服：" + entry.OutfitRoot.name + "\n分件菜单：" + entry.PartMenuRoot.name)
                });
            }
            return result;
        }
    }
}
#endif
