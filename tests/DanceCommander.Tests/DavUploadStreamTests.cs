using System.Net;
using DanceCommander.Core.Fs;
using DanceCommander.Providers.WebDav;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// The PUT behind a WebDAV upload, against a handler that plays the server. What these guard is
/// a request that ends before the body does: the writer was left waiting on a pipe nobody read,
/// and a 1.4 GB copy sat at 80% for good.
/// </summary>
public sealed class DavUploadStreamTests
{
    // Each write is bigger than the pipe holds before it makes the writer wait.
    private static readonly byte[] Piece = new byte[256 * 1024];
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(10);
    private static readonly FsUri File = FsUri.Parse("davs://cloud.example.com/dav/big.iso");

    private sealed class Server(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            answer(request, ct);
    }

    /// <summary>Takes so many bytes, then fails the way a dropped connection does.</summary>
    private sealed class DroppingStream(long after) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Length + buffer.Length > after)
                throw new IOException("An existing connection was forcibly closed by the remote host.");
            return base.WriteAsync(buffer, ct);
        }
    }

    private static (DavUploadStream Upload, List<string> Calls) Open(Server server)
    {
        var calls = new List<string>();
        var upload = new DavUploadStream(
            new HttpClient(server), new Uri("https://cloud.example.com/dav/big.iso.dc-upload-1a2b3c4d"), 64L * Piece.Length, File,
            commit: (_, _, _) =>
            {
                calls.Add("commit");
                return Task.CompletedTask;
            },
            abandon: () =>
            {
                calls.Add("abandon");
                return Task.CompletedTask;
            });
        return (upload, calls);
    }

    private static async Task WriteAllAsync(Stream upload)
    {
        for (var i = 0; i < 64; i++)
            await upload.WriteAsync(Piece);
    }

    [Fact]
    public async Task AConnectionDroppedMidBodyFailsTheWriteInsteadOfHanging()
    {
        var (upload, calls) = Open(new Server(async (request, ct) =>
        {
            await request.Content!.CopyToAsync(new DroppingStream(after: 3 * Piece.Length), ct);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }));

        var thrown = await Should.ThrowAsync<IOException>(() => WriteAllAsync(upload).WaitAsync(Hang));

        // Where it stopped and the network's own words, not "a task was cancelled".
        thrown.Message.ShouldContain("stopped after");
        thrown.Message.ShouldContain("forcibly closed");

        await upload.DisposeAsync().AsTask().WaitAsync(Hang);
        calls.ShouldBe(["abandon"]);
    }

    [Fact]
    public async Task AServerRefusingBeforeReadingTheBodyFailsTheWriteWithItsAnswer()
    {
        var (upload, calls) = Open(new Server((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge))));

        var thrown = await Should.ThrowAsync<IOException>(() => WriteAllAsync(upload).WaitAsync(Hang));
        thrown.Message.ShouldContain("does not take files as large");

        await upload.DisposeAsync().AsTask().WaitAsync(Hang);
        calls.ShouldBe(["abandon"]);
    }

    [Fact]
    public async Task AWriterAlreadyWaitingIsWokenWhenTheRequestEnds()
    {
        // The request ends while the writer is stuck waiting for room in the pipe, which nothing
        // reads: the shape of the original hang, without a serializer to close the pipe.
        var gate = new TaskCompletionSource();
        var (upload, calls) = Open(new Server(async (_, _) =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.InsufficientStorage);
        }));

        var writing = WriteAllAsync(upload);
        await Task.Delay(200);
        writing.IsCompleted.ShouldBeFalse();

        gate.SetResult();
        var thrown = await Should.ThrowAsync<IOException>(() => writing.WaitAsync(Hang));
        thrown.Message.ShouldContain("no room");

        await upload.DisposeAsync().AsTask().WaitAsync(Hang);
        calls.ShouldBe(["abandon"]);
    }

    [Fact]
    public async Task AFileFoundUnderTheTemporaryNameIsLeftAlone()
    {
        // The PUT says "only if nothing is there"; a 412 means something was, and it is not ours
        // to delete on the way out.
        var (upload, calls) = Open(new Server((request, _) =>
        {
            request.Headers.IfNoneMatch.ShouldContain(System.Net.Http.Headers.EntityTagHeaderValue.Any);
            request.Headers.ExpectContinue.ShouldBe(true);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
        }));

        var thrown = await Should.ThrowAsync<IOException>(() => WriteAllAsync(upload).WaitAsync(Hang));
        thrown.Message.ShouldContain("nothing was overwritten");

        await upload.DisposeAsync().AsTask().WaitAsync(Hang);
        calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("short.iso", 220, "short.iso")]
    [InlineData("abcdef", 4, "abcd")]
    [InlineData("ääää", 5, "ää")] // two bytes each: never half a character
    [InlineData("a😀b", 4, "a")] // four bytes, two UTF-16 units: not split either
    public void ATemporaryNameStemFitsInItsBytes(string name, int maxBytes, string expected) =>
        TemporaryName.Shortened(name, maxBytes).ShouldBe(expected);

    [Fact]
    public async Task AWholeUploadIsSentAndCommitted()
    {
        var received = new MemoryStream();
        var (upload, calls) = Open(new Server(async (request, ct) =>
        {
            await request.Content!.CopyToAsync(received, ct);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }));

        await WriteAllAsync(upload).WaitAsync(Hang);
        await upload.CommitAsync(replace: false, CancellationToken.None).WaitAsync(Hang);
        await upload.DisposeAsync();

        received.Length.ShouldBe(64L * Piece.Length);
        calls.ShouldBe(["commit"]);
    }

    [Fact]
    public async Task CancellingWhileTheServerHoldsTheAnswerIsACancellation()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>();
        var (upload, calls) = Open(new Server(async (request, ct) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, ct);
            using (ct.Register(() => held.TrySetCanceled(ct)))
                return await held.Task;
        }));

        await WriteAllAsync(upload).WaitAsync(Hang);
        using var cancel = new CancellationTokenSource();
        var commit = upload.CommitAsync(replace: false, cancel.Token);
        await cancel.CancelAsync();

        // The user's own cancel stays a cancellation, the PUT is stopped, and the temporary file goes.
        await Should.ThrowAsync<OperationCanceledException>(() => commit.WaitAsync(Hang));
        calls.ShouldBe(["abandon"]);
    }
}
