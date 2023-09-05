using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using blazor.tus.Constants;
using blazor.tus.Execption;
using blazor.tus.Infrastructure;

namespace blazor.tus;

public class TusUpload : IDisposable
{
    public TusUpload(Stream fileStream, TusUploadOption uploadOption)
    {
        _fileStream = fileStream;
        UploadOption = uploadOption;
    }

    public readonly TusUploadOption UploadOption;
    public bool IsDisposed { get; private set; }

    private Stream _fileStream;
    private HttpClient _httpClient = new();

    public async Task Start(CancellationToken cancellationToken = default)
    {
        var delays = new Queue<int>(UploadOption.RetryDelays ?? new List<int>());
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetHttpDefaultHeader();
                if (UploadOption.UploadUrl is null) await TusCreateAsync(cancellationToken);
                var uploadOffset = await TusHeadAsync(cancellationToken);
                await TusPatchAsync(UploadOption.UploadUrl!, uploadOffset, cancellationToken);
                break;
            }
            catch (TusException exception)
            {
                UploadOption.OnFailed?.Invoke(exception.OriginalResponseMessage, exception.OriginalRequestMessage,
                    exception.Message, exception.InnerException);
                if (delays.TryDequeue(out var delay)
                    && (UploadOption.OnShouldRetry?.Invoke(exception.OriginalResponseMessage,
                        exception.OriginalRequestMessage, delay) ?? true))
                {
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
                break;
            }
        }
        UploadOption.OnCompleted?.Invoke();
    }

    public async Task Delete(CancellationToken cancellationToken = default)
    {
        try
        {
            if (UploadOption.UploadUrl is null)
            {
                UploadOption.OnFailed?.Invoke(null, null,
                    "UploadOption.UploadUrl is null.", new ArgumentNullException("UploadOption.UploadUrl"));
                return;
            }
            SetHttpDefaultHeader();
            await TusDeleteAsync(UploadOption.UploadUrl, cancellationToken);
        }
        catch (TusException exception)
        {
            UploadOption.OnFailed?.Invoke(exception.OriginalResponseMessage, exception.OriginalRequestMessage,
                exception.Message, exception.InnerException);
        }
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

    private async Task TusCreateAsync(CancellationToken cancellationToken)
    {
        HttpRequestMessage? httpRequestMessage = null;
        HttpResponseMessage? httpResponseMessage = null;
        try
        {
            var uploadLength = _fileStream.Length;
            var endpoint = UploadOption.EndPoint;
            httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, UploadOption.EndPoint);
            if (uploadLength > 0)
            {
                httpRequestMessage.Headers.Add(TusHeaders.UploadLength, uploadLength.ToString());
            }
            else
            {
                httpRequestMessage.Headers.Add(TusHeaders.UploadDeferLength, "1");
            }

            httpResponseMessage = await _httpClient.SendAsync(httpRequestMessage, cancellationToken);
            httpResponseMessage.EnsureSuccessStatusCode();
            httpResponseMessage.GetValueOfHeader(TusHeaders.TusResumable);
            
            Uri? fileUrl;
            if (!httpResponseMessage.TryGetValueOfHeader(TusHeaders.Location, out var fileUrlStr)
                || fileUrlStr is null
                || !Uri.TryCreate(fileUrlStr, UriKind.RelativeOrAbsolute, out fileUrl))
            {
                throw new InvalidHeaderException("Invalid location header");
            }

            if (!fileUrl.IsAbsoluteUri)
            {
                fileUrl = new Uri(endpoint, fileUrl);
            }

            UploadOption.UploadUrl = fileUrl;
        }
        catch (Exception exception)
        {
            throw new TusException(exception.Message, exception, httpRequestMessage, httpResponseMessage);
        }
    }

    private async Task<long> TusHeadAsync(CancellationToken cancellationToken)
    {
        HttpRequestMessage? httpRequestMessage = null;
        HttpResponseMessage? httpResponseMessage = null;
        try
        {
            httpRequestMessage = new HttpRequestMessage(HttpMethod.Head, UploadOption.UploadUrl!);
            httpResponseMessage = await _httpClient.SendAsync(httpRequestMessage, cancellationToken);
            httpResponseMessage.EnsureSuccessStatusCode();
            httpResponseMessage.GetValueOfHeader(TusHeaders.TusResumable);

            if (!httpResponseMessage.TryGetValueOfHeader(TusHeaders.UploadOffset, out var uploadOffsetString)
                || uploadOffsetString is null
                || !long.TryParse(uploadOffsetString, out var uploadOffset))
            {
                throw new InvalidHeaderException("Invalid UploadOffset header");
            }

            if (!httpResponseMessage.TryGetValueOfHeader(TusHeaders.UploadLength, out var uploadLengthString)
                || uploadLengthString is null
                || !long.TryParse(uploadLengthString, out var uploadLength))
            {
                uploadLength = -1;
            }

            return uploadOffset;

        }
        catch (Exception exception)
        {
            throw new TusException(exception.Message, exception, httpRequestMessage, httpResponseMessage);
        }
    }

    private async Task TusPatchAsync(Uri fileLocation, long uploadOffset, CancellationToken cancellationToken)
    {
        HttpRequestMessage? httpRequestMessage = null;
        HttpResponseMessage? httpResponseMessage = null;
        try
        {
            var pipereader = PipeReader.Create(_fileStream);
            var totalSize = _fileStream.Length;
            var uploadedSize = uploadOffset;
            var firstRequest = true;

            if (uploadedSize != _fileStream.Position)
            {
                _fileStream.Seek(uploadOffset, SeekOrigin.Begin);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                if (totalSize == uploadedSize)
                {
                    break;
                }

                var result = await pipereader.ReadAsync();
                var buffer = result.Buffer;
                while (TrySliceBuffer(ref buffer, out var slicedBuffer)
                       && !cancellationToken.IsCancellationRequested)
                {
                    var chunkSize = slicedBuffer.Length;

                    httpRequestMessage = new HttpRequestMessage(new HttpMethod("PATCH"), fileLocation);
                    httpRequestMessage.Headers.Add(TusHeaders.UploadOffset, uploadedSize.ToString());
                    if (uploadOffset < 0)
                    {
                        httpRequestMessage.Headers.Add(TusHeaders.UploadLength, totalSize.ToString());
                    }

                    httpRequestMessage.Content = new ByteArrayContent(slicedBuffer.ToArray());
                    httpRequestMessage.Content.Headers.Add(TusHeaders.ContentType, TusHeaders.UploadContentTypeValue);
                    httpResponseMessage = await _httpClient.SendAsync(httpRequestMessage, cancellationToken);
                    httpResponseMessage.EnsureSuccessStatusCode();
                    if (firstRequest)
                    {
                        firstRequest = false;
                        httpResponseMessage.GetValueOfHeader(TusHeaders.TusResumable);
                    }
                    uploadedSize += chunkSize;
                    UploadOption.OnProgress?.Invoke(chunkSize, uploadedSize, totalSize);
                }

                if (!result.IsCompleted) continue;
                break;
            }
        }
        catch (Exception exception)
        {
            throw new TusException(exception.Message, exception, httpRequestMessage, httpResponseMessage);
        }
    }
    
    private async Task TusDeleteAsync(Uri uploadUri, CancellationToken cancellationToken)
    {
        HttpRequestMessage? httpRequestMessage = null;
        HttpResponseMessage? httpResponseMessage = null;
        try
        {
            var httpReqMsg = new HttpRequestMessage(HttpMethod.Delete, uploadUri);
            var response = await _httpClient.SendAsync(httpReqMsg, cancellationToken);
            response.EnsureSuccessStatusCode();
            response.GetValueOfHeader(TusHeaders.TusResumable);
        }
        catch (Exception exception)
        {
            throw new TusException(exception.Message, exception, httpRequestMessage, httpResponseMessage);
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
            UploadOption.CustomHttpHeaders.Keys
                .Where(headerKey => TusHeaders.TusReservedWords.Contains(headerKey.ToLower()))
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
        if (IsDisposed) return;
        if (disposing)
        {
            _httpClient.Dispose();
            _httpClient = null;
        }

        IsDisposed = true;
    }



}