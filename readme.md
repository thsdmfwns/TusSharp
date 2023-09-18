# blazor.tus

<img alt="Tus logo" src="https://github.com/tus/tus.io/blob/main/public/images/tus1.png?raw=true" width="30%" align="right" />

> **tus** is a protocol based on HTTP for _resumable file uploads_. Resumable
> means that an upload can be interrupted at any moment and can be resumed without
> re-uploading the previous data again. An interruption may happen willingly, if
> the user wants to pause, or by accident in case of an network issue or server
> outage.

blazor.tus is a simple **.net** client for the [tus resumable upload protocol](http://tus.io) and can be used inside **.net applications** and **.net blazor**.

**Protocol version:** 1.0.0


## Example

in Console Application

```c#
using blazor.tus;

var client = new TusClient();
//Create file stream
const string filePath = "/path/to/test.mp4";
using var stream = File.OpenRead(filePath);
var fileInfo = new FileInfo(filePath);
//Create new option
var opt = new TusUploadOption()
{
    EndPoint = new Uri("http://localhost:1080/files"),
    ChunkSize = 1 * 1024 * 1024, //1MB
    RetryDelays = new List<int>{0, 3000, 5000, 10000, 20000},
    MetaData = new Dictionary<string, string>()
    {
        {"filename", fileInfo.Name}
    },
    OnCompleted = () =>
    {
        Console.WriteLine("completed upload \n");
    },
    OnFailed = (originalResponseMsg, originalRequestMsg, errMsg, exception) =>
    {
        Console.WriteLine("upload failed beacuse : {errMsg} \n");
    },
    OnProgress = (chunkSize, uploadedSize, totalSize) =>
    {
        var progressPercentage = (double)uploadedSize / totalSize * 100;
        Console.WriteLine($"upload | chunkSize : {chunkSize} | uploadedSize : {uploadedSize} | total : {totalSize} |  {progressPercentage:F2}\n");
    }
};
//Create upload with option
using var upload = client.Upload(opt);
//start the upload
await upload.Start(stream);   
```

in Blazor

```c#
@using Tewr.Blazor.FileReader
@inject IFileReaderService fileReaderService;

<input type="file" @ref=_inputTypeFileElement />
<button @onclick=upload>upload</button>

<h1>@ProgressPercentage%</h1>

<textarea style="max-width: 100%;" cols="60" rows="20">@_console</textarea>

@code
{   
    double ProgressPercentage { get; set; } = 0;

    TusClient _tusClient = new TusClient();

    private ElementReference _inputTypeFileElement;

    string _console = string.Empty;

    async Task upload()
    {
        ProgressPercentage = 0;
        var files = await fileReaderService.CreateReference(_inputTypeFileElement).EnumerateFilesAsync();
        var file = files.First();
        var fileInfo = await file.ReadFileInfoAsync();
        var opt = new TusUploadOption()
        {
            EndPoint = new Uri("http://localhost:1080/files"),
            ChunkSize = 1 * 1024 * 1024, //1MB 
            MetaData = new Dictionary<string, string>()
            {
                {"filename", fileInfo.Name}
            },
            OnCompleted = () =>
            {
                _console += "completed upload \n";
                StateHasChanged();
            },
            OnFailed = (originalResponseMsg, originalRequestMsg, errMsg, exception) =>
            {
                Console.Error.WriteLine(exception);
                _console += $"upload failed beacuse : {errMsg} \n";
                StateHasChanged();
            },
            OnProgress = (chunkSize, uploadedSize, totalSize) =>
            {
                ProgressPercentage = (double)uploadedSize / totalSize * 100;
                _console += $"upload | chunkSize : {chunkSize} | uploadedSize : {uploadedSize} | total : {totalSize} \n";
                StateHasChanged();
            }
        };
        using var upload = _tusClient.Upload(opt);
        await upload.StartWithFileReader(file);
    }
}
```

## Related solutions

[BlazorFileReader](https://github.com/Tewr/BlazorFileReader) : Used to get the file stream available to Blazor.

## License

This project is licensed under the MIT license.