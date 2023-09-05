using System.Buffers;
using System.Text;
using Bogus;

namespace blazor.tus.test;

public class TusUploadTest
{
    private Faker _faker;
    [SetUp]
    public void Setup()
    {
        _faker = new Faker();
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
            EndPoint = new Uri("http://172.17.0.3:1080/files"),
            ChunkSize = chunkSize,
            RetryDelays = null,
            OnFailed = (message, requestMessage, arg3, ex) => { err = ex?.ToString();}, 
        };
        var fileContent = _faker.Lorem.Paragraphs();
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));
        using var upload = client.Upload(stream, opt, null);
        await upload.Start();
        TestContext.WriteLine(err);
        Assert.That(err, Is.Null);
    }

    [Test]
    public async Task UploadResume()
    {
        var client = new TusClient();
        var err = string.Empty;
        var token = new CancellationTokenSource();
        var opt = new TusUploadOption()
        {
            EndPoint = new Uri("http://172.17.0.3:1080/files"),
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
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));
        using (var upload = client.Upload(stream, opt, token.Token))
        {
            await upload.Start();
        };   
        Assert.That(opt.UploadUrl, Is.Not.Null);
        Assert.That(err, Is.Empty);

        err = string.Empty;
        var resumeOpt = new TusUploadOption()
        {
            EndPoint = new Uri("http://172.17.0.3:1080/files"),
            UploadUrl = opt.UploadUrl,
            ChunkSize = 100,
            RetryDelays = null,
            OnFailed = (message, requestMessage, arg3, ex) => { err += $"{ex}:{arg3}\n";},
        };
        using (var upload = client.Upload(stream, resumeOpt, null))
        {
            await upload.Start();
        }
        Assert.That(err, Is.Empty);
    }

    [Test]
    public async Task NoVersion()
    {
        var client = new TusClient();
        Exception? err = null;
        var opt = new TusUploadOption()
        {
            EndPoint = new Uri("http://172.17.0.3:1080/files"),
            ChunkSize = 10,
            TusVersion = string.Empty,
            RetryDelays = null,
            OnFailed = (message, requestMessage, arg3, ex) => { err = ex;}, 
        };
        var fileContent = _faker.Lorem.Paragraphs();
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));
        using (var upload = client.Upload(stream, opt, null))
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
        var opt = new TusUploadOption()
        {
            EndPoint = new Uri("http://172.17.0.3:1080/files"),
            ChunkSize = 10,
            TusVersion = string.Empty,
            OnShouldRetry = (message, requestMessage, arg3) =>
            {
                retryCount++;
                return true;
            } 
        };
        var fileContent = _faker.Lorem.Paragraphs();
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));
        using (var upload = client.Upload(stream, opt, null))
        {
            await upload.Start();
        }
        Assert.That(retryCount, Is.EqualTo(opt.RetryDelays!.Count));
    }
    
    [Test]
    public async Task NoReTry()
    {
        var client = new TusClient();
        var retryCount = 0;
        var opt = new TusUploadOption()
        {
            EndPoint = new Uri("http://172.17.0.3:1080/files"),
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
        using (var upload = client.Upload(stream, opt, null))
        {
            await upload.Start();
        }
        Assert.That(retryCount, Is.EqualTo(1));
    }
}