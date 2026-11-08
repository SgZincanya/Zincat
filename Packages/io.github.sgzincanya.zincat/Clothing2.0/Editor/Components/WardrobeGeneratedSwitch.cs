using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Zincat.Clothing
{
    /// <summary>记录本工具生成的整套换装菜单；Avatar 构建时由 NDMF 移除。</summary>
    [DisallowMultipleComponent]
    [MovedFrom(true, "Zincat.Clothing", "Assembly-CSharp", "WardrobeGeneratedSwitch")]
    [AddComponentMenu("")]
    public sealed class WardrobeGeneratedSwitch : MonoBehaviour, INDMFEditorOnly
    {
        public GameObject AvatarRoot;
        // 新版副工具的独立总菜单；旧版合并菜单保持 false，避免误覆盖。
        public bool IsSeparateCollection;
        // true 表示通过 MA Menu Install Target 引用原菜单，不移动原菜单物体。
        public bool UsesInstallTargets;
        // true 表示换衣项直接控制衣服根物体，不再生成中间控制节点。
        public bool DirectOutfitToggles;
        // true 表示使用一帧动画 + MA Merge Motion 控制整套衣服。
        public bool UsesMergeMotion;
        public Motion GeneratedMotion;
        public List<AnimationClip> GeneratedClips = new List<AnimationClip>();
        public string ParameterName;
        public GameObject DefaultOutfit;
        // 占位衣服仅作为额外关闭目标，不编入正常衣服的换衣参数。
        public bool UsesPlaceholderOutfit;
        public GameObject PlaceholderOutfit;
        public List<WardrobeSwitchLink> Entries = new List<WardrobeSwitchLink>();
    }

    [Serializable]
    public sealed class WardrobeSwitchLink
    {
        public GameObject OutfitRoot;
        public GameObject MenuNode;
        // 旧版只记录 MenuNode（其本身是切换开关）；新版为衣服子菜单。
        public GameObject WearNode;
        public GameObject PartsNode;
        public GameObject PartMenuRoot;
        public bool PartInstallerWasEnabled;
        public VRCExpressionsMenu PartInstallTargetMenu;
        public VRCExpressionsMenu PartMenuToAppend;
    }
}
