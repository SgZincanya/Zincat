// SPDX-License-Identifier: MIT
using System;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Zincat.Lighting.Editor
{
    public sealed class LightingMaterialPreprocess : IVRCSDKPreprocessAvatarCallback
    {
        // NDMF starts at -11000; Shader generation must run while imports are enabled.
        public int callbackOrder => -12000;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            try
            {
                LightingSetup.PrepareForBuild(avatarGameObject);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, avatarGameObject);
                return false;
            }
        }
    }
}
