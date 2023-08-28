using System.Net.Http;

namespace blazor.tus.Response.Base;

public abstract class TusResponseBase
{
    /// <summary>
    /// origin http response
    /// </summary>
    public HttpResponseMessage OriginResponseMessage { get; set; }
    
    
    /// <summary>
    /// origin HttpRequestMessage
    /// </summary>
    public HttpRequestMessage OriginHttpRequestMessage { get; set; }
    
    /// <summary>
    /// tus version from server
    /// </summary>
    public string TusResumableVersion { get; set; }
}