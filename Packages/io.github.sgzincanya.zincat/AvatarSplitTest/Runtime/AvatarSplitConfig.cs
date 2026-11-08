using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Zincat.AvatarSplitTest
{
    // Direct references are remapped by Unity when the complete Avatar is duplicated/exported.
    [DisallowMultipleComponent, AddComponentMenu("")]
    public sealed class AvatarSplitConfig : MonoBehaviour, INDMFEditorOnly
    {
        [HideInInspector] public string Key;
        [HideInInspector, TextArea] public string Data;
        [HideInInspector] public GameObject Source;
        [HideInInspector] public GameObject Output;
        [HideInInspector] public AnimationClip PhotoPoseClip;
        [HideInInspector] public List<AvatarSplitReferences> References = new List<AvatarSplitReferences>();
    }

    [Serializable]
    public sealed class AvatarSplitReferences
    {
        public string Id;
        public List<GameObject> Outfits = new List<GameObject>();
        public GameObject Generated;
        public List<GameObject> PhotoOutfits = new List<GameObject>();
        public Texture2D PhotoCover;
    }
}
