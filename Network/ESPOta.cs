using Polly;
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

        var listener = new TcpListener(IPAddress.Any, _localPort ?? 0)
        {
            Server =
            {
                SendTimeout = 10000,
                ReceiveTimeout = 10000,
                NoDelay = true
            }
        };

        try
        {
            listener.Start();
            _localPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            await _progress($"Server started. Listening to TCP clients at 0.0.0.0:{_localPort}", 0);
            await _progress($"Upload size {_contentSize}", 0);

            DateTime startTime = DateTime.UtcNow;
            TimeSpan timeout = TimeSpan.FromSeconds(30);

            while (DateTime.UtcNow - startTime < timeout)
            {
                var retryPolicy = Policy
                    .Handle<NoResponseException>()
                    .WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(1));
                if (!await retryPolicy.ExecuteAsync(Invite))
                    return false;

                await _progress("Waiting for device to connect...", 0);

                var acceptTask = listener.AcceptTcpClientAsync(ct).AsTask();
                var delayTask = Task.Delay(5000, ct);

                var completedTask = await Task.WhenAny(acceptTask, delayTask);

                if (completedTask != acceptTask) continue;
                using var client = await acceptTask;
                if (await Handle(client)) return true;
            }

            throw new NoResponseException();
        }
        finally
        {
            listener.Stop();
        }

        async Task<bool> Handle(TcpClient client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 60000;
            client.SendTimeout = 60000;
            var stream = client.GetStream();

            await _progress("Got Connection", 2);
            var offset = 0;
            var chunk = new byte[1460];
            var readCount = 0;

            while (_contentSize > offset)
            {
                var chunkSize = await _fs.ReadAsync(chunk, 0, 1460, ct);
                offset += chunkSize;
                if (client.Available > 0)
                {
                    var r1 = Encoding.UTF8.GetString(chunk, 0, readCount);
                    Console.Write(r1);
                }

                await _progress($"Written {offset} out of {_contentSize}", 2 + (int)(offset * 98.0f / _contentSize));
                await stream.WriteAsync(chunk, 0, chunkSize, ct);
            }

            readCount = await stream.ReadAsync(chunk, 0, 1460, ct);
            var resp = Encoding.UTF8.GetString(chunk, 0, readCount);
            while (!resp.Contains("O"))
            {
                if (resp.Contains("E"))
                    return false;

                readCount = await stream.ReadAsync(chunk, 0, 1460, ct);
                resp = Encoding.UTF8.GetString(chunk, 0, readCount);
                Console.Write(resp);
            }

            await _progress("All done!", 100);
            client.Close();
            return true;
        }

        async Task<bool> Invite()
        {
            var message = $"{command:D} {_localPort} {_contentSize} {_fileMd5}\n";
            var messageBytes = Encoding.UTF8.GetBytes(message);
            await _progress($"Sending invitation to {remoteIp}", 0);

            using var udp = new UdpClient();
            var ep = new IPEndPoint(IPAddress.Parse(remoteIp), remotePort);
            await udp.SendAsync(messageBytes, messageBytes.Length, ep);

            var res = udp.ReceiveAsync(ct).AsTask();
            var index = await Task.WhenAny(res, Task.Delay(10000, ct));
            if (res != index)
                throw new NoResponseException();

            var resText = Encoding.UTF8.GetString((await res).Buffer);
            await _progress($"Invitation: {resText}", 1);

            return resText == "OK" ? true : throw new Exception("AUTH required and not implemented");
        }
    }
}

public class NoResponseException : Exception
{
    public NoResponseException() : base("No Response")
    {
    }
}