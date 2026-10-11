namespace ESPresense.Services;

/// <summary>
/// Firmware flavor / CPU lookup used when building node state DTOs.
/// </summary>
public interface IFirmwareTypeStore
{
    Flavor? GetFlavor(string? firmware);
    CPU? GetCpu(string? firmware);
    FirmwareTypes? Get();
}
