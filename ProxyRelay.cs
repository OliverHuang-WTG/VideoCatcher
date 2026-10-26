using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VideoCatcher;

// WebView2 and ffmpeg always point at this loopback relay, which forwards to the configured upstream.
// This lets the upstream change without restarting WebView2, and gives ffmpeg (HTTP proxy only) SOCKS5 support.
sealed class ProxyRelay
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly ConcurrentDictionary<Socket, byte> _clients = new();
    Uri? _upstream;

    public ProxyRelay(Uri? upstream)
    {
        _upstream = upstream;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // Run on the thread pool so relayed traffic never resumes on the UI thread.
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public void SetUpstream(Uri? upstream)
    {
        _upstream = upstream;
        // Chromium reuses open tunnels; dropping them forces reconnects through the new upstream.
        foreach (var client in _clients.Keys) client.Dispose();
    }

    async Task AcceptLoopAsync()
    {
        while (true) _ = HandleAsync(await _listener.AcceptSocketAsync());
    }

    async Task HandleAsync(Socket client)
    {
        _clients.TryAdd(client, 0);
        try
        {
            await using var clientStream = new NetworkStream(client, ownsSocket: true);
            var (head, body) = await ReadHeadAsync(clientStream);
            var lines = head.Split("\r\n");
            var request = lines[0].Split(' '); // METHOD TARGET VERSION
            var upstream = _upstream;

            if (request[0] == "CONNECT")
            {
                var target = new Uri("tcp://" + request[1]);
                await using var remote = await OpenTunnelAsync(upstream, target.DnsSafeHost, target.Port);
                await clientStream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
                await PipeAsync(clientStream, remote);
                return;
            }

            // Plain HTTP arrives in absolute-form. Forcing Connection: close keeps one client connection bound to one
            // target, since a keep-alive client could send its next request to a different host.
            var url = new Uri(request[1]);
            var headers = lines.Skip(1)
                .Where(h => !h.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                         && !h.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Stream server;
            if (upstream?.Scheme == "http")
            {
                server = await ConnectAsync(upstream.Host, upstream.Port);
                if (ProxyAuthorization(upstream) is { } auth) headers.Add(auth);
            }
            else
            {
                server = await OpenTunnelAsync(upstream, url.DnsSafeHost, url.Port);
                request[1] = url.PathAndQuery;
            }
            headers.Add("Connection: close");

            await using (server)
            {
                var newHead = string.Join(' ', request) + "\r\n" + string.Join("\r\n", headers) + "\r\n\r\n";
                await server.WriteAsync(Encoding.Latin1.GetBytes(newHead));
                await server.WriteAsync(body);
                await PipeAsync(clientStream, server);
            }
        }
        finally
        {
            _clients.TryRemove(client, out _);
        }
    }

    static Task PipeAsync(Stream a, Stream b) => Task.WhenAny(a.CopyToAsync(b), b.CopyToAsync(a));

    static async Task<Stream> OpenTunnelAsync(Uri? upstream, string host, int port)
    {
        if (upstream is null) return await ConnectAsync(host, port);

        var stream = await ConnectAsync(upstream.Host, upstream.Port);
        if (upstream.Scheme == "socks5") await Socks5ConnectAsync(stream, upstream, host, port);
        else await HttpConnectAsync(stream, upstream, host, port);
        return stream;
    }

    static async Task<Stream> ConnectAsync(string host, int port)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(host, port);
        return new NetworkStream(socket, ownsSocket: true);
    }

    static async Task HttpConnectAsync(Stream stream, Uri proxy, string host, int port)
    {
        var auth = ProxyAuthorization(proxy) is { } line ? line + "\r\n" : "";
        await stream.WriteAsync(Encoding.Latin1.GetBytes($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n{auth}\r\n"));
        var (head, _) = await ReadHeadAsync(stream);
        var status = head.Split("\r\n")[0];
        if (status.Split(' ')[1] != "200") throw new IOException($"Upstream proxy refused CONNECT: {status}");
    }

    // RFC 1928 / RFC 1929. The target is sent as a domain name so DNS resolves on the proxy side.
    static async Task Socks5ConnectAsync(Stream stream, Uri proxy, string host, int port)
    {
        var hasAuth = proxy.UserInfo.Length > 0;
        await stream.WriteAsync(hasAuth ? new byte[] { 5, 1, 2 } : new byte[] { 5, 1, 0 });
        var method = (await ReadExactAsync(stream, 2))[1];
        if (method == 2)
        {
            var credentials = proxy.UserInfo.Split(':', 2);
            var user = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(credentials[0]));
            var pass = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(credentials.ElementAtOrDefault(1) ?? ""));
            byte[] login = [1, (byte)user.Length, .. user, (byte)pass.Length, .. pass];
            await stream.WriteAsync(login);
            if ((await ReadExactAsync(stream, 2))[1] != 0) throw new IOException("SOCKS5 authentication failed.");
        }
        else if (method != 0)
        {
            throw new IOException("SOCKS5 proxy rejected the authentication method.");
        }

        var hostBytes = Encoding.ASCII.GetBytes(host);
        byte[] connect = [5, 1, 0, 3, (byte)hostBytes.Length, .. hostBytes, (byte)(port >> 8), (byte)port];
        await stream.WriteAsync(connect);
        var reply = await ReadExactAsync(stream, 4); // VER REP RSV ATYP
        if (reply[1] != 0) throw new IOException($"SOCKS5 connect failed with code {reply[1]}.");
        var addressLength = reply[3] switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReadExactAsync(stream, 1))[0],
            _ => throw new IOException($"SOCKS5 returned unknown address type {reply[3]}."),
        };
        await ReadExactAsync(stream, addressLength + 2);
    }

    static string? ProxyAuthorization(Uri proxy) => proxy.UserInfo.Length == 0
        ? null
        : "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(proxy.UserInfo)));

    // Returns the header block and any bytes read past it (start of a request body).
    static async Task<(string Head, byte[] Rest)> ReadHeadAsync(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0) throw new IOException("Connection closed before headers were complete.");
            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0) return (Encoding.Latin1.GetString(buffer, 0, end), buffer[(end + 4)..length]);
            if (length == buffer.Length) throw new IOException("Header block too large.");
        }
    }

    static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }
}
