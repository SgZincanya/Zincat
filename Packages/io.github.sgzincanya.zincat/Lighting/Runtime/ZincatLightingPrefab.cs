// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Zincat.Lighting
{
    [Serializable]
    public class PrefabLightControl
    {
        public bool enabled = true;
        public float lower;
        public float upper = 1;
        public float initial;

        public float NormalizedInitial => Mathf.InverseLerp(lower, upper, initial);
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Zincat/光照菜单配置")]
    public sealed class ZincatLightingPrefab : MonoBehaviour, INDMFEditorOnly
    {
        public string menuTitle = "光照控制";
        public string parameterPrefix = "ZincatLighting";
        public bool networkSynced = true;
        public bool rememberValues = true;
        public bool followAdditiveLighting = true;
        public PrefabLightControl minimum = new PrefabLightControl { lower = 0, upper = 1, initial = 0 };
        public PrefabLightControl maximum = new PrefabLightControl { lower = 0, upper = 2, initial = 1 };
        public PrefabLightControl monochrome = new PrefabLightControl { lower = 0, upper = 1, initial = 1 };
        public List<Renderer> excludedRenderers = new List<Renderer>();
        public bool installMenuAutomatically = true;
        [HideInInspector] public GameObject menuRoot;
        [HideInInspector] public GameObject submenuObject;
        [HideInInspector] public GameObject minimumMenu;
        [HideInInspector] public GameObject maximumMenu;
        [HideInInspector] public GameObject environmentMenu;
    }
}
