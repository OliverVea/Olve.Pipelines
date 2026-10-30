using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// A raw HTTP/1.1 server on loopback: each connection gets one request read and one response from
/// <c>respond</c> (given the request head), then closes. Records the request heads it saw.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, string> _respond;
    private readonly Task _loop;

    private LoopbackHttpServer(Func<string, string> respond)
    {
        _respond = respond;
        _listener.Start();
        _loop = ServeAsync();
    }

    public static LoopbackHttpServer Start(Func<string, string> respond) => new(respond);

    public static LoopbackHttpServer Start(string response) => new(_ => response);

    /// <summary>A complete response with a body and its Content-Length.</summary>
    public static string Response(int status, string body, string contentType = "application/json") =>
        $"HTTP/1.1 {status} X\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ConcurrentQueue<string> Requests { get; } = new();
    public int Accepted => Requests.Count;

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var buffer = new byte[8192];
                try
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    var head = Encoding.ASCII.GetString(buffer, 0, read);
                    Requests.Enqueue(head);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_respond(head)), _stop.Token);
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
