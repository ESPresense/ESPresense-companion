namespace ESPresense.Models;

/// <summary>
/// Thrown from a solver's objective when the device has a newer reading than the solve started with.
/// <see cref="Scenario.Locate"/> drops the result; the newer reading has already re-queued the device.
/// </summary>
public sealed class ReadingsSupersededException : Exception
{
    public ReadingsSupersededException() : base("A newer reading superseded this solve") { }
}
