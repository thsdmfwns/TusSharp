namespace blazor.tus.Execption;

public class TusException : Exception
{
    public HttpRequestMessage? OriginalRequestMessage;
    public HttpResponseMessage? OriginalResponseMessage;
    
    public TusException(string message, Exception inner, HttpRequestMessage? originalRequestMessage, HttpResponseMessage? originalResponseMessage) : base(message, inner)
    {
        OriginalRequestMessage = originalRequestMessage;
        OriginalResponseMessage = originalResponseMessage;
    }
}