using System.Text.Json.Serialization;
using System.Windows.Input;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class SendInputNode : BaseNode
{
    public override string DefaultDataInputPropertyName => "TargetKey";

    private Key _targetKey = Key.None;
    public Key TargetKey
    {
        get => _targetKey;
        set { if (_targetKey != value) { _targetKey = value; OnPropertyChanged(); } }
    }

    private ModifierKeys _targetModifiers = ModifierKeys.None;
    public ModifierKeys TargetModifiers
    {
        get => _targetModifiers;
        set
        {
            if (_targetModifiers == value) return;
            _targetModifiers = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCtrl));
            OnPropertyChanged(nameof(IsShift));
            OnPropertyChanged(nameof(IsAlt));
            OnPropertyChanged(nameof(IsWin));
        }
    }

    [JsonIgnore]
    public bool IsCtrl
    {
        get => (TargetModifiers & ModifierKeys.Control) != 0;
        set => TargetModifiers = value ? TargetModifiers | ModifierKeys.Control : TargetModifiers & ~ModifierKeys.Control;
    }

    [JsonIgnore]
    public bool IsShift
    {
        get => (TargetModifiers & ModifierKeys.Shift) != 0;
        set => TargetModifiers = value ? TargetModifiers | ModifierKeys.Shift : TargetModifiers & ~ModifierKeys.Shift;
    }

    [JsonIgnore]
    public bool IsAlt
    {
        get => (TargetModifiers & ModifierKeys.Alt) != 0;
        set => TargetModifiers = value ? TargetModifiers | ModifierKeys.Alt : TargetModifiers & ~ModifierKeys.Alt;
    }

    [JsonIgnore]
    public bool IsWin
    {
        get => (TargetModifiers & ModifierKeys.Windows) != 0;
        set => TargetModifiers = value ? TargetModifiers | ModifierKeys.Windows : TargetModifiers & ~ModifierKeys.Windows;
    }

    private int _delayAfterMs = 50;
    public int DelayAfterMs
    {
        get => _delayAfterMs;
        set { if (_delayAfterMs != value) { _delayAfterMs = value; OnPropertyChanged(); } }
    }

    private bool _isDelayRandom;
    public bool IsDelayRandom
    {
        get => _isDelayRandom;
        set { if (_isDelayRandom != value) { _isDelayRandom = value; OnPropertyChanged(); } }
    }

    private int _minDelayAfterMs = 50;
    public int MinDelayAfterMs
    {
        get => _minDelayAfterMs;
        set { if (_minDelayAfterMs != value) { _minDelayAfterMs = value; OnPropertyChanged(); } }
    }

    private int _maxDelayAfterMs = 150;
    public int MaxDelayAfterMs
    {
        get => _maxDelayAfterMs;
        set { if (_maxDelayAfterMs != value) { _maxDelayAfterMs = value; OnPropertyChanged(); } }
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        DebugSink?.Invoke($"[ВВОД] Запуск. Клавиша: {TargetKey}, модификаторы: {TargetModifiers}");

        bool _hasKeyInput = false;
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _rawK))
        {
            var _ks = _rawK as string ?? _rawK?.ToString();
            if (_ks is not null) { ParseAndApplyHotkeyString(_ks); _hasKeyInput = true; }
        }

        if (_hasKeyInput)
            DebugSink?.Invoke($"[ВХОД] Динамические параметры приняты. Клавиша: {TargetKey}");
        else
            DebugSink?.Invoke($"[ВХОД] Используются статические настройки: {TargetKey} + {TargetModifiers}");

        if (TargetKey == Key.None)
        {
            DebugSink?.Invoke("[ВВОД] TargetKey = None — нажатие пропущено.");
            return NodeResult.Success(null);
        }

        int delay = DelayAfterMs;
        if (IsDelayRandom)
        {
            int min = Math.Min(MinDelayAfterMs, MaxDelayAfterMs);
            int max = Math.Max(MinDelayAfterMs, MaxDelayAfterMs);
            delay = Random.Shared.Next(min, max + 1);
        }

        var parts = new List<string>(5);
        if (IsCtrl)  parts.Add("Ctrl");
        if (IsShift) parts.Add("Shift");
        if (IsAlt)   parts.Add("Alt");
        if (IsWin)   parts.Add("Win");
        parts.Add(TargetKey.ToString());
        string shortcut = string.Join("+", parts);

        DebugSink?.Invoke($"[ВВОД] Эмулирую нажатие: «{shortcut}»...");
        var actionService = NodeServices!.GetRequiredService<IActionService>();

        if (TargetModifiers == ModifierKeys.None)
            await actionService.PressKeyAsync(TargetKey, ct).ConfigureAwait(false);
        else
            await actionService.PressKeyWithModifiersAsync(TargetKey, TargetModifiers, ct).ConfigureAwait(false);

        if (delay > 0)
            await Task.Delay(delay, ct).ConfigureAwait(false);

        LastOutputValue = new DataPacket { Type = DataType.Text, Payload = shortcut };
        await NodeLogger!.LogInfoAsync(Name, $"[ВВОД] Отправлено: {shortcut}. Задержка: {delay} мс.")
            .ConfigureAwait(false);
        LogToBlackBox($"[ВВОД] Нажата клавиша: {shortcut}, задержка: {delay} мс");

        var _sid = inputPacket?.SessionId ?? Guid.NewGuid();
        var _out = DataBusPacket.Text(_sid);
        DataBus?.Set(_out.SessionId, _out.DataId, shortcut);
        return NodeResult.Success(_out);
    }

    private void ParseAndApplyHotkeyString(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        TargetKey       = Key.None;
        TargetModifiers = ModifierKeys.None;

        foreach (var raw in input.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": IsCtrl  = true; break;
                case "shift":             IsShift = true; break;
                case "alt":               IsAlt   = true; break;
                case "win" or "windows":  IsWin   = true; break;
                case "none":              break;
                default:
                    if (Enum.TryParse<Key>(raw, ignoreCase: true, out var key))
                        TargetKey = key;
                    break;
            }
        }
    }
}
