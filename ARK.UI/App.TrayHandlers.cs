using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows.Controls;
using ARK.UI.Core.Services;
using ARK.UI.Resources;
using ARK.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace ARK.UI;

// Обработчики быстрых инструментов управления трей-меню
public partial class App
{
    private const string AutoStartKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartAppName = "ARK";

    private DashboardWindow? _dashboardWindow;

    // ── Пауза / Возобновление ────────────────────────────────────────────

    private async Task TogglePauseAsync()
    {
        IsPaused = !IsPaused;
        if (IsPaused)
        {
            await (_overlayService?.HideOverlayAsync() ?? Task.CompletedTask);
            if (MiStatus   is not null) MiStatus.Header   = Strings.Tray_StatusPause;
            if (MiRunPause is not null) MiRunPause.Header  = Strings.Tray_RunPlay;
        }
        else
        {
            await (_overlayService?.ShowOverlayAsync() ?? Task.CompletedTask);
            if (MiStatus   is not null) MiStatus.Header   = Strings.Tray_StatusActive;
            if (MiRunPause is not null) MiRunPause.Header  = Strings.Tray_RunPause;
        }
    }

    // ── Оверлей ──────────────────────────────────────────────────────────

    private async Task ToggleOverlayAsync(MenuItem item)
    {
        if (_configService is not null)
        {
            _configService.Current.IsOverlayEnabled = item.IsChecked;
            _ = _configService.SaveAsync();
        }
        if (item.IsChecked)
            await (_overlayService?.ShowOverlayAsync() ?? Task.CompletedTask);
        else
            await (_overlayService?.HideOverlayAsync() ?? Task.CompletedTask);
    }

    private async Task ResetOverlayAsync()
    {
        try
        {
            await (_overlayService?.ResetAsync() ?? Task.CompletedTask);
            _ = _logger?.LogInfoAsync(nameof(App), "Оверлей сброшен по команде пользователя.");
        }
        catch (Exception ex)
        {
            _ = _logger?.LogErrorAsync(nameof(App), "Ошибка сброса оверлея.", ex);
        }
    }

    // ── Статус сети ──────────────────────────────────────────────────────

    private void OnNetworkStatusChanged(object? sender, bool isConnected)
    {
        Dispatcher.Invoke(() =>
        {
            if (MiNetwork is not null)
                MiNetwork.Header = isConnected
                    ? Strings.Tray_NetworkConnected
                    : Strings.Tray_NetworkDisconnected;
        });
    }

    // ── Перезапуск ───────────────────────────────────────────────────────

    private void RestartApp()
    {
        var exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath is null) return;

        var psi = new ProcessStartInfo(exePath) { UseShellExecute = true };
        if (IsRunningAsAdmin())
            psi.Verb = "runas";

        try
        {
            Process.Start(psi);
            _trayIcon?.Dispose();
            Shutdown();
        }
        catch (Exception ex)
        {
            _ = _logger?.LogErrorAsync(nameof(App), "Ошибка перезапуска приложения.", ex);
        }
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ── Переключение языка ───────────────────────────────────────────────

    private void ToggleLanguage()
    {
        bool isRu = (Strings.Culture?.Name ?? "ru").StartsWith("ru", StringComparison.Ordinal);
        var newCulture = new CultureInfo(isRu ? "en" : "ru");

        Strings.Culture = newCulture;
        Thread.CurrentThread.CurrentUICulture = newCulture;

        if (_configService is not null)
        {
            _configService.Current.Language = newCulture.Name;
            _ = _configService.SaveAsync();
        }

        LocalizationService.NotifyCultureChanged();
        RefreshMenuTexts();
        _ = _logger?.LogInfoAsync(nameof(App), $"Язык переключён: {newCulture.Name.ToUpperInvariant()}");
    }

    // Обновляет все заголовки меню после смены языка / состояния
    internal void RefreshMenuTexts()
    {
        if (_trayContextMenu is null) return;
        foreach (var obj in _trayContextMenu.Items)
        {
            if (obj is not MenuItem item) continue;
            switch (item.Tag?.ToString())
            {
                case "status":
                    item.Header = IsPaused ? Strings.Tray_StatusPause : Strings.Tray_StatusActive;
                    break;
                case "run_pause":
                    item.Header = IsPaused ? Strings.Tray_RunPlay : Strings.Tray_RunPause;
                    break;
                case "overlay":      item.Header = Strings.Tray_ToggleOverlay;   break;
                case "network":
                    item.Header = _networkService?.IsConnected == true
                        ? Strings.Tray_NetworkConnected
                        : Strings.Tray_NetworkDisconnected;
                    break;
                case "dashboard":    item.Header = Strings.Tray_OpenDashboard;   break;
                case "restart":      item.Header = Strings.Tray_RestartApp;      break;
                case "language":     item.Header = Strings.Tray_ToggleLanguage;  break;
                case "clear_logs":   item.Header = Strings.Tray_ClearLogs;       break;
                case "autostart":    item.Header = Strings.Tray_AutoStart;       break;
                case "reset_overlay":item.Header = Strings.Tray_ResetOverlay;    break;
                case "exit":         item.Header = Strings.Tray_Exit;            break;
            }
        }
    }

    // ── Очистка логов ────────────────────────────────────────────────────

    private async Task ClearLogsAsync()
    {
        try
        {
            var logDir   = _logger?.LogDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs");
            var todayLog = Path.Combine(logDir, $"log_{DateTime.Now:yyyy-MM-dd}.json");

            await (_logger?.LogInfoAsync(nameof(App), "Очистка архивных файлов логов...") ?? Task.CompletedTask);

            var deleted = 0;
            foreach (var file in Directory.GetFiles(logDir, "*.json"))
            {
                if (string.Equals(file, todayLog, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); deleted++; }
                catch { /* файл занят — пропускаем */ }
            }

            _ = _logger?.LogInfoAsync(nameof(App), $"Удалено архивных логов: {deleted}.");
        }
        catch (Exception ex)
        {
            _ = _logger?.LogErrorAsync(nameof(App), "Ошибка очистки логов.", ex);
        }
    }

    // ── Автозапуск с Windows ─────────────────────────────────────────────

    private void ToggleAutoStart(MenuItem item)
    {
        try
        {
            SetAutoStart(item.IsChecked);
            _ = _logger?.LogInfoAsync(nameof(App),
                $"Автозапуск Windows: {(item.IsChecked ? "включён" : "выключен")}.");
        }
        catch (Exception ex)
        {
            item.IsChecked = !item.IsChecked; // откат при ошибке
            _ = _logger?.LogErrorAsync(nameof(App), "Ошибка изменения автозапуска.", ex);
        }
    }

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartKeyPath, writable: false);
        return key?.GetValue(AutoStartAppName) is not null;
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartKeyPath, writable: true);
        if (key is null) return;

        if (enable)
        {
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? string.Empty;
            key.SetValue(AutoStartAppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AutoStartAppName, throwOnMissingValue: false);
        }
    }

    // ── Dashboard ────────────────────────────────────────────────────────

    private void OpenDashboard()
    {
        if (_dashboardWindow is { IsVisible: true })
        {
            _dashboardWindow.Activate();
            return;
        }
        _dashboardWindow = _serviceProvider!.GetRequiredService<DashboardWindow>();
        _dashboardWindow.Closed += (_, _) => _dashboardWindow = null;
        _dashboardWindow.Show();
        _dashboardWindow.Activate();
    }
}
