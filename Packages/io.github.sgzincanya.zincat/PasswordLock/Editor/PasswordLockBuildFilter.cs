using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zincat.PasswordLock.Editor
{
    internal static class PasswordLockBuildFilter
    {
        internal static void Process(GameObject avatarRoot, Action<Object> save)
        {
            bool isPlay = EditorApplication.isPlayingOrWillChangePlaymode;
            foreach (var config in avatarRoot.GetComponentsInChildren<ZincatPasswordLock>(true))
            {
                if (isPlay)
                {
                    // Remove only plugin-owned legacy modules from the NDMF Play object.
                    var legacy = config.transform.Find(PasswordLockGenerator.GeneratedName);
                    if (legacy) Object.DestroyImmediate(legacy.gameObject);
                    continue;
                }
                if (!config.enabled || !config.gameObject.activeInHierarchy) continue;
                PasswordLockGenerator.Generate(config, save);
            }
        }
    }
}
