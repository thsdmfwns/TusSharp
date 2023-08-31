using System.Text;
using blazor.tus.Constants;

namespace blazor.tus;

public class TusUploadOption
{

    public required Uri EndPoint { get; set; }
    
    /// <summary>
    /// A URL which will be used to directly attempt a resume without creating an upload first.
    /// <para>Only if the resume attempt fails it will fall back to creating a new upload using the URL specified in the endpoint option.</para>
    /// <para>Using this option may be necessary if the server is automatically creating upload resources for you</para>
    /// </summary>
    public Uri? UploadUrl { get; set; } 

    /// <summary>
    /// An array or null, indicating how many milliseconds should pass before the next attempt to uploading will be started after the transfer has been interrupted.
    /// <para>Default value: [0, 1000, 3000, 5000]</para>
    /// 
    /// </summary>
    public List<int> RetryDelays { get; set; } = new List<int> { 0, 1000, 3000, 5000 };

    public string TusVersion { get; set; } = Constants.TusVersion.V1;

    /// <summary>
    /// A number indicating the maximum size of a PATCH request body in bytes.
    /// <para>Null means that client will try to upload the entire file in one request.</para> 
    /// <para>Default value: 10MB</para> 
    /// </summary>
    public long? ChunkSize { get; set; } = 10 * 1024 * 1024; //10MB

    public bool IsUploadDeferLength { get; set; } = false;

    public Dictionary<string, string> CustomHttpHeaders { get; set; } = new Dictionary<string, string>();

    public Dictionary<string, string> MetaData { get; set; } = new Dictionary<string, string>();
    
    public string SerializedMetaData => SerializeMetaData();
    
    /// <summary>
    /// invoke when client send Chunk
    /// <typeparam name="left Long">chunk Size</typeparam>
    /// <typeparam name="middle Long">uploaded size</typeparam>
    /// <typeparam name="right Long">total Size</typeparam>
    /// </summary>
    public Action<long, long, long>? OnProgress { get; set; }


    /// <summary>
    /// invoke when invoke client failed send chunk
    /// <typeparam name="HttpResponseMessage">original Response Message</typeparam>
    /// <typeparam name="HttpRequestMessage">original Request Message</typeparam>
    /// <typeparam name="string">error Message</typeparam>
    /// </summary>
    public Action<HttpResponseMessage, HttpRequestMessage, string>? OnFailed
    {
        get;
        set;
    }

    /// <summary>
    /// invoke when complete uploading
    /// </summary>
    public Action? OnCompleted { get; set; }
    
    /// <summary>
    /// invoke once an error appears and before retrying.
    /// <typeparam name="HttpResponseMessage">original Response Message</typeparam>
    /// <typeparam name="HttpRequestMessage">original Request Message</typeparam>
    /// <typeparam name="int">retry Attempt</typeparam>
    /// <typeparam name="bool">return value</typeparam>
    /// <returns>function must return true if the request should be retried.</returns>
    /// </summary>
    public Func<HttpResponseMessage, HttpRequestMessage, int, bool>? OnShouldRetry { get; set; }


    private string SerializeMetaData()
    {
        string[] meta = new string[MetaData.Count];
        int index = 0;
        foreach (var item in MetaData)
        {
            string key = item.Key;
            string value = Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Value));
            meta[index++] = $"{key} {value}";
        }

        return string.Join(",", meta);
    }
}