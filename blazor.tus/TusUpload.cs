using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using blazor.tus.Constants;
using blazor.tus.Infrastructure;
using blazor.tus.Internal;
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
    private bool _disposedValues;
    private HttpClient _httpClient = new HttpClient();

    public async Task Start()
    {
        SetHttpDefaultHeader();
        var create = await TusCreateAsync(); 
        var head = await TusHeadAsync(create.FileLocation);
        await TusPatchAsync(create.FileLocation, head.UploadOffset);
    }

    public async Task StartWithUploadUri()
    {
        SetHttpDefaultHeader();
        var uri = UploadOption.UploadUrl;
        var head = await TusHeadAsync(uri!);
        await TusPatchAsync(uri!, head.UploadOffset);
    }

    private void SetHttpDefaultHeader()
    {
        var defaultHeaders = _httpClient.DefaultRequestHeaders;
        defaultHeaders.Clear();
        
        defaultHeaders.Add(TusHeaders.TusResumable, UploadOption.TusVersion);
        
        var metaData = UploadOption.SerializedMetaData;
        if (!string.IsNullOrWhiteSpace(metaData))
        {
            defaultHeaders.Add(TusHeaders.UploadMetadata, metaData);
        }
        
        if (!UploadOption.CustomHttpHeaders.Any()) return;
        ValidateHttpHeaders();
        UploadOption.CustomHttpHeaders.ToList()
            .ForEach(x => defaultHeaders.Add(x.Key, x.Value));
    }

    private async Task<TusCreateResponse> TusCreateAsync()
    {
        var uploadLength = FileStream.Length;
        var endpoint = UploadOption.EndPoint;
        
        switch (UploadOption.IsUploadDeferLength)
        {
            case true when uploadLength > 0:
                throw new ArgumentException($"IsUploadDeferLength:[{UploadOption.IsUploadDeferLength}] can not set true if UploadLength:[{uploadLength}] is greater than zero");
            case false when uploadLength <= 0:
                throw new ArgumentException($"IsUploadDeferLength:[{UploadOption.IsUploadDeferLength}] can not set false if UploadLength:[{uploadLength}] is less than zero");
        }

        var httpReqMsg = new HttpRequestMessage(HttpMethod.Post, UploadOption.EndPoint);
        if (uploadLength > 0)
        {
            httpReqMsg.Headers.Add(TusHeaders.UploadLength, uploadLength.ToString());
        }
        else if(UploadOption.IsUploadDeferLength)
        {
            httpReqMsg.Headers.Add(TusHeaders.UploadDeferLength,"1");
        }

        var response = await _httpClient.SendAsync(httpReqMsg, CancellationToken);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new IOException($"Tus upload Create method failed with http status code : {response.StatusCode} \n");
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
    
    private async Task<TusHeadResponse> TusHeadAsync(Uri fileLocation)
    {
        var httpReqMsg = new HttpRequestMessage(HttpMethod.Head, fileLocation);
        var response = await _httpClient.SendAsync(httpReqMsg, CancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"Tus upload HEAD method failed with http status code : {response.StatusCode} \n");
        }
        
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

    private async Task TusPatchAsync(Uri fileLocation, long uploadOffset)
    {
        var pipereader = PipeReader.Create(FileStream);
        var totalSize = FileStream.Length;
        long uploadedSize;
        HttpRequestMessage httpReqMsg = new HttpRequestMessage(new HttpMethod("PATCH"), fileLocation);
        HttpResponseMessage response;

        uploadedSize = uploadOffset;
        if (uploadedSize != FileStream.Position)
        {
            FileStream.Seek(uploadOffset, SeekOrigin.Begin);
        }

        if (uploadOffset < 0)
        {
            httpReqMsg.Headers.Add(TusHeaders.UploadLength, totalSize.ToString());
        }

        while (!CancellationToken.IsCancellationRequested)
        {
            if (totalSize == uploadedSize)
            {
                break;
            }

            var result = await pipereader.ReadAsync();
            var buffer = result.Buffer;
            while (TrySliceBuffer(ref buffer, out var slicedBuffer))
            {
                var delays = new Queue<int>(UploadOption.RetryDelays);
                var chunkSize = slicedBuffer.Length;
                var curentTryCount = 1;
                var totalTryCount = UploadOption.RetryDelays.Count + 1;
                while (!CancellationToken.IsCancellationRequested)
                {
                    httpReqMsg.Headers.Remove(TusHeaders.UploadOffset);
                    httpReqMsg.Headers.Add(TusHeaders.UploadOffset, uploadedSize.ToString());
                    httpReqMsg.Content = new ByteArrayContent(slicedBuffer.ToArray());
                    httpReqMsg.Content.Headers.Add(TusHeaders.ContentType, TusHeaders.UploadContentTypeValue);
                    response = await _httpClient.SendAsync(httpReqMsg, CancellationToken);

                    var success = true;
                    if (!response.IsSuccessStatusCode)
                    {
                        var errMessage =
                            $"[TUS] ({curentTryCount}/{totalTryCount}) PATCH method failed with http status code : {response.StatusCode}";
                        UploadOption.OnFailed?.Invoke(response, httpReqMsg, errMessage);
                        success = false;
                    }

                    if (!response.TryGetValueOfHeader(TusHeaders.TusResumable, out var tusVersion)
                        || tusVersion is null)
                    {
                        var errMessage =
                            $"[TUS] ({curentTryCount}/{totalTryCount}) Invalid header : {TusHeaders.TusResumable} = {tusVersion ?? "NULL"}";
                        UploadOption.OnFailed?.Invoke(response, httpReqMsg, errMessage);
                        success = false;
                    }

                    long offset = -1;
                    if (!response.TryGetValueOfHeader(TusHeaders.UploadOffset, out var offsetString)
                        || offsetString is null
                        || !long.TryParse(offsetString, out offset)
                        || offset < 0)
                    {
                        var errMessage =
                            $"[TUS] ({curentTryCount}/{totalTryCount}) Invalid header : {TusHeaders.UploadOffset} = {offsetString ?? "NULL"} \n";
                        UploadOption.OnFailed?.Invoke(response, httpReqMsg, errMessage);
                        success = false;
                    }

                    if (!success)
                    {
                        var delay = delays.Dequeue();
                        if ((UploadOption.OnShouldRetry?.Invoke(response, httpReqMsg, delay) ?? true)
                            && delays.Count <= 0)
                        {
                            return;
                        }

                        await Task.Delay(delay);
                        curentTryCount++;
                        continue;
                    }

                    uploadedSize = offset;
                    break;
                }

                UploadOption.OnProgress?.Invoke(chunkSize, uploadedSize, totalSize);
            }

            if (!result.IsCompleted) continue;
            UploadOption.OnCompleted?.Invoke();
            break;
        }
    }

    private bool TrySliceBuffer(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> slicedBuffer)
    {
        if (buffer.IsEmpty)
        {
            slicedBuffer = default;
            return false;
        }
        var chunkSize = UploadOption.ChunkSize;
        if (chunkSize is null || buffer.Length <= chunkSize)
        {
            slicedBuffer = buffer;
            buffer = default;
            return true;
        }
        
        slicedBuffer = buffer.Slice(0, chunkSize.Value);
        buffer = buffer.Slice(buffer.GetPosition(chunkSize.Value));
        return true;
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
         Dispose(true);
         GC.SuppressFinalize(this);
     }
    
     protected virtual void Dispose(bool disposing)
     {
         if (_disposedValues) return;
         if (disposing)
         {
             _httpClient.Dispose();
             _httpClient = default;
         }
         _disposedValues = true;
     }
     
     

}