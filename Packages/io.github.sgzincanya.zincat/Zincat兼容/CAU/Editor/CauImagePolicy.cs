#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Zincat.AvatarSplitTest
{
    internal sealed class CauImageVersion
    {
        public string url,md5,status,fileStatus;
        public int version,size;
        public bool deleted;
    }
    internal static class CauImagePolicy
    {
        internal static CauImageVersion Reusable(IEnumerable<CauImageVersion> versions,string fileId,int currentVersion,string md5,long size)
        {
            return versions?.Where(v=>v!=null&&!v.deleted&&v.status=="complete"&&v.fileStatus=="complete"&&
                v.md5==md5&&v.size==size&&CauServerQueries.TryModelFile(v.url,out var id,out int n)&&id==fileId&&n==v.version)
                .OrderByDescending(v=>v.version==currentVersion).ThenByDescending(v=>v.version).FirstOrDefault();
        }
        internal static bool Duplicate(Exception error)
        {
            for(var e=error;e!=null;e=e.InnerException)
                if(e.Message=="This file was already uploaded"||e.Message=="This file was already uploaded, you should make a new build")return true;
            return false;
        }
        internal static bool Transient(Exception error)
        {
            for(var e=error;e!=null;e=e.InnerException)
            {
                if(e is System.Net.Http.HttpRequestException)return true;
                if(e is WebException web)
                    switch(web.Status)
                    {
                        case WebExceptionStatus.NameResolutionFailure:case WebExceptionStatus.ProxyNameResolutionFailure:
                        case WebExceptionStatus.ConnectFailure:case WebExceptionStatus.ConnectionClosed:
                        case WebExceptionStatus.ReceiveFailure:case WebExceptionStatus.SendFailure:
                        case WebExceptionStatus.Timeout:case WebExceptionStatus.KeepAliveFailure:return true;
                    }
            }
            return false;
        }
        internal static string Explain(Exception error)
        {
            for(var e=error;e!=null;e=e.InnerException)
                if(e is WebException web)
                {
                    if(web.Status==WebExceptionStatus.NameResolutionFailure||web.Status==WebExceptionStatus.ProxyNameResolutionFailure)return "DNS 域名解析失败，请检查当前网络或代理后重试封面。";
                    if(web.Status==WebExceptionStatus.Timeout)return "封面请求超时，请稍后重试。";
                    if(web.Status==WebExceptionStatus.TrustFailure||web.Status==WebExceptionStatus.SecureChannelFailure)return "封面请求的 TLS/证书连接失败。";
                }
            return "封面网络请求失败，请稍后重试；模型不会重新上传。";
        }
    }
}
#endif
