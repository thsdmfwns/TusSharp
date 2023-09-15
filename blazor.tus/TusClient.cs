using blazor.tus.Constants;
using blazor.tus.Infrastructure;

namespace blazor.tus;

public class TusClient
{
    public TusUpload Upload(TusUploadOption uploadOption)
        => new TusUpload(uploadOption);

    /// <summary>
    ///  OPTIONS request MAY be used to gather information about the Server’s current configuration
    /// </summary>
    /// <param name="endPoint"></param>
    /// <returns></returns>
    public async Task<TusOptionResponse> SendOption(Uri endPoint)
    {
        using var client = new HttpClient();
        var response = new TusOptionResponse();
        var req = new HttpRequestMessage(HttpMethod.Options, endPoint);
        var responseMessage = await client.SendAsync(req);
        if (!responseMessage.TryGetValueOfHeader(TusHeaders.TusVersion, out var tusVersion)
            || tusVersion is null)
        {
            throw new HttpRequestException("Invalid Tus-Version header");
        }
        response.TusVersion = tusVersion.Split(',').ToList();
        
        if (!responseMessage.TryGetValueOfHeader(TusHeaders.TusMaxSize, out var maxsizeString)
            || maxsizeString is null
            || !long.TryParse(maxsizeString, out var maxSize))
        {
            response.TusMaxSize = null;
        }
        else
        {
            response.TusMaxSize = maxSize;
        }
        
        if (!responseMessage.TryGetValueOfHeader(TusHeaders.TusExtension, out var tusExtension)
            || tusExtension is null)
        {
            response.TusExtension = null;
        }
        else
        {
            response.TusExtension = tusExtension.Split(',').ToList();
        }

        return response;
    }
}