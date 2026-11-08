using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Zincat.Clothing
{
    /// <summary>
    /// 仅供 Unity 编辑窗口找回生成记录，按 NDMF 的编辑器专用组件标记。
    /// 实际菜单与开关仍完全由 Modular Avatar 组件负责。
    /// </summary>
    [DisallowMultipleComponent]
    [MovedFrom(true, "Zincat.Clothing", "Assembly-CSharp", "WardrobeGeneratedMenu")]
    [AddComponentMenu("")]
    public sealed class WardrobeGeneratedMenu : MonoBehaviour, INDMFEditorOnly
    {
        public GameObject ClothingRoot;
        public Texture2D GeneratedOutfitIcon;
        public Texture2D GeneratedWearIcon;
        public List<WardrobeSlotLink> Slots = new List<WardrobeSlotLink>();
        public GameObject OutfitSwitchNode;
        public string OutfitSwitchGroupParameter;
        public string PartParameterPrefix;
        public GameObject BatchControlNode;
        public List<WardrobePartPreset> PartPresets = new List<WardrobePartPreset>();
    }

    [Serializable]
    public sealed class WardrobePartPreset
    {
        public string Name = "模板";
        // 未勾选的有效分件会隐藏；空分类不参与。记录分类键，不依赖可修改的菜单名称。
        public List<string> VisibleKeys = new List<string>();
    }

    [Serializable]
    public sealed class WardrobeSlotLink
    {
        public string Key;
        public GameObject MenuNode;
        public int IconSourceVersion;
        public Texture2D GeneratedIcon;
        // 仅用于编辑器截图，不参与 MA 控制或游戏材质。
        public bool IconTransparentCompatibility = true;
        public float IconYaw = 335;
        public float IconPitch = 22;
        public float IconPadding = .1f;
        public int IconViewVersion;
    }
}
