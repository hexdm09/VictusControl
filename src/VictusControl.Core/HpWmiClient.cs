using Microsoft.Management.Infrastructure;

namespace VictusControl.Core;

/// <summary>
/// Sub-command ids of the HP gaming WMI module (Command = 0x20008).
/// Values established from public protocol documentation and on-device dumps.
/// </summary>
public enum BiosCommand : uint
{
    // Used by PowerController:
    FanMode = 0x1A,
    GpuRead = 0x21,
    GpuWrite = 0x22,
    SystemRead = 0x28,

    // Documented protocol map, no caller yet (kept for reference):
    FanCount = 0x10,
    TempRead = 0x23,
    MaxFanRead = 0x26,
    MaxFanWrite = 0x27,
    CpuWrite = 0x29, // reserved: AMD test board stays on firmware auto-limits
}

/// <summary>
/// Clean-room CIM client for the HP BIOS WMI interface.
/// Talks to ACPI\PNP0C14 via root\wmi, no kernel drivers involved
/// (required for Windows 11 with Memory Integrity / HVCI enabled).
/// </summary>
public sealed class HpWmiClient : IDisposable
{
    private const string Namespace = @"root\wmi";
    private const string MethodClass = "hpqBIntM";
    private const string DataClass = "hpqBDataIn";
    private const uint GamingModule = 0x20008;
    private static readonly byte[] AuthSignature = [(byte)'S', (byte)'E', (byte)'C', (byte)'U'];

    private readonly CimSession _session;
    private readonly CimClass _dataClass;
    private readonly CimInstance _methodInstance;
    private bool _disposed;

    public HpWmiClient()
    {
        CimSession session = CimSession.Create(null);
        try
        {
            CimClass dataClass = session.GetClass(Namespace, DataClass);
            try
            {
                _methodInstance = session
                    .QueryInstances(Namespace, "WQL", $"SELECT * FROM {MethodClass}")
                    .FirstOrDefault()
                    ?? throw new PlatformNotSupportedException(
                        $"HP WMI BIOS interface ({MethodClass}) not found. Not an HP OMEN/Victus device?");
                _dataClass = dataClass;
                _session = session;
            }
            catch
            {
                dataClass.Dispose();
                throw;
            }
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public int Execute(BiosCommand command, byte[]? payload, int responseSize, out byte[]? response)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (responseSize is not (0 or 4 or 128))
            throw new ArgumentOutOfRangeException(nameof(responseSize), "Only hpqBIOSInt{0,4,128} exist.");

        using var input = new CimInstance(_dataClass);
        Set(input, "Sign", AuthSignature);
        Set(input, "Command", GamingModule);
        Set(input, "CommandType", (uint)command);

        if (payload is { Length: > 0 })
        {
            Set(input, "hpqBData", payload);
            Set(input, "Size", (uint)payload.Length);
        }
        else
        {
            Set(input, "Size", 0u);
        }

        using var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("InData", input, CimType.Instance, CimFlags.In),
        };

        using var result = _session.InvokeMethod(_methodInstance, $"hpqBIOSInt{responseSize}", parameters);

        if (result.OutParameters["OutData"].Value is CimInstance outData)
        {
            response = responseSize > 0
                ? outData.CimInstanceProperties["Data"]?.Value as byte[]
                : null;
            return Convert.ToInt32(outData.CimInstanceProperties["rwReturnCode"]?.Value ?? BiosReturnCode.WmiError);
        }

        response = null;
        return BiosReturnCode.WmiError;
    }

    public void EnsureSuccess(BiosCommand command, byte[]? payload, int responseSize, out byte[]? response)
    {
        int rc = Execute(command, payload, responseSize, out response);
        if (rc != BiosReturnCode.Ok)
            throw new InvalidOperationException($"BIOS command {command} failed: {BiosReturnCode.Describe(rc)}.");
    }

    private static void Set(CimInstance instance, string name, object value)
    {
        var property = instance.CimInstanceProperties[name]
            ?? throw new InvalidOperationException($"WMI property '{name}' not found on {DataClass}.");
        property.Value = value;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _methodInstance.Dispose();
        _dataClass.Dispose();
        _session.Dispose();
    }
}
