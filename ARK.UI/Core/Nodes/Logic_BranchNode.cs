using System.Globalization;
using ARK.UI.Core.Bus;

namespace ARK.UI.Core.Nodes;

public sealed class Logic_BranchNode : BaseNode
{
    public static readonly LogicDataType[]    AllDataTypes         = Enum.GetValues<LogicDataType>();
    public static readonly LogicCompareType[] TextBoolCompareTypes = [LogicCompareType.Equals, LogicCompareType.NotEquals, LogicCompareType.Contains];
    public static readonly LogicCompareType[] NumberCompareTypes   = [LogicCompareType.Equals, LogicCompareType.NotEquals, LogicCompareType.GreaterThan, LogicCompareType.LessThan];

    public override string DefaultDataInputPropertyName => nameof(InputValue);

    private LogicDataType    _dataType    = LogicDataType.Text;
    private LogicCompareType _compareType = LogicCompareType.Equals;
    private string           _inputValue  = string.Empty;
    private string           _targetValue = string.Empty;
    private bool             _ignoreCase  = true;

    public LogicDataType DataType
    {
        get => _dataType;
        set { if (_dataType != value) { _dataType = value; OnPropertyChanged(); } }
    }

    public LogicCompareType CompareType
    {
        get => _compareType;
        set { if (_compareType != value) { _compareType = value; OnPropertyChanged(); } }
    }

    public string InputValue
    {
        get => _inputValue;
        set { if (_inputValue != value) { _inputValue = value; OnPropertyChanged(); } }
    }

    public string TargetValue
    {
        get => _targetValue;
        set { if (_targetValue != value) { _targetValue = value; OnPropertyChanged(); } }
    }

    public bool IgnoreCase
    {
        get => _ignoreCase;
        set { if (_ignoreCase != value) { _ignoreCase = value; OnPropertyChanged(); } }
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            var _s = _raw as string ?? _raw?.ToString();
            if (_s is not null) InputValue = _s;
        }

        bool result = DataType switch
        {
            LogicDataType.Text    => CompareText(InputValue, TargetValue),
            LogicDataType.Number  => CompareNumber(InputValue, TargetValue),
            LogicDataType.Boolean => CompareBool(InputValue, TargetValue),
            _                     => false
        };

        await NodeLogger!.LogInfoAsync(Name,
            $"[ЛОГИКА] Сравнение ({InputValue} {CompareType} {TargetValue}) → Результат: {result}")
            .ConfigureAwait(false);

        return result
            ? NodeResult.Success(inputPacket)
            : NodeResult.Failure("Условие не выполнено.", inputPacket);
    }

    private bool CompareText(string a, string b)
    {
        var cmp = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return CompareType switch
        {
            LogicCompareType.Equals    =>  string.Equals(a, b, cmp),
            LogicCompareType.NotEquals => !string.Equals(a, b, cmp),
            LogicCompareType.Contains  =>  a.Contains(b, cmp),
            _                          => false
        };
    }

    // Нормализует разделитель (запятая → точка) и парсит через InvariantCulture.
    private static bool SmartTryParseDouble(string? input, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        return double.TryParse(input.Replace(',', '.').Trim(),
            NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private bool CompareNumber(string a, string b)
    {
        // Приоритет: версионное сравнение (1.2.3.4), затем числовое (3,14 / 3.14)
        if (Version.TryParse(a?.Trim(), out var verA) && Version.TryParse(b?.Trim(), out var verB))
        {
            int comp = verA.CompareTo(verB);
            return CompareType switch
            {
                LogicCompareType.Equals      => comp == 0,
                LogicCompareType.NotEquals   => comp != 0,
                LogicCompareType.GreaterThan => comp > 0,
                LogicCompareType.LessThan    => comp < 0,
                _                            => false
            };
        }

        if (!SmartTryParseDouble(a, out double da)) return false;
        if (!SmartTryParseDouble(b, out double db)) return false;
        return CompareType switch
        {
            LogicCompareType.Equals      => Math.Abs(da - db) < 1e-10,
            LogicCompareType.NotEquals   => Math.Abs(da - db) >= 1e-10,
            LogicCompareType.GreaterThan => da > db,
            LogicCompareType.LessThan    => da < db,
            _                            => false
        };
    }

    private bool CompareBool(string a, string b)
    {
        bool ba = ParseBoolValue(a);
        bool bb = ParseBoolValue(b);
        return CompareType switch
        {
            LogicCompareType.Equals    => ba == bb,
            LogicCompareType.NotEquals => ba != bb,
            _                          => false
        };
    }

    private static bool ParseBoolValue(string s) =>
        bool.TryParse(s, out var b) ? b : s is "1" or "yes";
}
