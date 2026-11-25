using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using FamilyTree.Web;
using Microsoft.AspNetCore.Http;

internal static class PrivateTreeApiClientAssertions
{
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StallingContent()
            });
    }

    private sealed class StallingContent : HttpContent
    {
        public StallingContent() =>
            Headers.ContentType = new MediaTypeHeaderValue("application/json");

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new StallingStream());

        protected override Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new StallingStream());
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            WaitForCancellationAsync(cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WaitForCancellationAsync(cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private static async ValueTask<int> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    public static async Task RunAsync()
    {
        var options = new PrivateTreeModeOptions(true, new Uri("https://localhost:5043/"));
        using var http = new HttpClient(new StallingHandler());
        var api = new PrivateTreeApiClient(http, options);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        var timer = Stopwatch.StartNew();
        var timedOut = await api.SearchAsync(context, Guid.NewGuid(), Guid.NewGuid(),
            "synthetic", CancellationToken.None);
        Ensure(timedOut.Kind == PrivateTreeApiResultKind.Unavailable &&
            timer.Elapsed < TimeSpan.FromSeconds(7),
            "a stalled JSON body returns unavailable within the body-read deadline");

        using var callerCancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));
        try
        {
            await api.SearchAsync(context, Guid.NewGuid(), Guid.NewGuid(),
                "synthetic", callerCancellation.Token);
            throw new InvalidOperationException("Caller cancellation was swallowed.");
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            Console.WriteLine("PASS bounded private API body read and caller cancellation");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(
                $"Private API client assertion failed: {message}");
    }
}
