using ARK.UI.Core.Bus;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace ARK.UI.Core.Nodes;

// ── Undocumented IPolicyConfig COM interface (Windows 10/11) ─────────────────
[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    // vtable slots 3-12: placeholder slots before SetDefaultEndpoint
    void _s03(); void _s04(); void _s05(); void _s06(); void _s07();
    void _s08(); void _s09(); void _s10(); void _s11(); void _s12();
    // vtable slot 13
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string devId, EAudioRole role);
    void _s14();
}

[ComImport, Guid("87563F5B-4456-40D9-A36F-AB66AC4E8198")]
class PolicyConfigClient { }

internal enum EAudioRole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

// ─────────────────────────────────────────────────────────────────────────────

public sealed class Win_AudioDeviceNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(DeviceName);

    private string _deviceName = string.Empty;
    public string DeviceName
    {
        get => _deviceName;
        set { if (_deviceName != value) { _deviceName = value; OnPropertyChanged(); } }
    }

    private bool _isPlayback = true;
    public bool IsPlayback
    {
        get => _isPlayback;
        set { if (_isPlayback != value) { _isPlayback = value; OnPropertyChanged(); } }
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            var _s = _raw as string ?? _raw?.ToString();
            if (_s is not null) DeviceName = _s;
        }

        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            await NodeLogger!.LogWarningAsync(Name, "[Win] Имя устройства не задано.").ConfigureAwait(false);
            return NodeResult.Failure("Имя устройства не задано.");
        }

        var dataFlow = IsPlayback ? DataFlow.Render : DataFlow.Capture;
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);

        MMDevice? target = null;
        foreach (var d in devices)
        {
            if (d.FriendlyName.Contains(DeviceName, StringComparison.OrdinalIgnoreCase))
            { target = d; break; }
        }

        if (target is null)
        {
            await NodeLogger!.LogWarningAsync(Name, $"[Win] Устройство '{DeviceName}' не найдено в системе.").ConfigureAwait(false);
            return NodeResult.Failure($"Устройство '{DeviceName}' не найдено.");
        }

        var policyConfig = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            policyConfig.SetDefaultEndpoint(target.ID, EAudioRole.eConsole);
            policyConfig.SetDefaultEndpoint(target.ID, EAudioRole.eMultimedia);
            policyConfig.SetDefaultEndpoint(target.ID, EAudioRole.eCommunications);
        }
        finally
        {
            Marshal.ReleaseComObject(policyConfig);
        }

        string direction = IsPlayback ? "вывода" : "ввода";
        await NodeLogger!.LogInfoAsync(Name,
            $"[СИСТЕМА] Устройство {direction} по умолчанию успешно переключено на: '{target.FriendlyName}'.").ConfigureAwait(false);
        return NodeResult.Success(null);
    }
}
