namespace blazor.tus;

public class TusClient
{
    public TusUpload Upload(Stream fileStream, TusUploadOption uploadOption, CancellationToken? cancellationToken)
        => new TusUpload(fileStream, uploadOption, cancellationToken);
}