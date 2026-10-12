namespace VictusControl.Core;

// seam for hardware-free tests of the critical path (order, retry, verify)
public interface IHpWmiClient : IDisposable
{
    int Execute(BiosCommand command, byte[]? payload, int responseSize, out byte[]? response);
    void EnsureSuccess(BiosCommand command, byte[]? payload, int responseSize, out byte[]? response);
}
