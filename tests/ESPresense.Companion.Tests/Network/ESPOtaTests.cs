using ESPresense.Network;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ESPresense.Companion.Tests.Network;

public class ESPOtaTests
{
    [Test]
    public async Task Update_NodeConfirms_ReturnsTrue()
    {
        using var node = new FakeNode(confirm: true);
        var ota = new ESPOta(new MemoryStream(new byte[5000]), null) { ResponseTimeout = TimeSpan.FromSeconds(5) };

        var result = await ota.Update("127.0.0.1", node.Port);

        Assert.That(result, Is.True);
        Assert.That(await node.Received, Is.EqualTo(5000));
    }

    [Test]
    public async Task Update_NodeNeverRespondsOrCloses_TimesOut()
    {
        using var node = new FakeNode(confirm: false);
        var ota = new ESPOta(new MemoryStream(new byte[5000]), null) { ResponseTimeout = TimeSpan.FromSeconds(1) };

        var sw = Stopwatch.StartNew();
        var result = await ota.Update("127.0.0.1", node.Port).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.That(result, Is.False);
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void Update_CancelledWhileAwaitingResponse_Throws()
    {
        using var node = new FakeNode(confirm: false);
        var ota = new ESPOta(new MemoryStream(new byte[5000]), null) { ResponseTimeout = TimeSpan.FromMinutes(5) };
        using var cts = new CancellationTokenSource();

        var update = ota.Update("127.0.0.1", node.Port, ct: cts.Token);
        _ = node.Received.ContinueWith(_ => cts.Cancel());

        Assert.CatchAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    /// <summary>Accepts the UDP invitation, connects back and reads the image, optionally confirming with OK.</summary>
    private sealed class FakeNode : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private TcpClient? _tcp;

        public FakeNode(bool confirm)
        {
            Received = Task.Run(async () =>
            {
                var invite = await _udp.ReceiveAsync();
                var parts = Encoding.UTF8.GetString(invite.Buffer).Split(' ');
                var port = int.Parse(parts[1]);
                var size = int.Parse(parts[2]);
                await _udp.SendAsync(Encoding.UTF8.GetBytes("OK"), invite.RemoteEndPoint);

                _tcp = new TcpClient();
                await _tcp.ConnectAsync(IPAddress.Loopback, port);
                var stream = _tcp.GetStream();
                var buf = new byte[1460];
                var total = 0;
                while (total < size)
                    total += await stream.ReadAsync(buf);
                if (confirm)
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("OK"));
                    _tcp.Close();
                }
                return total;
            });
        }

        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public Task<int> Received { get; }

        public void Dispose()
        {
            _tcp?.Dispose();
            _udp.Dispose();
        }
    }
}
