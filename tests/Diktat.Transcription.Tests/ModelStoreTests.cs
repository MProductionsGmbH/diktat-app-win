using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Diktat.Transcription.Models;

namespace Diktat.Transcription.Tests;

public class ModelStoreTests
{
    private static readonly byte[] First = RandomBytes(300_000, 1);
    private static readonly byte[] Second = RandomBytes(70_000, 2);

    private static ModelInfo TestModel(string? firstSha = null) => new(
        "test-model",
        "Testmodell",
        ModelEngine.Whisper,
        "",
        "",
        [
            new("first.bin", "https://models.example.test/first.bin", First.Length, firstSha ?? Sha(First)),
            new("second.bin", "https://models.example.test/second.bin", Second.Length, Sha(Second)),
        ]);

    [Fact]
    public async Task DownloadsAllFilesAndReportsProgress()
    {
        var server = new FakeServer();
        var store = NewStore(server, out var root);
        var model = TestModel();
        var reports = new ConcurrentQueue<double>();

        Assert.False(store.IsInstalled(model));
        await store.DownloadAsync(model, new SyncProgress(reports.Enqueue));

        Assert.True(store.IsInstalled(model));
        Assert.Equal(First, File.ReadAllBytes(Path.Combine(root, "test-model", "first.bin")));
        Assert.Equal(Second, File.ReadAllBytes(Path.Combine(root, "test-model", "second.bin")));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "test-model"), "*.part"));
        Assert.Equal(1.0, reports.Last());
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second >= p.First - 1e-9), "progress never goes back");
    }

    [Fact]
    public async Task ResumesAPartialFileWithARangeRequest()
    {
        var server = new FakeServer();
        var store = NewStore(server, out var root);
        var model = TestModel();
        var directory = Path.Combine(root, "test-model");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "first.bin.part"), First[..123_456]);

        await store.DownloadAsync(model);

        Assert.Equal(First, File.ReadAllBytes(Path.Combine(directory, "first.bin")));
        var request = server.Requests.First(r => r.Path == "/first.bin");
        Assert.Equal(123_456, request.RangeFrom);
        Assert.Equal(First.Length - 123_456, request.BytesSent);
    }

    [Fact]
    public async Task StartsOverWhenTheServerIgnoresTheRange()
    {
        var server = new FakeServer { SupportsRanges = false };
        var store = NewStore(server, out var root);
        var directory = Path.Combine(root, "test-model");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "first.bin.part"), First[..1000]);

        await store.DownloadAsync(TestModel());

        Assert.Equal(First, File.ReadAllBytes(Path.Combine(directory, "first.bin")));
    }

    [Fact]
    public async Task ResumesAfterTheConnectionBreaks()
    {
        var server = new FakeServer { BreakFirstResponseAfter = 100_000 };
        var store = NewStore(server, out var root);

        await store.DownloadAsync(TestModel());

        Assert.Equal(First, File.ReadAllBytes(Path.Combine(root, "test-model", "first.bin")));
        var firstRequests = server.Requests.Where(r => r.Path == "/first.bin").ToList();
        Assert.Equal(2, firstRequests.Count);
        Assert.Null(firstRequests[0].RangeFrom);
        Assert.Equal(100_000, firstRequests[1].RangeFrom);
    }

    [Fact]
    public async Task KeepsThePartWhenCancelled()
    {
        var server = new FakeServer();
        var store = NewStore(server, out var root);
        using var cts = new CancellationTokenSource();
        server.OnChunk = sent => { if (sent >= 150_000) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DownloadAsync(TestModel(), ct: cts.Token));

        var part = new FileInfo(Path.Combine(root, "test-model", "first.bin.part"));
        Assert.True(part.Exists);
        Assert.InRange(part.Length, 1, First.Length - 1);
        Assert.False(store.IsInstalled(TestModel()));
        Assert.True(store.DownloadedBytes(TestModel()) > 0);

        server.OnChunk = null;
        await store.DownloadAsync(TestModel());
        Assert.True(store.IsInstalled(TestModel()));
        Assert.Equal(part.Length, server.Requests.Last(r => r.Path == "/first.bin").RangeFrom);
    }

    [Fact]
    public async Task RejectsAFileWithAWrongChecksum()
    {
        var server = new FakeServer();
        var store = NewStore(server, out var root);
        var model = TestModel(firstSha: new string('0', 64));

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => store.DownloadAsync(model));

        Assert.Contains("beschädigt", error.Message);
        Assert.False(File.Exists(Path.Combine(root, "test-model", "first.bin")));
        Assert.False(File.Exists(Path.Combine(root, "test-model", "first.bin.part")));
    }

    [Fact]
    public async Task ReportsAMissingFileInGerman()
    {
        var server = new FakeServer { NotFound = true };
        var store = NewStore(server, out _);
        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => store.DownloadAsync(TestModel()));
        Assert.Contains("HTTP 404", error.Message);
    }

    [Fact]
    public async Task DownloadsOnceWhenCalledTwiceAtTheSameTime()
    {
        var server = new FakeServer { DelayPerChunk = TimeSpan.FromMilliseconds(2) };
        var store = NewStore(server, out _);
        var model = TestModel();

        await Task.WhenAll(store.DownloadAsync(model), store.DownloadAsync(model));

        Assert.True(store.IsInstalled(model));
        Assert.Equal(1, server.Requests.Count(r => r.Path == "/first.bin"));
        Assert.Equal(1, server.Requests.Count(r => r.Path == "/second.bin"));

        // Installed: no request at all.
        await store.DownloadAsync(model);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task DeleteRemovesTheModel()
    {
        var server = new FakeServer();
        var store = NewStore(server, out var root);
        var model = TestModel();
        await store.DownloadAsync(model);
        string? changed = null;
        store.Changed += id => changed = id;

        store.Delete(model);

        Assert.False(store.IsInstalled(model));
        Assert.False(Directory.Exists(Path.Combine(root, "test-model")));
        Assert.Equal("test-model", changed);
    }

    private static ModelStore NewStore(FakeServer server, out string root)
    {
        root = TestAudio.TempDirectory();
        return new ModelStore(root, new HttpClient(server)) { RetryDelay = TimeSpan.FromMilliseconds(1) };
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed record RequestLog(string Path, long? RangeFrom)
    {
        public long BytesSent { get; set; }
    }

    /// Serves First and Second, honouring Range headers like Hugging Face and GitHub do.
    private sealed class FakeServer : HttpMessageHandler
    {
        private int _responses;

        public ConcurrentQueue<RequestLog> RequestQueue { get; } = new();
        public List<RequestLog> Requests => [.. RequestQueue];
        public bool SupportsRanges { get; init; } = true;
        public bool NotFound { get; init; }
        public long? BreakFirstResponseAfter { get; init; }
        public TimeSpan DelayPerChunk { get; init; }
        public Action<long>? OnChunk { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            var path = request.RequestUri!.AbsolutePath;
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            var log = new RequestLog(path, from);
            RequestQueue.Enqueue(log);
            if (NotFound) return new HttpResponseMessage(HttpStatusCode.NotFound);

            var body = path switch
            {
                "/first.bin" => First,
                "/second.bin" => Second,
                _ => null,
            };
            if (body is null) return new HttpResponseMessage(HttpStatusCode.NotFound);

            var start = SupportsRanges && from is long f ? f : 0;
            if (start >= body.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            var breakAfter = Interlocked.Increment(ref _responses) == 1 ? BreakFirstResponseAfter : null;
            var stream = new SlowStream(body, (int)start, breakAfter, DelayPerChunk, sent =>
            {
                log.BytesSent = sent;
                OnChunk?.Invoke(start + sent);
            });
            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentLength = body.Length - start;
            if (start > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, body.Length - 1, body.Length);
            return response;
        }
    }

    /// Delivers the body in 16 KB pieces; can break the connection after some bytes.
    private sealed class SlowStream(byte[] body, int start, long? breakAfter, TimeSpan delay, Action<long> sent) : Stream
    {
        private int _position = start;
        private long _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            ct.ThrowIfCancellationRequested();
            if (breakAfter is long limit && _sent >= limit) throw new IOException("Connection reset");
            var count = Math.Min(Math.Min(buffer.Length, 16_384), body.Length - _position);
            if (breakAfter is long cap) count = (int)Math.Min(count, cap - _sent);
            if (count <= 0) return 0;
            body.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            _sent += count;
            sent(_sent);
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
