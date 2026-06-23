using System.Windows.Input;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class HotkeyTriggerNode : BaseNode
{
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

    public override Task OnStartListeningAsync(CancellationToken ct)
    {
        DebugSink?.Invoke($"[TRIGGER INIT] Hotkey «{HotKeyModifiers}+{HotKey}» → IsListening=true, ожидает нажатия.");
        return Task.CompletedTask;
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        DebugSink?.Invoke($"[ТРИГГЕР] Запуск. Хоткей: {HotKeyModifiers}+{HotKey}");

        void ApplyHotkeyStr(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var kc  = new KeyConverter();
            var mkc = new ModifierKeysConverter();
            int lp  = v.LastIndexOf('+');
            string keyStr = lp >= 0 ? v[(lp + 1)..] : v;
            string modStr = lp >= 0 ? v[..lp]        : "None";
            try
            {
                if (kc.ConvertFromString(keyStr)  is Key          k) HotKey          = k;
                if (mkc.ConvertFromString(modStr) is ModifierKeys m) HotKeyModifiers = m;
            }
            catch { }
        }

        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _rawH))
        {
            var _hs = _rawH as string ?? _rawH?.ToString();
            if (_hs is not null) ApplyHotkeyStr(_hs);
        }

        string _hkText = $"{HotKeyModifiers}+{HotKey}";
        LastOutputValue = new DataPacket { Type = DataType.Text, Payload = _hkText };
        DebugSink?.Invoke($"[ВЫХОД] DataBus будет записан: «{_hkText}»");

        if (CurrentContext?.Variables.ContainsKey("IsInteractiveTest") == true)
        {
            DebugSink?.Invoke("[ТРИГГЕР] Режим интерактивного теста — срабатываю мгновенно.");
            IsTriggered = true;
            var _tSid = inputPacket?.SessionId ?? Guid.NewGuid();
            var _tOut = DataBusPacket.Text(_tSid);
            DataBus?.Set(_tOut.SessionId, _tOut.DataId, _hkText);
            return NodeResult.Success(_tOut);
        }

        if (!IsListening)
        {
            DebugSink?.Invoke("[ТРИГГЕР] IsListening=false — хоткей не прослушивается (нода не подключена к TriggerRootNode).");
            return NodeResult.Failure("Hotkey-триггер неактивен (IsListening=false).");
        }

        DebugSink?.Invoke($"[ТРИГГЕР] Ожидаю нажатие {HotKeyModifiers}+{HotKey}...");

        var inputService = NodeServices!.GetRequiredService<IInputService>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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

        using var reg = ct.Register(() =>
        {
            inputService.KeyDown -= OnKeyDown;
            tcs.TrySetCanceled(ct);
        });

        try
        {
            bool result = await tcs.Task.ConfigureAwait(false);
            if (result)
                await NodeLogger!.LogInfoAsync(Name,
                    $"Хоткей активирован: {HotKeyModifiers}+{HotKey}").ConfigureAwait(false);
            var _sid = inputPacket?.SessionId ?? Guid.NewGuid();
            var _out = DataBusPacket.Text(_sid);
            DataBus?.Set(_out.SessionId, _out.DataId, _hkText);
            return result ? NodeResult.Success(_out) : NodeResult.Failure("Хоткей не сработал.", _out);
        }
        catch (OperationCanceledException)
        {
            inputService.KeyDown -= OnKeyDown;
            throw;
        }
    }
}
