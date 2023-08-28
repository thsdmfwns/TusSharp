namespace blazor.tus;

public class TusClient : IDisposable
{
    private bool _disposedValue;

    
    
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
            }
            _disposedValue = true;
        }
    }
}