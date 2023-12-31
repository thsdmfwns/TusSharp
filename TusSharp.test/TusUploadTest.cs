using System.Security.Cryptography;
using System.Text;
using Bogus;

namespace TusSharp.Test;

public class TusUploadTest
{
    private Faker _faker;
    private readonly string _dataPath = "/mt/nvme1n1p2/tusd-data";
    private readonly Uri _endpoint = new("http://172.17.0.3:1080/files");
    [SetUp]
    public void Setup()
    {
        _faker = new Faker();
    }

    private string Hash(byte [] temp)
    {
        using (SHA256Managed sha256Managed = new SHA256Managed())
        {
            var hash = sha256Managed.ComputeHash(temp);
            return Convert.ToBase64String(hash);
        }
    }

    [Test]
    [TestCase(1)]
    [TestCase(10)]
    [TestCase(100)]
    [TestCase(1000)]
    public async Task UploadStart(long chunkSize)
    {
        var client = new TusClient();
        string? err = null;
        var opt = new TusUploadOption()
        {
            EndPoint = _endpoint,
            ChunkSize = chunkSize,
            RetryDelays = null,
            OnFailed = (message, requestMessage, arg3, ex) => { err = ex!.ToString();}, 
        };
        var fileContent = _faker.Lorem.Paragraphs();
        var fileByte = Encoding.UTF8.GetBytes(fileContent);
        var stream = new MemoryStream(fileByte);
        using var upload = client.Upload(opt, stream);
        await upload.Start();   
        TestContext.WriteLine(err);
        Assert.That(err, Is.Null);
        var id = opt.UploadUrl!.Segments.Last();
        var file = new FileInfo(Path.Combine(_dataPath, id));
        Assert.That(file.Exists, Is.True);
        var uploaded = await File.ReadAllBytesAsync(file.FullName);
        Assert.That(Hash(uploaded), Is.EqualTo(Hash(fileByte)));
    }
    
    [Test]
    public async Task UploadStartBigFile()
    {
        var client = new TusClient();
        string? err = null;
        var opt = new TusUploadOption()
        {
            EndPoint = _endpoint,
            ChunkSize = 1 * 1024 * 1024,
            RetryDelays = null,
            OnFailed = (message, requestMessage, arg3, ex) => { err = ex!.ToString();},
        };
        using var stream = File.OpenRead("/home/son/test.mp4");
        using var upload = client.Upload(opt, stream);
        await upload.Start();   
        TestContext.WriteLine(err);
        Assert.That(err, Is.Null);
        var id = opt.UploadUrl!.Segments.Last();
        var file = new FileInfo(Path.Combine(_dataPath, id));
        Assert.That(file.Exists, Is.True);
        var data = await File.ReadAllBytesAsync("/home/son/test.mp4");
        var uploaded = await File.ReadAllBytesAsync(file.FullName);
        Assert.That(Hash(uploaded), Is.EqualTo(Hash(data)));
    }

    [Test]
    public async Task UploadResume()
    {
        var client = new TusClient();
        var err = string.Empty;
        var token = new CancellationTokenSource();
        var opt = new TusUploadOption()
        {
            EndPoint = _endpoint,
            ChunkSize = 10,
            RetryDelays = null,
            OnFailed = (message, requestMessage, arg3, ex) => { err += $"{ex}";},
            OnProgress = (chunkSize, uploaded, total) => {
                if (total/3 < uploaded)
                {
                    token.Cancel();  
                }
            }
        };
        var fileContent = _faker.Lorem.Paragraphs();
        var fileByte = Encoding.UTF8.GetBytes(fileContent);
        var stream = new MemoryStream(fileByte);
        using var upload = client.Upload(opt, stream);
        {
            await upload.Start(token.Token);
        }
        Assert.That(opt.UploadUrl, Is.Not.Null);
        Assert.That(err, Is.Empty);

        err = string.Empty;
        using var reUpload = client.Upload(opt, new MemoryStream(Encoding.UTF8.GetBytes(fileContent)));
        {
            await reUpload.Start();
        }
        Assert.That(err, Is.Empty);
        var id = opt.UploadUrl!.Segments.Last();
        var file = new FileInfo(Path.Combine(_dataPath, id));
        Assert.That(file.Exists, Is.True);
        var uploaded = await File.ReadAllBytesAsync(file.FullName);
        Assert.That(await File.ReadAllTextAsync(file.FullName), Is.EqualTo(fileContent));
        Assert.That(Hash(uploaded), Is.EqualTo(Hash(fileByte)));
    }
    
