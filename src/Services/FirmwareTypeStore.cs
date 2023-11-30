using System.Text.Json.Serialization;
using Polly;

namespace ESPresense.Services
{
    public class FirmwareTypeStore
    {
        private readonly HttpClient _httpClient;
        private FirmwareTypes? _firmwareTypes;

        public FirmwareTypeStore(HttpClient httpClient)
        {
            _httpClient = httpClient;
            var retryPolicy = Policy
                .Handle<HttpRequestException>()
                .WaitAndRetryAsync(int.MaxValue, i => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, i))));
            retryPolicy.ExecuteAsync(() => httpClient.GetFromJsonAsync<FirmwareTypes>("https://espresense.com/firmware/types.json")).ContinueWith(async a => { _firmwareTypes = await a; });
        }

        public Flavor? GetFlavor(string? firmware)
        {
            if (firmware == null || _firmwareTypes?.Flavors == null) return null;
            foreach (var flavor in _firmwareTypes.Flavors.Where(a => a.Value is not ""))
                if (firmware.EndsWith(flavor.Value ?? ""))
                    return flavor;
            return _firmwareTypes.Flavors.First();
        }

        public FirmwareTypes? Get()
        {
            return _firmwareTypes;
        }

        public CPU? GetCpu(string? firmware)
        {
            if (firmware == null || _firmwareTypes?.Firmware == null || _firmwareTypes?.CPUs == null) return null;
            var c = _firmwareTypes.Firmware.SingleOrDefault(a => a.Name == firmware + ".bin")?.CPU;
            return _firmwareTypes.CPUs.SingleOrDefault(a => a.Value == c);
        }
    }

    public class FirmwareTypes
    {
        public IList<Firmware>? Firmware { get; set; }
        public IList<Flavor>? Flavors { get; set; }
        [JsonPropertyName("cpus")]
        public IList<CPU>? CPUs { get; set; }
    }

    public class Firmware
    {
        public string? Name { get; set; }
        public string? CPU { get; set; }
        public string? Flavor { get; set; }
    }

    public class Flavor
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
        [JsonPropertyName("cpus")]
        public IList<string>? CPUs { get; set; }
    }

    public class CPU
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
    }
}
