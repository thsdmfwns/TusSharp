using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using blazor.tus.Constants;
using blazor.tus.Infrastructure;
using blazor.tus.Internal;
using blazor.tus.Request;
using blazor.tus.Response;

namespace blazor.tus;

public class TusUpload : IDisposable
{
    public TusUpload(Stream fileStream, TusUploadOption uploadOption, CancellationToken? cancellationToken = null)
    {
        FileStream = fileStream;
        UploadOption = uploadOption;
        CancellationToken = cancellationToken ?? default;
    }

    public readonly Stream FileStream;
    public readonly TusUploadOption UploadOption;
    public readonly CancellationToken CancellationToken;
    private bool _disposedValue;
    private HttpClient _httpClient = new HttpClient();

    private async Task<TusCreateResponse> TusCreateAsync()
    {
        var uploadLength = FileStream.Length;
        var endpoint = UploadOption.EndPoint;
        if (UploadOption.IsUploadDeferLength && uploadLength > 0)
        {
            throw new ArgumentException($"IsUploadDeferLength:[{UploadOption.IsUploadDeferLength}] can not set true if UploadLength:[{uploadLength}] is greater than zero");
        }
        if (!UploadOption.IsUploadDeferLength && uploadLength <= 0)
        {
            throw new ArgumentException($"IsUploadDeferLength:[{UploadOption.IsUploadDeferLength}] can not set false if UploadLength:[{uploadLength}] is less than zero");
        }
        
        var httpReqMsg = new HttpRequestMessage(HttpMethod.Post, UploadOption.EndPoint);
        httpReqMsg.Headers.Add(TusHeaders.TusResumable, UploadOption.TusVersion.GetEnumDescription());
        if (uploadLength > 0)
        {
            httpReqMsg.Headers.Add(TusHeaders.UploadLength, uploadLength.ToString());
        }
        else if(UploadOption.IsUploadDeferLength)
        {
            httpReqMsg.Headers.Add(TusHeaders.UploadDeferLength,"1");
        }
        
        var uploadMetadata = UploadOption.SerializedMetaData;
        if (!string.IsNullOrWhiteSpace(uploadMetadata))
        {
            httpReqMsg.Headers.Add(TusHeaders.UploadMetadata, uploadMetadata);
        }
        AddCustomHeaders(httpReqMsg);

        var response = await _httpClient.SendAsync(httpReqMsg, CancellationToken);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            UploadOption.OnFailed?.Invoke((response, httpReqMsg));
        }

        Uri? fileUrl;
        if (!response.TryGetValueOfHeader(TusHeaders.Location, out var fileUrlStr)
            || fileUrlStr is null
            || !Uri.TryCreate(fileUrlStr, UriKind.RelativeOrAbsolute, out fileUrl))
        {
            throw new IOException("Invalid location header");
        }
        if (!fileUrlStr.StartsWith("https://") && !fileUrlStr.StartsWith("http://"))
        {
            fileUrl = new Uri(endpoint, fileUrl);
        }

        if (!response.TryGetValueOfHeader(TusHeaders.TusResumable, out var tusVersion)
            || tusVersion is null)
        {
            throw new IOException("Invalid TusResumable header");
        }

        return new TusCreateResponse()
        {
            FileLocation = fileUrl,
            OriginHttpRequestMessage = httpReqMsg,
            OriginResponseMessage = response,
            TusResumableVersion = tusVersion
        };
    }
    
    private async Task<TusHeadResponse> TusHeadAsync(TusCreateResponse tusCreateResponse)
    {
        if (tusCreateResponse.FileLocation is null)
        {
            throw new ArgumentNullException(nameof(tusCreateResponse.FileLocation));
        }
        var httpReqMsg = new HttpRequestMessage(HttpMethod.Head, tusCreateResponse.FileLocation);
        httpReqMsg.Headers.Add(TusHeaders.TusResumable, tusCreateResponse.TusResumableVersion);
        AddCustomHeaders(httpReqMsg);
        var response = await _httpClient.SendAsync(httpReqMsg, CancellationToken);
        response.EnsureSuccessStatusCode();
        
        if (!response.TryGetValueOfHeader(TusHeaders.TusResumable, out var tusVersion)
            || tusVersion is null)
        {
            throw new IOException("Invalid TusResumable header");
        }
        
        if (!response.TryGetValueOfHeader(TusHeaders.UploadOffset, out var uploadOffsetString)
            || uploadOffsetString is null)
        {
            throw new IOException("Invalid UploadOffset header");
        }
        var uploadOffset = long.Parse(uploadOffsetString);
        
        if (!response.TryGetValueOfHeader(TusHeaders.UploadOffset, out var uploadLengthString)
            || uploadLengthString is null
            || !long.TryParse(uploadLengthString, out var uploadLength))
        {
            uploadLength = -1;
        }

        return new TusHeadResponse
        {
            OriginHttpRequestMessage = httpReqMsg,
            OriginResponseMessage = response,
            TusResumableVersion = tusVersion,
            UploadOffset = uploadOffset,
            UploadLength = uploadLength
        };
    }

     
    private void AddCustomHeaders(HttpRequestMessage httpRequestMessage)
    {
        ValidateHttpHeaders();
        if (!UploadOption.CustomHttpHeaders.Any()) return;
        foreach (var key in UploadOption.CustomHttpHeaders.Keys)
        {
            httpRequestMessage.Headers.Add(key, UploadOption.CustomHttpHeaders[key]);
        }
    }

    private void ValidateHttpHeaders()
    {
        if (!UploadOption.CustomHttpHeaders.Any()) return;
        var reveredWords =
            UploadOption.CustomHttpHeaders.Keys.Where(headerKey => TusHeaders.TusReservedWords.Contains(headerKey.ToLower()))
                .ToList();
        if (reveredWords.Any())
        {
            throw new ArgumentException(
                $"HttpHeader can not contain tus Reserved word : {JsonSerializer.Serialize(reveredWords)}");
        }
    }
    
     public void Dispose()
     {
         GC.SuppressFinalize(this);
     }
    
     protected virtual void Dispose(bool disposing)
     {
         if (!_disposedValue)
         {
             if (disposing)
             {
                 //Todo dispose handles
                 _httpClient.Dispose();
                 _httpClient = null!;
             }
             _disposedValue = true;
         }
     }
     
     

}