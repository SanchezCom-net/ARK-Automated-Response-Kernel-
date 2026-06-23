using ARK.UI.Core.Bus;
using ARK.UI.Core.Services;
using System.Diagnostics;

namespace ARK.UI.Core.Nodes;

public sealed class Wait_SmartDelayNode : BaseNode
{
    public static IReadOnlyList<SmartWaitType> AllWaitTypes { get; } = Enum.GetValues<SmartWaitType>();

    private SmartWaitType _waitType = SmartWaitType.UntilWindowActive;
    public SmartWaitType WaitType
    {
        get => _waitType;
        set { if (_waitType != value) { _waitType = value; OnPropertyChanged(); } }
    }

    private string _windowProcessName = string.Empty;
    public string WindowProcessName
    {
        get => _windowProcessName;
        set { if (_windowProcessName != value) { _windowProcessName = value; OnPropertyChanged(); } }
    }

    private int _x = 0;
    public int X
    {
        get => _x;
        set { if (_x != value) { _x = value; OnPropertyChanged(); } }
    }

    private int _y = 0;
    public int Y
    {
        get => _y;
        set { if (_y != value) { _y = value; OnPropertyChanged(); } }
    }

    private string _targetColorHex = "#FFFFFF";
    public string TargetColorHex
    {
        get => _targetColorHex;
        set { if (_targetColorHex != value) { _targetColorHex = value; OnPropertyChanged(); } }
    }

    private int _timeoutSec = 30;
    public int TimeoutSec
    {
        get => _timeoutSec;
        set { if (_timeoutSec != value) { _timeoutSec = value; OnPropertyChanged(); } }
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(TimeoutSec);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            ResetWatchdogTimer(); // не даём Orchestrator'у объявить ноду ZOMBIE при долгом ожидании

            bool conditionMet = WaitType == SmartWaitType.UntilWindowActive
                ? CheckWindowActive()
                : CheckPixelColor();

            if (conditionMet)
            {
                await NodeLogger!.LogInfoAsync(Name, $"[Wait] Условие выполнено ({WaitType}).").ConfigureAwait(false);
                return NodeResult.Success(null);
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        await NodeLogger!.LogWarningAsync(Name,
            $"[Wait] Таймаут {TimeoutSec} сек истёк — условие не выполнено.").ConfigureAwait(false);
        return NodeResult.Failure($"Таймаут {TimeoutSec} сек истёк.");
    }

    private bool CheckWindowActive()
    {
        if (string.IsNullOrWhiteSpace(WindowProcessName)) return false;
        var hwnd = Win32Api.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        Win32Api.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName.Contains(WindowProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private bool CheckPixelColor()
    {
        var hex = TargetColorHex.TrimStart('#');
        if (hex.Length < 6) return false;

        uint tr = Convert.ToUInt32(hex[0..2], 16);
        uint tg = Convert.ToUInt32(hex[2..4], 16);
        uint tb = Convert.ToUInt32(hex[4..6], 16);
        uint targetRef = tr | (tg << 8) | (tb << 16); // COLORREF: 0x00BBGGRR

        var hdc = Win32Api.GetDC(IntPtr.Zero);
        try
        {
            uint colorRef = Win32Api.GetPixel(hdc, X, Y);
            return colorRef != 0xFFFF_FFFFu && colorRef == targetRef;
        }
        finally
        {
            Win32Api.ReleaseDC(IntPtr.Zero, hdc);
        }
    }
}
