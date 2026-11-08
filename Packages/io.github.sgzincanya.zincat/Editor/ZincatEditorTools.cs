using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Zincat.EditorTools
{
    public static class ToolResources
    {
        public static string Resolve(string guid, string fallback)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ToolResources).Assembly);
            if (package != null && fallback.StartsWith("Assets/Zincat/", StringComparison.Ordinal))
            {
                string packaged = "Packages/" + package.name + fallback.Substring("Assets/Zincat".Length);
                if (AssetDatabase.IsValidFolder(packaged) || AssetDatabase.LoadMainAssetAtPath(packaged) != null)
                    return packaged;
            }
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? fallback : path;
        }
    }

    public static class CaptureService
    {
        public static byte[] Capture(Camera camera, int width, int height)
        {
            if (camera == null || EditorUtility.IsPersistent(camera))
                throw new ArgumentException("请选择场景中的摄像机。");
            if (width < 1 || height < 1 || width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize)
                throw new ArgumentException("分辨率超出当前设备支持的范围。");
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture renderTexture = null;
            Texture2D texture = null;
            try
            {
                renderTexture = new RenderTexture(width, height, 24);
                if (!renderTexture.Create()) throw new InvalidOperationException("无法创建截图缓冲区。");
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture.EncodeToPNG();
            }
            finally
            {
                if (camera != null) camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (renderTexture != null)
                {
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }
            }
        }

        public static string SafeFileName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            name = name.Trim().TrimEnd('.');
            return string.IsNullOrEmpty(name) ? "Camera" : name;
        }
    }

}
