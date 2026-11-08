using nadena.dev.ndmf;
using UnityEngine;

namespace Zincat.Switches
{
    public enum SwitchMode { Bool, Int }

    /// <summary>编辑器识别记录；开关的名称、目标、动作和参数以标准 MA 组件为准。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Zincat/分件开关标记")]
    public sealed class ZincatSwitchMarker : MonoBehaviour, INDMFEditorOnly
    {
        [HideInInspector] public string Id;
        [HideInInspector] public SwitchMode Mode;
        [HideInInspector] public string GroupId;
        [HideInInspector] public GameObject GroupRoot;
        [HideInInspector] public bool IsGroup;
    }
}
