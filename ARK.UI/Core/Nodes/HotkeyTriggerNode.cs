using System.Windows.Input;
using ARK.UI.Core.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class HotkeyTriggerNode : BaseNode
{
    // "HotKeyText" — виртуальный ключ, принимающий комбинацию клавиш как строку (напр. "Ctrl+F12")
    public override string DefaultDataInputPropertyName => "HotKeyText";

    private Key _hotKey = Key.None;
    public Key HotKey
    {
        get => _hotKey;
        set { if (_hotKey != value) { _hotKey = value; OnPropertyChanged(); } }
    }

    private ModifierKeys _hotKeyModifiers = ModifierKeys.None;
    public ModifierKeys HotKeyModifiers
    {
        get => _hotKeyModifiers;
        set { if (_hotKeyModifiers != value) { _hotKeyModifiers = value; OnPropertyChanged(); } }
    }

    public bool IsTriggered { get; private set; }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        DebugSink?.Invoke($"[ТРИГГЕР] Запуск. Хоткей: {HotKeyModifiers}+{HotKey}");

        TryApplyContextInput<string>("HotKeyText", v =>
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var kc  = new KeyConverter();
            var mkc = new ModifierKeysConverter();
            int lastPlus = v.LastIndexOf('+');
            string keyStr = lastPlus >= 0 ? v[(lastPlus + 1)..] : v;
            string modStr = lastPlus >= 0 ? v[..lastPlus]       : "None";
            try
            {
                if (kc.ConvertFromString(keyStr)   is Key          k) HotKey          = k;
                if (mkc.ConvertFromString(modStr)  is ModifierKeys m) HotKeyModifiers = m;
            }
            catch { /* игнорируем невалидные комбинации */ }
        });

        LastOutputValue = new DataPacket { Type = DataType.Text, Payload = $"{HotKeyModifiers}+{HotKey}" };
        DebugSink?.Invoke($"[ВЫХОД] DataPacket записан: «{HotKeyModifiers}+{HotKey}»");

        if (CurrentContext?.Variables.ContainsKey("IsInteractiveTest") == true)
        {
            DebugSink?.Invoke("[ТРИГГЕР] Режим интерактивного теста — срабатываю мгновенно, без ожидания физической клавиши.");
            IsTriggered = true;
            return true;
        }

        DebugSink?.Invoke($"[ТРИГГЕР] Ожидаю нажатие {HotKeyModifiers}+{HotKey}...");

        var inputService = serviceProvider.GetRequiredService<IInputService>();
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnKeyDown(object? sender, KeyHookEventArgs e)
        {
            if (e.Key == HotKey && e.Modifiers == HotKeyModifiers)
            {
                DebugSink?.Invoke($"[ТРИГГЕР] Клавиша получена: {e.Modifiers}+{e.Key} ✓");
                IsTriggered = true;
                inputService.KeyDown -= OnKeyDown;
                tcs.TrySetResult(true);
            }
        }

        inputService.KeyDown += OnKeyDown;

        using var reg = cancellationToken.Register(() =>
        {
            inputService.KeyDown -= OnKeyDown;
            tcs.TrySetCanceled(cancellationToken);
        });

        try
        {
            bool result = await tcs.Task.ConfigureAwait(false);
            if (result)
                await logger.LogInfoAsync(Name,
                    $"Хоткей активирован: {HotKeyModifiers}+{HotKey}").ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            inputService.KeyDown -= OnKeyDown;
            throw;
        }
    }
}
