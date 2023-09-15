using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using blazor.tus.Constants;
using blazor.tus.Execption;
using blazor.tus.Infrastructure;
using Tewr.Blazor.FileReader;

namespace blazor.tus;

public class TusUpload : IDisposable
{
    public TusUpload(TusUploadOption uploadOption)
    {
        UploadOption = uploadOption;
    }

    public readonly TusUploadOption UploadOption;
    public bool IsDisposed { get; private set; }

    private HttpClient _httpClient = new();

    public async Task StartWithFileReader(IFileReference file, CancellationToken cancellationToken = default)
    {
        var delays = new Queue<int>(UploadOption.RetryDelays ?? new List<int>());
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var stream = await file.OpenReadAsync();
                SetHttpDefaultHeader();
                if (UploadOption.UploadUrl is null)
                {
                    UploadOption.UploadUrl = await TusCreateAsync(stream.Length, cancellationToken);
                }
                if (!UploadOption.UploadUrl!.IsAbsoluteUri)
                    UploadOption.UploadUrl = new Uri(UploadOption.EndPoint, UploadOption.UploadUrl);
                var uploadOffset = await TusHeadAsync(cancellationToken);
                await TusPatchWithFileReader(stream, uploadOffset, cancellationToken);
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

    public async Task Start(Stream fileStream, CancellationToken cancellationToken = default)
    {
        var delays = new Queue<int>(UploadOption.RetryDelays ?? new List<int>());
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetHttpDefaultHeader();
                if (UploadOption.UploadUrl is null)
                    UploadOption.UploadUrl = await TusCreateAsync(fileStream.Length, cancellationToken);
                if (!UploadOption.UploadUrl!.IsAbsoluteUri)
                    UploadOption.UploadUrl = new Uri(UploadOption.EndPoint, UploadOption.UploadUrl);
                var uploadOffset = await TusHeadAsync(cancellationToken);
                if (uploadOffset != fileStream.Position)
                {
                    fileStream.Seek(uploadOffset, SeekOrigin.Begin);
                }
                await TusPatchAsync(UploadOption.UploadUrl!, fileStream.Length, uploadOffset,
                    PipeReader.Create(fileStream), cancellationToken);
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

    private async Task TusPatchWithFileReader(AsyncDisposableStream stream, long uploadOffset, CancellationToken cancellationToken)
    {
        if (uploadOffset != stream.Position)
        {
            stream.Seek(uploadOffset, SeekOrigin.Begin);
        }
        var opt = new PipeOptions(minimumSegmentSize: 100 * 1024);
        var pipe = new Pipe(opt);
        Task writing = FillPipeAsync(pipe.Writer, stream, cancellationToken);
        Task reading = TusPatchAsync(UploadOption.UploadUrl!, stream.Length, uploadOffset,
            pipe.Reader, cancellationToken);
        await Task.WhenAll(reading, writing);
    }

    private async Task FillPipeAsync(PipeWriter pipeWriter, AsyncDisposableStream stream, CancellationToken cancellationToken)
    {
        await stream.CopyToAsync(pipeWriter, cancellationToken);
        await pipeWriter.FlushAsync();
        await pipeWriter.CompleteAsync();
    }

    private async Task<Uri> TusCreateAsync(long uploadLength,CancellationToken cancellationToken)
    {
        HttpRequestMessage? httpRequestMessage = null;
        HttpResponseMessage? httpResponseMessage = null;
        try
        {
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

            return fileUrl;
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

    private async Task TusPatchAsync(Uri fileLocation, long fileSize, long uploadOffset, PipeReader pipereader, CancellationToken cancellationToken)
    {
        HttpRequestMessage? httpRequestMessage = null;
        HttpResponseMessage? httpResponseMessage = null;
        try
        {
            var uploadedSize = uploadOffset;
            var firstRequest = true;
            while (!cancellationToken.IsCancellationRequested && fileSize != uploadedSize)
            {
                ReadResult result;
                if (fileSize < uploadedSize + UploadOption.ChunkSize )
                {
                    result = await pipereader.ReadAtLeastAsync((int)(fileSize - uploadedSize) ,cancellationToken);
                }
                else
                {
                    result = await pipereader.ReadAtLeastAsync((int)UploadOption.ChunkSize ,cancellationToken);
                }
                var buffer = result.Buffer;
                var isLast = false;
                while (TrySliceBuffer(ref buffer, ref isLast, out var slicedBuffer)
                       && !cancellationToken.IsCancellationRequested)
                {
                    var chunkSize = slicedBuffer.Length;
                    httpRequestMessage = new HttpRequestMessage(new HttpMethod("PATCH"), fileLocation);
                    httpRequestMessage.Headers.Add(TusHeaders.UploadOffset, uploadedSize.ToString());
                    if (uploadOffset < 0)
                    {
                        httpRequestMessage.Headers.Add(TusHeaders.UploadLength, fileSize.ToString());
                    }
                    var chunk = slicedBuffer.ToArray();
                    httpRequestMessage.Content = new ByteArrayContent(chunk);
                    httpRequestMessage.Content.Headers.Add(TusHeaders.ContentType, TusHeaders.UploadContentTypeValue);
                    httpResponseMessage = await _httpClient.SendAsync(httpRequestMessage, cancellationToken);
                    httpResponseMessage.EnsureSuccessStatusCode();
                    if (firstRequest)
                    {
                        firstRequest = false;
                        httpResponseMessage.GetValueOfHeader(TusHeaders.TusResumable);
                    }
                    uploadedSize += chunkSize;
                    UploadOption.OnProgress?.Invoke(chunkSize, uploadedSize, fileSize);
                }
                pipereader.AdvanceTo(buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (Exception exception)
        {
            throw new TusException(exception.Message, exception, httpRequestMessage, httpResponseMessage);
        }
        finally
        {
            await pipereader.CompleteAsync();
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


    private bool TrySliceBuffer(ref ReadOnlySequence<byte> buffer, ref bool isLast, out ReadOnlySequence<byte> slicedBuffer)
    {
        if (isLast)
        {
            slicedBuffer = ReadOnlySequence<byte>.Empty;
            return false;
        }

        var chunkSize = UploadOption.ChunkSize;
        if (buffer.Length <= chunkSize)
        {
            slicedBuffer = buffer;
            isLast = true;
            return true;
        }

        slicedBuffer = buffer.Slice(0, chunkSize);
        buffer = buffer.Slice(buffer.GetPosition(chunkSize));
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