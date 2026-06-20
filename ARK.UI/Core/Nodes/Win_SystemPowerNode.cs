using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ARK.UI.Core.Nodes;

public sealed partial class Win_SystemPowerNode : BaseNode
{
    public override bool   IsDangerous       => true;
    public override string DangerWarningText =>
        "Команды завершения работы, перезагрузки или сна мгновенно изменят состояние системы. Убедитесь, что все важные файлы сохранены.";

    public static IReadOnlyList<WinPowerAction> AllActions { get; } = Enum.GetValues<WinPowerAction>();

    private WinPowerAction _selectedAction = WinPowerAction.Lock;
    public WinPowerAction SelectedAction
    {
        get => _selectedAction;
        set { if (_selectedAction != value) { _selectedAction = value; OnPropertyChanged(); } }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();

    [LibraryImport("PowrProf.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool bHibernate,
        [MarshalAs(UnmanagedType.Bool)] bool bForce,
        [MarshalAs(UnmanagedType.Bool)] bool bWakeupEventsDisabled);

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider, ILogService logger, CancellationToken cancellationToken)
    {
        DebugSink?.Invoke($"[ПИТАНИЕ] Инициализация системного действия [{SelectedAction}]...");

        bool isSuccess = false;
        try
        {
            switch (SelectedAction)
            {
                case WinPowerAction.Lock:
                    DebugSink?.Invoke("[СИСТЕМА] Вызываю блокировку экрана Windows (user32.dll → LockWorkStation)...");
                    LockWorkStation();
                    isSuccess = true;
                    break;

                case WinPowerAction.Sleep:
                    DebugSink?.Invoke("[СИСТЕМА] Перевожу ОС в режим сна (PowrProf.dll → SetSuspendState)...");
                    SetSuspendState(false, false, false);
                    isSuccess = true;
                    break;

                case WinPowerAction.MonitorOff:
                    DebugSink?.Invoke("[СИСТЕМА] Отправляю WM_SYSCOMMAND SC_MONITORPOWER=2 (user32.dll → SendMessageW)...");
                    Win32Api.SendMessageW(new IntPtr(0xFFFF), Win32Api.WM_SYSCOMMAND, (IntPtr)0xF170, (IntPtr)2);
                    isSuccess = true;
                    break;

                case WinPowerAction.Shutdown:
                    DebugSink?.Invoke("[СИСТЕМА] Запускаю выключение компьютера (shutdown.exe /s /t 0)...");
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0")
                        { UseShellExecute = false, CreateNoWindow = true });
                    isSuccess = true;
                    break;

                case WinPowerAction.Restart:
                    DebugSink?.Invoke("[СИСТЕМА] Запускаю перезагрузку компьютера (shutdown.exe /r /t 0)...");
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0")
                        { UseShellExecute = false, CreateNoWindow = true });
                    isSuccess = true;
                    break;

                default:
                    DebugSink?.Invoke($"[ПИТАНИЕ] Неизвестное действие [{SelectedAction}] — пропускаю.");
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugSink?.Invoke($"[СИСТЕМА] [ОШИБКА] Сбой [{SelectedAction}]: {ex.Message}");
            await logger.LogErrorAsync(Name, $"[ПИТАНИЕ] Сбой действия [{SelectedAction}].", ex).ConfigureAwait(false);
            return false;
        }

        DebugSink?.Invoke($"[ПИТАНИЕ] [{SelectedAction}] завершено. Статус: {(isSuccess ? "Успех ✓" : "Ошибка ✗")}");
        await logger.LogInfoAsync(Name,
            $"[ПИТАНИЕ] [{SelectedAction}] → {(isSuccess ? "УСПЕХ" : "ОШИБКА")}").ConfigureAwait(false);

        return isSuccess;
    }
}
