using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Zincat.AvatarSplitTest
{
    [DisallowMultipleComponent, AddComponentMenu("")]
    public sealed class AvatarSplitOutputRoot : MonoBehaviour, INDMFEditorOnly
    {
        public string PlanGuid;
        public GameObject Source;
        public List<GameObject> Generated = new List<GameObject>();
    }
}
