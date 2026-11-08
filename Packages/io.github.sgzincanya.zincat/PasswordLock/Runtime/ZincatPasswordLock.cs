using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace Zincat.PasswordLock
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Zincat.PasswordLock", "Assembly-CSharp", "ZincatPasswordLock")]
    [DisallowMultipleComponent]
    [AddComponentMenu("Zincat/密码锁（MA）")]
    public sealed class ZincatPasswordLock : MonoBehaviour, IEditorOnly
    {
        [HideInInspector] public string identifier;
        [HideInInspector] public string assetFolder;
        [Tooltip("1–20 位数字，只能使用 0–9。")]
        public string password = "";
        [Tooltip("保存解锁状态，切换角色后返回无需再次输入；清空会重新锁定。")]
        public bool rememberUnlock = true;
        [Tooltip("PC 实验功能：进入密码锁菜单时，在视野下方显示输入圆点。")]
        public bool hudEnabled = true;
        [Range(0.1f, 0.8f)] public float hudVertical = 0.34f;
        [Range(0.004f, 0.015f)] public float hudDotRadius = 0.006f;
        [Tooltip("留空表示角色中的所有 Renderer。包含未激活的衣服和粒子渲染器。")]
        public List<GameObject> hiddenRoots = new List<GameObject>();
        [Tooltip("这些对象及其子对象在锁定时仍可显示。")]
        public List<GameObject> exclusions = new List<GameObject>();
        public bool hideLights = true;
        // Legacy serialized setting; no longer used.
        [HideInInspector] public bool playPreview;
    }
}
