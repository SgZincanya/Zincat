#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Zincat.AvatarSplitTest
{
    internal static class CauCoverPreparation
    {
        // Use the same SDK crop operation as its avatar panel, on a snapshot only.
        // Keep the returned upload copy isolated from SDK/UI temporary image changes.
        internal static string Prepare(string source)
        {
            var method=typeof(VRC.SDKBase.VRC_EditorTools).GetMethod("CropImage",BindingFlags.Static|BindingFlags.NonPublic,
                null,new[]{typeof(string),typeof(float),typeof(float),typeof(bool),typeof(bool),typeof(bool)},null);
            if(method==null)throw new InvalidOperationException("当前 SDK 缺少封面处理接口，未发送原图。");
            var previous=RenderTexture.active;
            string path;
            try{path=method.Invoke(null,new object[]{source,800f,600f,true,false,false}) as string;}
            catch(TargetInvocationException e){throw new InvalidOperationException("SDK 封面处理失败，未上传。",e.InnerException??e);}
            finally{RenderTexture.active=previous;}
            if(string.IsNullOrEmpty(path)||!File.Exists(path))throw new InvalidOperationException("SDK 未生成有效的封面文件。");
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            finally { File.Delete(path); }
            if(!Is800By600Png(bytes))throw new InvalidOperationException("SDK 封面处理结果不是 800×600 PNG，未上传。");
            string folder="Library/ZincatCauExtras/PreparedImages";Directory.CreateDirectory(folder);
            string copy=Path.Combine(folder,Guid.NewGuid().ToString("N")+".png");File.WriteAllBytes(copy,bytes);return copy;
        }
        internal static string FileMd5(string path)
        {
            using (var hash = System.Security.Cryptography.MD5.Create())
            using (var input = File.OpenRead(path))
                return Convert.ToBase64String(hash.ComputeHash(input));
        }

        internal static bool Is800By600Png(byte[] data)
        {
            if(data==null||data.Length<24)return false;
            byte[] magic={137,80,78,71,13,10,26,10};for(int i=0;i<magic.Length;i++)if(data[i]!=magic[i])return false;
            return data[12]==73&&data[13]==72&&data[14]==68&&data[15]==82&&
                data[16]==0&&data[17]==0&&data[18]==3&&data[19]==32&&data[20]==0&&data[21]==0&&data[22]==2&&data[23]==88;
        }
    }
}
#endif
