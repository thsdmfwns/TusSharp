using blazor.tus.Response.Base;

namespace blazor.tus.Response;

public class TusHeadResponse:TusResponseBase
{
    /// <summary>
    /// UploadOffset
    /// </summary>
    public long UploadOffset { get; set; }
    
    /// <summary>
    /// if Upload-Length unknown, value is less than zero
    /// </summary>
    public long UploadLength { get; set; }
}