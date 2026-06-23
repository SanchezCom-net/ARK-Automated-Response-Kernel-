using System.Globalization;
using System.Text.RegularExpressions;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Models;
using WpfApp       = System.Windows.Application;
using WpfClipboard = System.Windows.Clipboard;

namespace ARK.UI.Core.Nodes;

public enum TextCompareType
{
    Contains,
    ContainsWholeWord,
    Equals,
    StartsWith,
    IsEmpty,
    GreaterThanOrEqual,
    LessThanOrEqual,
    Between,
}

public sealed class TextConditionNode : BaseNode
{
    // Серебряный провод доставляет ПРОВЕРЯЕМОЕ значение, а не образец.
    public override string DefaultDataInputPropertyName => nameof(InputValue);

    public static readonly TextCompareType[] AllCompareTypes = Enum.GetValues<TextCompareType>();

    // Удаляет точки и знаки препинания при текстовых сравнениях (Contains, Equals).
    private static readonly Regex _sanitizeRegex = new(@"[.,!?;:""'()]", RegexOptions.Compiled);

    // Нормализует разделитель (запятая → точка) и парсит через InvariantCulture.
    private static bool SmartTryParseDouble(string? input, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        return double.TryParse(input.Replace(',', '.').Trim(),
            NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private TextCompareType _compareType = TextCompareType.Contains;
    public TextCompareType CompareType
    {
        get => _compareType;
        set { if (_compareType != value) { _compareType = value; OnPropertyChanged(); } }
    }

    private string _matchValue = string.Empty;
    public string MatchValue
    {
        get => _matchValue;
        set { if (_matchValue != value) { _matchValue = value; OnPropertyChanged(); } }
    }

    private string _minValue = string.Empty;
    public string MinValue
    {
        get => _minValue;
        set { if (_minValue != value) { _minValue = value; OnPropertyChanged(); } }
    }

    private string _maxValue = string.Empty;
    public string MaxValue
    {
        get => _maxValue;
        set { if (_maxValue != value) { _maxValue = value; OnPropertyChanged(); } }
    }

    private string _inputValue = string.Empty;
    public string InputValue
    {
        get => _inputValue;
        set { if (_inputValue != value) { _inputValue = value; OnPropertyChanged(); } }
    }

    private bool _ignoreCase = true;
    public bool IgnoreCase
    {
        get => _ignoreCase;
        set { if (_ignoreCase != value) { _ignoreCase = value; OnPropertyChanged(); } }
    }

    private bool _transitOnInput = false;
    public bool TransitOnInput
    {
        get => _transitOnInput;
        set { if (_transitOnInput != value) { _transitOnInput = value; OnPropertyChanged(); } }
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        DebugSink?.Invoke($"[УСЛОВИЕ] Запуск. Тип: {CompareType}");

        bool _hasInput = false;
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            var _s = _raw as string ?? _raw?.ToString();
            if (_s is not null) { InputValue = _s; _hasInput = true; }
        }

        if (!_hasInput)
        {
            InputValue = await WpfApp.Current.Dispatcher.InvokeAsync(() =>
            {
                try { return WpfClipboard.ContainsText() ? WpfClipboard.GetText() : string.Empty; }
                catch { return string.Empty; }
            });
            DebugSink?.Invoke($"[УСЛОВИЕ] Провод не подключён — читаю буфер: «{InputValue}»");
        }
        else
        {
            DebugSink?.Invoke($"[УСЛОВИЕ] Значение с DataBus: «{InputValue}»");
        }

        // ── Транзитный режим: пропускаем сравнение, отдаём InputValue как-есть ──
        if (TransitOnInput)
        {
            if (!string.IsNullOrWhiteSpace(InputValue))
            {
                LastOutputValue = new DataPacket { Type = DataType.Text, Payload = InputValue ?? string.Empty };
                DebugSink?.Invoke("[УСЛОВИЕ] [ТРАНЗИТ] Входящие данные получены. Пропускаю выполнение дальше.");
                await NodeLogger!.LogInfoAsync(Name, "[CONDITION] [TRANSIT] InputValue → транзит.").ConfigureAwait(false);
                var _tSid = inputPacket?.SessionId ?? Guid.NewGuid();
                var _tOut = DataBusPacket.Text(_tSid);
                DataBus?.Set(_tOut.SessionId, _tOut.DataId, InputValue ?? string.Empty);
                return NodeResult.Success(_tOut);
            }
            DebugSink?.Invoke("[УСЛОВИЕ] [ТРАНЗИТ] Ожидание входящих данных...");
            return NodeResult.Failure("Нет входных данных для транзита.", inputPacket);
        }

        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool result;

        switch (CompareType)
        {
            case TextCompareType.Contains:
            {
                var cleanInput = _sanitizeRegex.Replace(InputValue, string.Empty);
                var cleanMatch = _sanitizeRegex.Replace(MatchValue, string.Empty);
                result = cleanInput.Contains(cleanMatch, comparison);
                DebugSink?.Invoke($"[УСЛОВИЕ] Contains «{cleanInput}» ⊇ «{cleanMatch}» → {result}");
                break;
            }
            case TextCompareType.ContainsWholeWord:
            {
                var opts   = IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
                var pattern = @"\b" + Regex.Escape(MatchValue) + @"\b";
                result = Regex.IsMatch(InputValue, pattern, opts);
                DebugSink?.Invoke($"[УСЛОВИЕ] ContainsWholeWord /{pattern}/ в «{InputValue}» → {result}");
                break;
            }
            case TextCompareType.Equals:
            {
                var cleanInput = _sanitizeRegex.Replace(InputValue, string.Empty);
                var cleanMatch = _sanitizeRegex.Replace(MatchValue, string.Empty);
                result = cleanInput.Equals(cleanMatch, comparison);
                DebugSink?.Invoke($"[УСЛОВИЕ] Equals «{cleanInput}» = «{cleanMatch}» → {result}");
                break;
            }
            case TextCompareType.StartsWith:
            {
                result = InputValue.StartsWith(MatchValue, comparison);
                DebugSink?.Invoke($"[УСЛОВИЕ] StartsWith «{InputValue}» ^ «{MatchValue}» → {result}");
                break;
            }
            case TextCompareType.IsEmpty:
            {
                result = string.IsNullOrEmpty(InputValue);
                DebugSink?.Invoke($"[УСЛОВИЕ] IsEmpty «{InputValue}» → {result}");
                break;
            }
            case TextCompareType.GreaterThanOrEqual:
            {
                // Приоритет: версионное сравнение (1.2.3), затем числовое (3,14 / 3.14)
                if (Version.TryParse(InputValue?.Trim(), out var verA) &&
                    Version.TryParse(MatchValue?.Trim(),  out var verB))
                {
                    int cmp = verA.CompareTo(verB);
                    result  = cmp >= 0;
                    DebugSink?.Invoke($"[УСЛОВИЕ] Version {verA} >= {verB} (cmp={cmp}) → {result}");
                }
                else if (SmartTryParseDouble(InputValue, out double val) &&
                         SmartTryParseDouble(MatchValue,  out double threshold))
                {
                    result = val >= threshold;
                    DebugSink?.Invoke($"[УСЛОВИЕ] {val} >= {threshold} → {result}");
                }
                else
                {
                    result = false;
                    DebugSink?.Invoke($"[УСЛОВИЕ] >=: не удалось распарсить «{InputValue}» / «{MatchValue}»");
                }
                break;
            }
            case TextCompareType.LessThanOrEqual:
            {
                if (Version.TryParse(InputValue?.Trim(), out var verA) &&
                    Version.TryParse(MatchValue?.Trim(),  out var verB))
                {
                    int cmp = verA.CompareTo(verB);
                    result  = cmp <= 0;
                    DebugSink?.Invoke($"[УСЛОВИЕ] Version {verA} <= {verB} (cmp={cmp}) → {result}");
                }
                else if (SmartTryParseDouble(InputValue, out double val) &&
                         SmartTryParseDouble(MatchValue,  out double threshold))
                {
                    result = val <= threshold;
                    DebugSink?.Invoke($"[УСЛОВИЕ] {val} <= {threshold} → {result}");
                }
                else
                {
                    result = false;
                    DebugSink?.Invoke($"[УСЛОВИЕ] <=: не удалось распарсить «{InputValue}» / «{MatchValue}»");
                }
                break;
            }
            case TextCompareType.Between:
            {
                if (SmartTryParseDouble(InputValue, out double val) &&
                    SmartTryParseDouble(MinValue,    out double min) &&
                    SmartTryParseDouble(MaxValue,    out double max))
                {
                    result = val >= min && val <= max;
                    DebugSink?.Invoke($"[УСЛОВИЕ] {min} ≤ {val} ≤ {max} → {result}");
                }
                else
                {
                    result = false;
                    DebugSink?.Invoke($"[УСЛОВИЕ] Between: не удалось распарсить «{InputValue}» / «{MinValue}» / «{MaxValue}»");
                }
                break;
            }
            default:
                result = false;
                break;
        }

        // ── Транзитный выход: при успехе пробрасываем входное значение дальше ─
        if (result)
        {
            LastOutputValue = new DataPacket { Type = DataType.Text, Payload = InputValue ?? string.Empty };
            DebugSink?.Invoke($"[ВЫХОД] Условие выполнено → транзит: «{InputValue}»");
        }
        else
        {
            LastOutputValue = null;
            DebugSink?.Invoke("[ВЫХОД] Условие не выполнено → null");
        }

        string logDetail = CompareType == TextCompareType.Between
            ? $"«{InputValue}» в диапазоне [{MinValue}, {MaxValue}]"
            : $"«{InputValue}» vs «{MatchValue}»";

        await NodeLogger!.LogInfoAsync(Name,
            $"[CONDITION] [{CompareType}] {logDetail} → {(result ? "ИСТИНА ✓" : "ЛОЖЬ ✗")}")
            .ConfigureAwait(false);

        if (result)
        {
            var _sid = inputPacket?.SessionId ?? Guid.NewGuid();
            var _out = DataBusPacket.Text(_sid);
            DataBus?.Set(_out.SessionId, _out.DataId, InputValue ?? string.Empty);
            return NodeResult.Success(_out);
        }
        return NodeResult.Failure("Условие не выполнено.", inputPacket);
    }
}
