namespace VictusControl.Core;

// Command module ids routed in the "Command" field of hpqBDataIn.
// Most gaming subcommands live under Gaming; the graphics-mode switch
// (OmenMon Get/SetGpuMode) sits under its own legacy ids.
public static class BiosModule
{
    public const uint Gaming = 0x20008;
    public const uint Legacy = 0x00001;
    public const uint GpuMode = 0x00002;
}

// seam for hardware-free tests of the critical path (order, retry, verify)
public interface IHpWmiClient : IDisposable
{
    int Execute(uint module, BiosCommand command, byte[]? payload, int responseSize, out byte[]? response);

    void EnsureSuccess(uint module, BiosCommand command, byte[]? payload, int responseSize, out byte[]? response);

    // convenience overload: gaming module (0x20008), the common case
    int Execute(BiosCommand command, byte[]? payload, int responseSize, out byte[]? response)
        => Execute(BiosModule.Gaming, command, payload, responseSize, out response);

    void EnsureSuccess(BiosCommand command, byte[]? payload, int responseSize, out byte[]? response)
        => EnsureSuccess(BiosModule.Gaming, command, payload, responseSize, out response);
}
