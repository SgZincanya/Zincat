#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
namespace Zincat.Clothing.Editor
{
    internal static class WardrobeIconStorage
    {
        internal const string Root = "Assets/Zincat/Clothing2.0/Cache/MenuIcons";
        internal const string Legacy = "Assets/Zincat/ClothingIcons";
        internal static bool IsGeneratedPath(string path) =>
            (path.StartsWith(Legacy + "/", StringComparison.Ordinal) || path.StartsWith(Root + "/", StringComparison.Ordinal)) &&
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(path).StartsWith("Part_", StringComparison.Ordinal) &&
            Guid.TryParseExact(Path.GetFileNameWithoutExtension(path).Substring(5), "N", out _);

        internal static void EnsureFolder(string path)
        {
            var parts = path.Split('/'); string current = parts[0];
            foreach (var part in parts.Skip(1)) { if (!AssetDatabase.IsValidFolder(current + "/" + part)) AssetDatabase.CreateFolder(current, part); current += "/" + part; }
        }
        internal static string Folder(GameObject clothing)
        {
            if (clothing == null) return Root + "/未关联";
            string identity = GlobalObjectId.GetGlobalObjectIdSlow(clothing).ToString();
            if (identity.Contains("-00000000000000000000000000000000-")) identity += clothing.GetInstanceID();
            string id; using (var hash = SHA256.Create()) id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").Substring(0, 10);
            if (AssetDatabase.IsValidFolder(Root))
            {
                var existing = AssetDatabase.GetSubFolders(Root).FirstOrDefault(f => f.EndsWith("_" + id, StringComparison.Ordinal));
                if (existing != null) return existing;
            }
            var invalid = Path.GetInvalidFileNameChars();
            string name = new string(clothing.name.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (name.Length == 0) name = "衣服"; if (name.Length > 48) name = name.Substring(0, 48);
            return Root + "/" + name + "_" + id;
        }
        internal static void SetCompression(TextureImporter importer)
        {
            importer.textureCompression = TextureImporterCompression.CompressedLQ;
            importer.crunchedCompression = true; importer.compressionQuality = 50;
            var desktop = importer.GetPlatformTextureSettings("Standalone");
            if (desktop.overridden)
            {
                desktop.format = TextureImporterFormat.Automatic;
                desktop.textureCompression = TextureImporterCompression.CompressedLQ;
                desktop.crunchedCompression = true; desktop.compressionQuality = 50;
                importer.SetPlatformTextureSettings(desktop);
            }
        }
    }
}
#endif
