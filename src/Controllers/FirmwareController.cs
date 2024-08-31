using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using ESPresense.Network;
using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers;

[ApiController]
public class FirmwareController : Controller
{
    private readonly FirmwareTypeStore _firmwareTypeStore;
    private readonly NodeSettingsStore _nodeSettingsStore;
    private readonly NodeTelemetryStore _nts;
    private readonly ILogger<FirmwareController> _logger;
    private readonly HttpClient _hc;

    public FirmwareController(FirmwareTypeStore firmwareTypeStore, NodeSettingsStore nodeSettingsStore, NodeTelemetryStore nts, ILogger<FirmwareController> logger, HttpClient hc)
    {
        _firmwareTypeStore = firmwareTypeStore;
        _nodeSettingsStore = nodeSettingsStore;
        _nts = nts;
        _logger = logger;
        _hc = hc;
    }

    [HttpGet]
    [Route("api/firmware/types")]
    public FirmwareTypes? Types()
    {
        return _firmwareTypeStore.Get();
    }

    [Route("ws/firmware/update/{id}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task Update(string id, [FromQuery] string url)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

        var ms = await GetFirmware(url);

        await _nodeSettingsStore.Arduino(id, true);
        try
        {
            var tele = _nts.Get(id);
            var espOta = new ESPOta(ms, Environment.GetEnvironmentVariable("OTA_UPDATE_PORT").ToInt(), Log);
            var success = await espOta.Update(tele?.Ip ?? throw new Exception("Telemetry not found"), 3232);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error updating firmware");
            await Log(e.Message, -1);
        }
        finally
        {
            await _nodeSettingsStore.Arduino(id, false);
        }

        return;

        async Task Log(string message, int percent)
        {
            _logger.LogInformation("[{per}] {msg}", percent, message);
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { percentComplete = percent, message = message });
            var buffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json));
            await webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    private async Task<MemoryStream> GetFirmware(string url)
    {
        var response = await _hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Error loading firmware, status={response.StatusCode}");

        var ms1 = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync();
        await stream.CopyToAsync(ms1);
        ms1.Position = 0;

        byte[] buffer = new byte[2];
        ms1.Read(buffer, 0, 2);
        ms1.Position = 0;
        bool isZip = buffer[0] == 0x50 && buffer[1] == 0x4B; // Check for 'PK'

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!isZip && mediaType != "application/zip")
            return ms1;

        using (var zipArchive = new ZipArchive(ms1))
        {
            foreach (var entry in zipArchive.Entries)
            {
                var ms2 = new MemoryStream();
                await using var entryStream = entry.Open();
                await entryStream.CopyToAsync(ms2);
                ms2.Position = 0;
                return ms2;
            }
        }

        ms1.Position = 0;
        return ms1;
    }
}