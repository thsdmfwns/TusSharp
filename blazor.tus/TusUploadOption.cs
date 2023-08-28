using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using blazor.tus.Constants;

namespace blazor.tus.Request;

public class TusUploadOption
{

    public required Uri EndPoint { get; set; }

    public List<int> RetryDelays { get; set; } = new List<int> { 0, 1000, 3000, 5000 };

    public TusVersion TusVersion { get; set; } = TusVersion.V1_0_0;

    public Dictionary<string, string> MetaData { get; set; } = new Dictionary<string, string>();
    public string SerializedMetaData => SerializeMetaData();

    public Action<(long uploadedSize, long totalSize)>? OnProgress { get; set; }

    public Dictionary<string, string> CustomHttpHeaders { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// invoke when appear a Exception
    /// </summary>
    public Action<(HttpResponseMessage originalResponseMessage, HttpRequestMessage originalRequestMessage)>? OnFailed
    {
        get;
        set;
    }

    /// <summary>
    /// invoke when complete uploading
    /// </summary>
    public Action? OnCompleted { get; set; }

    public bool IsUploadDeferLength { get; set; }

    /// <summary>
    /// invoke once an error appears and before retrying.
    /// </summary>
    public Func<(
        (HttpResponseMessage originalResponseMessage, HttpRequestMessage originalRequestMessage) err, int retryAttempt),
        bool>? OnShouldRetry { get; set; }


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