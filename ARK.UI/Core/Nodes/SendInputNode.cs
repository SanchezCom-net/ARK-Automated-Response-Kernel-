using System.Text.Json.Serialization;
using System.Windows.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class SendInputNode : BaseNode
{
    // "TargetKey" — виртуальный ключ для приёма имени клавиши в виде строки (напр. "F12")
    public override string DefaultDataInputPropertyName => "TargetKey";

    // ── Целевая клавиша ───────────────────────────────────────────────────

    private Key _targetKey = Key.None;
    public Key TargetKey
    {
        get => _targetKey;
        set { if (_targetKey != value) { _targetKey = value; OnPropertyChanged(); } }
    }

    // ── Модификаторы ──────────────────────────────────────────────────────

    private ModifierKeys _targetModifiers = ModifierKeys.None;
    public ModifierKeys TargetModifiers
    {
        get => _targetModifiers;
        set
        {
            if (_targetModifiers == value) return;
            _targetModifiers = value;
            OnPropertyChanged();
            // Уведомляем вспомогательные свойства для биндинга чекбоксов
            OnPropertyChanged(nameof(IsCtrl));
            OnPropertyChanged(nameof(IsShift));
            OnPropertyChanged(nameof(IsAlt));
            OnPropertyChanged(nameof(IsWin));
        }
    }

    // ── Bool-хелперы для биндинга CheckBox (JsonIgnore — только runtime) ─

    [JsonIgnore]
    public bool IsCtrl
    {
        get => (TargetModifiers & ModifierKeys.Control) != 0;
        set => TargetModifiers = value
            ? TargetModifiers | ModifierKeys.Control
            : TargetModifiers & ~ModifierKeys.Control;
    }

    [JsonIgnore]
    public bool IsShift
    {
        get => (TargetModifiers & ModifierKeys.Shift) != 0;
        set => TargetModifiers = value
            ? TargetModifiers | ModifierKeys.Shift
            : TargetModifiers & ~ModifierKeys.Shift;
    }

    [JsonIgnore]
    public bool IsAlt
    {
        get => (TargetModifiers & ModifierKeys.Alt) != 0;
        set => TargetModifiers = value
            ? TargetModifiers | ModifierKeys.Alt
            : TargetModifiers & ~ModifierKeys.Alt;
    }

    [JsonIgnore]
    public bool IsWin
    {
        get => (TargetModifiers & ModifierKeys.Windows) != 0;
        set => TargetModifiers = value
            ? TargetModifiers | ModifierKeys.Windows
            : TargetModifiers & ~ModifierKeys.Windows;
    }

    // ── Пауза после отправки ──────────────────────────────────────────────

    private int _delayAfterMs = 50;
    public int DelayAfterMs
    {
        get => _delayAfterMs;
        set { if (_delayAfterMs != value) { _delayAfterMs = value; OnPropertyChanged(); } }
    }

    // ── Случайная задержка ────────────────────────────────────────────────

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

    // ── Выполнение ────────────────────────────────────────────────────────

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        DebugSink?.Invoke($"[ВВОД] Запуск. Клавиша: {TargetKey}, модификаторы: {TargetModifiers}");

        // ── Input: принимаем параметры по серебряному проводу ─────────────
        // ParseAndApplyHotkeyString разбирает полные строки вида "Ctrl+Shift+V"
        bool hasKeyInput = TryApplyContextInput<string>("TargetKey", ParseAndApplyHotkeyString);
        TryApplyContextInput<int>(nameof(DelayAfterMs), v => DelayAfterMs = v);

        if (hasKeyInput)
            DebugSink?.Invoke($"[ВХОД] Динамические параметры приняты. Клавиша: {TargetKey}, модификаторы: {TargetModifiers}");
        else
            DebugSink?.Invoke($"[ВХОД] Используются статические настройки: {TargetKey} + {TargetModifiers}");

        if (TargetKey == Key.None)
        {
            DebugSink?.Invoke("[ВВОД] TargetKey = None — нажатие пропущено.");
            return true;
        }

        // ── Расчёт задержки ───────────────────────────────────────────────
        int delay = DelayAfterMs;
        if (IsDelayRandom)
        {
            int min = Math.Min(MinDelayAfterMs, MaxDelayAfterMs);
            int max = Math.Max(MinDelayAfterMs, MaxDelayAfterMs);
            delay = Random.Shared.Next(min, max + 1);
            DebugSink?.Invoke($"[ТАЙМЕР] Случайная задержка: {delay} мс (диапазон {min}–{max} мс)");
        }
        else
        {
            DebugSink?.Invoke($"[ТАЙМЕР] Статическая задержка: {delay} мс");
        }

        // ── Формируем строку нажатия для вывода ───────────────────────────
        var parts = new List<string>(5);
        if (IsCtrl)  parts.Add("Ctrl");
        if (IsShift) parts.Add("Shift");
        if (IsAlt)   parts.Add("Alt");
        if (IsWin)   parts.Add("Win");
        parts.Add(TargetKey.ToString());
        string shortcut = string.Join("+", parts);

        // ── Эмуляция нажатия ──────────────────────────────────────────────
        DebugSink?.Invoke($"[ВВОД] Эмулирую нажатие: «{shortcut}»...");
        var actionService = serviceProvider.GetRequiredService<IActionService>();

        if (TargetModifiers == ModifierKeys.None)
            await actionService.PressKeyAsync(TargetKey, cancellationToken).ConfigureAwait(false);
        else
            await actionService.PressKeyWithModifiersAsync(TargetKey, TargetModifiers, cancellationToken)
                .ConfigureAwait(false);

        DebugSink?.Invoke($"[ВВОД] Нажатие «{shortcut}» отправлено ✓");

        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            DebugSink?.Invoke($"[ТАЙМЕР] Пауза {delay} мс выдержана.");
        }

        // ── Output ────────────────────────────────────────────────────────
        LastOutputValue = new DataPacket { Type = DataType.Text, Payload = shortcut };
        DebugSink?.Invoke($"[ВЫХОД] DataPacket записан: «{shortcut}»");

        await logger.LogInfoAsync(Name, $"[ВВОД] Отправлено: {shortcut}. Задержка: {delay} мс.")
            .ConfigureAwait(false);

        return true;
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
