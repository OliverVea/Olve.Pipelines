using System.Net;
using System.Net.Sockets;
using System.Text;
using Olve.Pipelines.Pipelines.Triggers;

namespace Olve.Pipelines.UnitTests.Triggers;

public class PollHttpTests
{
    [Test]
    [Arguments("8.8.8.8", true)]
    [Arguments("140.82.112.3", true)]
    [Arguments("2606:4700::1111", true)]
    [Arguments("127.0.0.1", false)]
    [Arguments("10.43.0.1", false)]
    [Arguments("172.16.0.1", false)]
    [Arguments("172.31.255.255", false)]
    [Arguments("192.168.1.10", false)]
    [Arguments("169.254.169.254", false)]
    [Arguments("100.64.0.1", false)]
    [Arguments("0.0.0.0", false)]
    [Arguments("224.0.0.1", false)]
    [Arguments("::1", false)]
    [Arguments("::ffff:10.0.0.1", false)]
    [Arguments("64:ff9b::a00:1", false)]
    [Arguments("fd00::1", false)]
    [Arguments("fe80::1", false)]
    public async Task IsPublic_RejectsPrivateLoopbackAndLinkLocal(string address, bool expected) =>
        await Assert.That(PollHttp.IsPublic(IPAddress.Parse(address))).IsEqualTo(expected);

    // Catches: a poll URL reaching the K8s API, storage or a metadata endpoint from the controller.
    [Test]
    public async Task Get_ToALoopbackAddress_IsRefused()
    {
        await using var server = RawServer.Start("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}");
        using var http = PollHttp.Create();

        await Assert.That(async () => await http.GetAsync($"http://127.0.0.1:{server.Port}/")).Throws<HttpRequestException>();
        await Assert.That(server.Accepted).IsEqualTo(0);
    }

    // Catches: a redirect carrying the poll's $SECRET headers to another host.
    [Test]
    public async Task Get_DoesNotFollowRedirects()
    {
        await using var server = RawServer.Start("HTTP/1.1 302 Found\r\nLocation: http://example.invalid/\r\nContent-Length: 0\r\n\r\n");
        using var http = PollHttp.Create(allowed: _ => true);

        using var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
    }

    [Test]
    public async Task Get_RejectsABodyOverTheCap()
    {
        var body = new string('x', PollHttp.MaxResponseBytes + 1);
        await using var server = RawServer.Start($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        using var http = PollHttp.Create(allowed: _ => true);

        await Assert.That(async () => await http.GetAsync($"http://127.0.0.1:{server.Port}/")).Throws<HttpRequestException>();
    }

    /// <summary>Answers every connection on loopback with one canned HTTP response.</summary>
    private sealed class RawServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        private int _accepted;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Accepted => _accepted;

        public static RawServer Start(string response)
        {
            var server = new RawServer();
            server._listener.Start();
            server._loop = server.ServeAsync(Encoding.ASCII.GetBytes(response));
            return server;
        }

        private async Task ServeAsync(byte[] response)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _accepted);
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer, _stop.Token);
                    try
                    {
                        await stream.WriteAsync(response, _stop.Token);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }
}
