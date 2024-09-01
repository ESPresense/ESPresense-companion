using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ESPresense.Network;

public enum EspOtaCommand
{
    Flash = 0,
    Spiffs = 100,
    Auth = 200,
}

public class ESPOta
{
    private readonly Stream _fs;
    private int? _localPort;
    private readonly Func<string, int, Task> _progress;
    private readonly string _fileMd5;
    private readonly long _contentSize;

    public ESPOta(Stream fs, int? localPort, Func<string, int, Task>? progress = default)
    {
        _fs = fs;
        _localPort = localPort;
        _progress = progress ?? ((a, b) => { return Task.CompletedTask; });
        using var md5 = MD5.Create();
        _fileMd5 = BitConverter.ToString(md5.ComputeHash(_fs)).Replace("-", string.Empty).ToLower();
        _contentSize = _fs.Length;
    }

    public async Task<bool> Update(string remoteIp, int remotePort, EspOtaCommand command = EspOtaCommand.Flash, CancellationToken ct = default)
    {
        _fs.Seek(0, SeekOrigin.Begin);

        var listener = new TcpListener(IPAddress.Any, _localPort ?? 0);
        try
        {
            listener.Start();
            _localPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            await _progress($"Server started. Listening to TCP clients at 0.0.0.0:{_localPort}", 0);
            await _progress($"Upload size {_contentSize}", 0);

            _ = Invite(ct);

            DateTime startTime = DateTime.UtcNow;
            TimeSpan timeout = TimeSpan.FromSeconds(30);
            while (true)
            {
                var remainingTime = timeout - (DateTime.UtcNow - startTime);
                if (remainingTime <= TimeSpan.Zero) break;

                var acceptTask = listener.AcceptTcpClientAsync(ct).AsTask();
                var t = await Task.WhenAny(acceptTask, Task.Delay(remainingTime, ct));
                if (t == acceptTask)
                {
                    using var client = await acceptTask;
                    if (await Handle(client)) return true;
                }
            }

            throw new NoResponseException();
        }
        finally
        {
            listener.Stop();
        }

        async Task<bool> Handle(TcpClient client)
        {
            using var s = client.GetStream();
            using var sr = new StreamReader(s, Encoding.UTF8);
            var response = "";
            var reader = Task.Run(async () =>
            {
                while (true)
                {
                    var buf = new char[32];
                    var r = await sr.ReadAsync(buf, 0, buf.Length);
                    if (r == 0) break;
                    var s = new string(buf, 0, r);
                    foreach (var c in s)
                        if (!char.IsDigit(c))
                            response += c;
                }
            }, ct);
            try
            {
                var ip = client.Client.RemoteEndPoint as IPEndPoint;
                await _progress($"Connection from {ip?.Address}", 2);
                var offset = 0;
                var chunk = new byte[1460];
                while (_contentSize > offset)
                {
                    var chunkSize = await _fs.ReadAsync(chunk, 0, 1460, ct);
                    offset += chunkSize;
                    await s.WriteAsync(chunk, 0, chunkSize, ct);
                    await _progress($"Written {offset} out of {_contentSize}", 2 + (int)(offset * 98.0f / _contentSize));
                }
            }
            finally
            {
                await reader;
            }
            DateTime startTime = DateTime.UtcNow;
            await _progress("Response: " + response, 100);
            if (response.Contains('E')) return false;
            if (response.Contains('O'))
            {
                await _progress("Successfully Updated!", 100);
                return true;
            }
            return false;
        }

        async Task<bool> Invite(CancellationToken ct)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                var message = $"{command:D} {_localPort} {_contentSize} {_fileMd5}\n";
                var messageBytes = Encoding.UTF8.GetBytes(message);
                CancellationTokenSource cts = new();
                var r = Task.Factory.StartNew(async a =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var ep = new IPEndPoint(IPAddress.Parse(remoteIp), remotePort);
                        await udp.SendAsync(messageBytes, messageBytes.Length, ep);
                        await _progress($"Sent invitation to {remoteIp}", 0);
                        await Task.Delay(1000, cts.Token);
                    }
                }, cts.Token, TaskCreationOptions.LongRunning);

                try
                {
                    var res = udp.ReceiveAsync(ct).AsTask();
                    var t = await Task.WhenAny(res, Task.Delay(30000, ct));
                    if (res == t)
                    {
                        var resText = Encoding.UTF8.GetString((await res).Buffer);
                        await _progress($"Invitation: {resText}", 1);
                        if (resText == "OK")
                            return true;
                        throw new Exception("AUTH required and not implemented");
                    }
                    throw new NoResponseException();
                }
                finally
                {
                    cts.Cancel();
                    await r;
                }
            }
            catch (Exception ex)
            {
                await _progress($"Error: {ex.Message}", 100);
                throw;
            }
        }
    }
}

public class NoResponseException : Exception
{
    public NoResponseException() : base("No Response")
    {
    }
}