    [Test]
    public async Task UploadBigFileResume()
    {
        var client = new TusClient();
        var err = string.Empty;
        var token = new CancellationTokenSource();
        var opt = new TusUploadOption()
        {
            EndPoint = _endpoint,
            ChunkSize = 1 * 1024 * 1024,
            RetryDelays = null,
            OnFailed = (message, requestMessage, arg3, ex) => { err += $"{ex}";},
            OnProgress = (chunkSize, uploaded, total) => {
                if (total/3 < uploaded)
                {
                    token.Cancel();  
                }
            }
        };

        using (var stream = File.OpenRead("/home/son/test.mp4"))
        {
            using var upload = client.Upload(opt, stream);
            {
                await upload.Start();
            }
            Assert.That(opt.UploadUrl, Is.Not.Null);
            Assert.That(err, Is.Empty);
        }

        err = string.Empty;
        using (var restream = File.OpenRead("/home/son/test.mp4"))
        {
            using var reUpload = client.Upload(opt, restream);
            {
                await reUpload.Start(token.Token);
            }
        }
        Assert.That(err, Is.Empty);
        var id = opt.UploadUrl!.Segments.Last();
        var file = new FileInfo(Path.Combine(_dataPath, id));
        Assert.That(file.Exists, Is.True);
        var data = await File.ReadAllBytesAsync("/home/son/test.mp4");
        var uploaded = await File.ReadAllBytesAsync(file.FullName);
        Assert.That(Hash(uploaded), Is.EqualTo(Hash(data)));
    }

    [Test]
    public async Task NoVersion()
    {
        var client = new TusClient();
        Exception? err = null;
        var opt = new TusUploadOption()
        {
            EndPoint = _endpoint,
            ChunkSize = 10,
            TusVersion = string.Empty,
            RetryDelays = null,
            OnFailed = (message, requestMessage, arg3, ex) => { err = ex;}, 
        };
        var fileContent = _faker.Lorem.Paragraphs();
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));
        using (var upload = client.Upload(opt,stream))
        {
            await upload.Start();
        }
        Assert.That(err, Is.Not.Null);
        Assert.That(err is HttpRequestException, Is.True);
    }
    
    [Test]
    public async Task ShouldReTry()
    {
        var client = new TusClient();
        var retryCount = 0;
        var completedCount = 0;
        var opt = new TusUploadOption()
        {
            EndPoint = _endpoint,
            ChunkSize = 10,
            TusVersion = string.Empty,
            OnShouldRetry = (message, requestMessage, arg3) =>
            {
                retryCount++;
                return true;
            }, 
            OnCompleted = () => ++completedCount,
        };
        var fileContent = _faker.Lorem.Paragraphs();
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));
        using (var upload = client.Upload(opt, stream))
        {
            await upload.Start();
        }
        Assert.That(retryCount, Is.EqualTo(opt.RetryDelays!.Count));
        Assert.That(completedCount, Is.EqualTo(1));
    }
    
    [Test]
    public async Task NoReTry()
    {
        var client = new TusClient();
        var retryCount = 0;
        var opt = new TusUploadOption()
        {
            EndPoint = _endpoint,
            ChunkSize = 10,
            TusVersion = string.Empty,
            OnShouldRetry = (message, requestMessage, arg3) =>
            {
                retryCount++;
                return false;
            } 
        };
        var fileContent = _faker.Lorem.Paragraphs();
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));
        using (var upload = client.Upload(opt, stream))
        {
            await upload.Start();
        }
        Assert.That(retryCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Delete()
    {
        var client = new TusClient();
        var err = string.Empty;
        var token = new CancellationTokenSource();
        var opt = new TusUploadOption()
        {
            EndPoint = _endpoint,
            ChunkSize = 10,
            RetryDelays = null,
            OnFailed = (message, requestMessage, arg3, ex) => { err += $"{ex}"; },
            OnProgress = (chunkSize, uploaded, total) =>
            {
                if (total / 3 < uploaded)
                {
                    token.Cancel();
                }
            }
        };
        var fileContent = _faker.Lorem.Paragraphs();
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));
        using var upload = client.Upload(opt, stream);
        await upload.Start(token.Token);
        Assert.That(opt.UploadUrl, Is.Not.Null);
        Assert.That(err, Is.Empty);
        var id = opt.UploadUrl!.Segments.Last();
        var file = new FileInfo(Path.Combine(_dataPath, id));
        Assert.That(file.Exists, Is.True);

        await upload.Delete();
        Assert.That(err, Is.Empty);
        file = new FileInfo(Path.Combine(_dataPath, id));
        Assert.That(file.Exists, Is.False);
    }

    [Test]
    public async Task Option()
    {
        var client = new TusClient();
        var endPoint = _endpoint;
        var res = await client.SendOption(endPoint);
        Assert.That(res.TusVersion, Does.Contain("1.0.0"));
        Assert.That(res.TusExtension, Is.Not.Empty);
    }
}