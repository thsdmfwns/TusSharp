namespace TusSharp;

public class TusOptionResponse
{
    public string? TusResumable { get; set; }
    public List<string> TusVersion { get; set; } = new List<string>();
    public long? TusMaxSize { get; set; }
    public List<string>? TusExtension { get; set; }
}