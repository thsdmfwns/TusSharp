using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BirdMessenger;
using blazor.tus.Request;
using blazor.tus.Response;

namespace blazor.tus;

public class TusUpload : IDisposable
{
    public TusUpload(
        Stream fileStream, 
        Uri endPoint,
        
        List<int>? retryDelays = null,
        Action<(long uploadedSize, long totalSize)>? onProgress = null,
        Action<(HttpResponseMessage originalResponseMessage, HttpRequestMessage originalRequestMessage)>? onFailed = null,
        Action? onCompleted = null,
        Func<((HttpResponseMessage originalResponseMessage, HttpRequestMessage originalRequestMessage) err, int retryAttempt), bool>? onShouldRetry = null)
    {
        FileStream = fileStream;
        EndPoint = endPoint;
        RetryDelays = retryDelays ?? new List<int> { 0, 1000, 3000, 5000 }; 
        OnProgress = onProgress;
        OnFailed = onFailed;
        OnCompleted = onCompleted;
        OnShouldRetry = onShouldRetry;
    }
    
    public Stream FileStream { get; set; }

    public Uri EndPoint { get; set; }

    public List<int> RetryDelays { get; set; }
    
    

    public Action<(long uploadedSize, long totalSize)>? OnProgress { get; set; }
    
    /// <summary>
    /// invoke when appear a Exception
    /// </summary>
    public Action<(HttpResponseMessage originalResponseMessage, HttpRequestMessage originalRequestMessage)>? OnFailed { get; set; }
    
    /// <summary>
    /// invoke when complete uploading
    /// </summary>
    public Action? OnCompleted{ get; set; }

    /// <summary>
    /// invoke once an error appears and before retrying.
    /// </summary>
    public Func<(
        (HttpResponseMessage originalResponseMessage, HttpRequestMessage originalRequestMessage) err, int retryAttempt), 
        bool>? OnShouldRetry { get; set; }

    private bool _disposedValue;
    private HttpClient _httpClient = new HttpClient();

     
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
     
     private string SerializeMetaData(Dictionary<string, string> metadata)
     {
         string[] meta = new string[metadata.Count];
         int index = 0;
         foreach (var item in metadata)
         {
             string key = item.Key;
             string value = Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Value));
             meta[index++] = $"{key} {value}";
         }
         return string.Join(",", meta);
     }

}