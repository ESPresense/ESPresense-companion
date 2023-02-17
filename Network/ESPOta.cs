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
    private readonly FileStream _fs;
    private readonly int _localPort;
    private readonly Action<string> _logger;
    private readonly string _fileMd5;
    private readonly long _contentSize;

    public ESPOta(FileStream fs, int localPort, Action<string>? logger)
    {
        _fs = fs;
        _localPort = localPort;
        _logger = logger ?? Console.WriteLine;
        using var md5 = MD5.Create();
        _fileMd5 = BitConverter.ToString(md5.ComputeHash(_fs)).Replace("-", string.Empty).ToLower();
        _contentSize = _fs.Length;
    }

    public async Task<bool> Update(string remoteIp, int remotePort, EspOtaCommand command = EspOtaCommand.Flash, CancellationToken ct = default)
    {
        _fs.Seek(0, SeekOrigin.Begin);

        var listener = new TcpListener(IPAddress.Any, _localPort)
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
            _logger($"Server started. Listening to TCP clients at 0.0.0.0:{_localPort}");
            _logger($"Upload size {_contentSize}");

            if (!await Invite())
                return false;

            _logger("Waiting for device to connect...");

            var startTime = DateTime.UtcNow;
            while (DateTime.UtcNow - startTime < TimeSpan.FromSeconds(10))
            {
                if (listener.Pending())
                {
                    using var client = await listener.AcceptTcpClientAsync(ct);
                    if (!await Handle(client)) return false;
                }

                await Task.Delay(10, ct);
            }

            _logger("No response from device");
            return true;
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

            _logger("Got Connection");
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

                _logger($"Written {offset} out of {_contentSize} ({offset * 100.0f / _contentSize})");
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

            _logger("All done!");
            client.Close();
            return true;
        }

        async Task<bool> Invite()
        {
            var message = $"{command} {_localPort} {_contentSize} {_fileMd5}\n";
            var messageBytes = Encoding.UTF8.GetBytes(message);
            _logger("Sending invitation to " + remoteIp);

            using var udp = new UdpClient();
            var ep = new IPEndPoint(IPAddress.Parse(remoteIp), remotePort);
            await udp.SendAsync(messageBytes, messageBytes.Length, ep);

            var res = udp.ReceiveAsync(ct);
            var index = Task.WaitAny(new Task[] { res.AsTask() }, 10000, ct);
            if (index < 0)
            {
                _logger("No Response");
                return false;
            }

            var resText = Encoding.UTF8.GetString((await res).Buffer);
            if (resText == "OK") return true;
            _logger("AUTH required and not implemented");
            return false;
        }
    }
}