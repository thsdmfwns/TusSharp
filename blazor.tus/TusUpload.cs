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
    private HttpClient _httpClient = new();

    public async Task Start()
    {
        var delays = new Queue<int>(UploadOption.RetryDelays);
        while (!CancellationToken.IsCancellationRequested)
        {
            try
            {
                SetHttpDefaultHeader();
                if (UploadOption.UploadUrl is null) await TusCreateAsync();
                var uploadOffset = await TusHeadAsync();
                await TusPatchAsync(UploadOption.UploadUrl!, uploadOffset);
                return;
            }
            catch (TusException exception)
            {
                UploadOption.OnFailed?.Invoke(exception.OriginalResponseMessage, exception.OriginalRequestMessage,
                    exception.Message);
                if (delays.TryDequeue(out var delay)
                    && (UploadOption.OnShouldRetry?.Invoke(exception.OriginalResponseMessage,
                        exception.OriginalRequestMessage, delay) ?? true))
                {
                    await Task.Delay(delay);
                    continue;
                }

                throw;
            }
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

    private async Task TusCreateAsync()
    {
        HttpRequestMessage? httpRequestMessage = null;
        HttpResponseMessage? httpResponseMessage = null;
        try
        {
            var uploadLength = FileStream.Length;
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

            httpResponseMessage = await _httpClient.SendAsync(httpRequestMessage, CancellationToken);
            if (httpResponseMessage.StatusCode != HttpStatusCode.Created)
            {
                throw new HttpRequestException($"Tus upload failed with http status code : {httpResponseMessage.StatusCode}");
            }

            Uri? fileUrl;
            if (!httpResponseMessage.TryGetValueOfHeader(TusHeaders.Location, out var fileUrlStr)
                || fileUrlStr is null
                || !Uri.TryCreate(fileUrlStr, UriKind.RelativeOrAbsolute, out fileUrl))
            {
                throw new ArgumentException("Invalid location header");
            }

            if (!fileUrlStr.StartsWith("https://") && !fileUrlStr.StartsWith("http://"))
            {
                fileUrl = new Uri(endpoint, fileUrl);
            }

            if (!httpResponseMessage.TryGetValueOfHeader(TusHeaders.TusResumable, out var tusVersion)
                || tusVersion is null)
            {
                throw new ArgumentException("Invalid TusResumable header");
            }

            UploadOption.UploadUrl = fileUrl;
        }
        catch (Exception exception)
        {
            throw new TusException(exception.Message, exception, httpRequestMessage, httpResponseMessage);
        }
    }

    private async Task<long> TusHeadAsync()
    {
        HttpRequestMessage? httpRequestMessage = null;
        HttpResponseMessage? httpResponseMessage = null;
        try
        {
            httpRequestMessage = new HttpRequestMessage(HttpMethod.Head, UploadOption.UploadUrl!);
            httpResponseMessage = await _httpClient.SendAsync(httpRequestMessage, CancellationToken);
            if (!httpResponseMessage.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Tus upload failed with http status code : {httpResponseMessage.StatusCode}");
            }

            if (!httpResponseMessage.TryGetValueOfHeader(TusHeaders.TusResumable, out var tusVersion)
                || tusVersion is null)
            {
                throw new ArgumentException("Invalid TusResumable header");
            }

            if (!httpResponseMessage.TryGetValueOfHeader(TusHeaders.UploadOffset, out var uploadOffsetString)
                || uploadOffsetString is null
                || !long.TryParse(uploadOffsetString, out var uploadOffset))
            {
                throw new ArgumentException("Invalid UploadOffset header");
            }

            if (!httpResponseMessage.TryGetValueOfHeader(TusHeaders.UploadOffset, out var uploadLengthString)
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

    private async Task TusPatchAsync(Uri fileLocation, long uploadOffset)
    {
        HttpRequestMessage? httpRequestMessage = null;
        HttpResponseMessage? httpResponseMessage = null;
        try
        {
            var pipereader = PipeReader.Create(FileStream);
            var totalSize = FileStream.Length;
            var uploadedSize = uploadOffset;

            if (uploadedSize != FileStream.Position)
            {
                FileStream.Seek(uploadOffset, SeekOrigin.Begin);
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
                    var chunkSize = slicedBuffer.Length;

                    httpRequestMessage = new HttpRequestMessage(new HttpMethod("PATCH"), fileLocation);
                    httpRequestMessage.Headers.Add(TusHeaders.UploadOffset, uploadedSize.ToString());
                    if (uploadOffset < 0)
                    {
                        httpRequestMessage.Headers.Add(TusHeaders.UploadLength, totalSize.ToString());
                    }

                    httpRequestMessage.Content = new ByteArrayContent(slicedBuffer.ToArray());
                    httpRequestMessage.Content.Headers.Add(TusHeaders.ContentType, TusHeaders.UploadContentTypeValue);
                    httpResponseMessage = await _httpClient.SendAsync(httpRequestMessage, CancellationToken);
                    if (!httpResponseMessage.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"Tus upload failed with http status code : {httpResponseMessage.StatusCode} \n");
                    }

                    uploadedSize += chunkSize;
                    UploadOption.OnProgress?.Invoke(chunkSize, uploadedSize, totalSize);
                }

                if (!result.IsCompleted) continue;
                UploadOption.OnCompleted?.Invoke();
                break;
            }
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
        if (_disposedValues) return;
        if (disposing)
        {
            _httpClient.Dispose();
            _httpClient = null;
        }

        _disposedValues = true;
    }



}