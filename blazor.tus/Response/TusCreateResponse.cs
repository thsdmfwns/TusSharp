using System;
using blazor.tus.Response.Base;

namespace blazor.tus.Response;

/// <summary>
/// 
/// </summary>
public sealed class TusCreateResponse : TusResponseBase
{
    /// <summary>
    /// file url
    /// </summary>
    public Uri FileLocation { get; set; }
}