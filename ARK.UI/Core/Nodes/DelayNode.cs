using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Nodes;

public sealed class DelayNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(DelayMilliseconds);

    private int  _delayMs    = 1000;
    private bool _isRandom   = false;
    private int  _minDelayMs = 500;
    private int  _maxDelayMs = 1500;

    public int DelayMilliseconds
    {
        get => _delayMs;
        set { if (_delayMs != value) { _delayMs = value; OnPropertyChanged(); } }
    }

    public bool IsDelayRandom
    {
        get => _isRandom;
        set { if (_isRandom != value) { _isRandom = value; OnPropertyChanged(); } }
    }

    public int MinDelayMilliseconds
    {
        get => _minDelayMs;
        set { if (_minDelayMs != value) { _minDelayMs = value; OnPropertyChanged(); } }
    }

    public int MaxDelayMilliseconds
    {
        get => _maxDelayMs;
        set { if (_maxDelayMs != value) { _maxDelayMs = value; OnPropertyChanged(); } }
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        // Input: int напрямую, string-fallback с предупреждением при невалидном значении.
        // Setter DelayMilliseconds автоматически вызывает OnPropertyChanged → TextBox обновляется.
        bool hasInput = TryApplyContextInput<int>(nameof(DelayMilliseconds), v => DelayMilliseconds = v);
        if (!hasInput)
        {
            TryApplyContextInput<string>(nameof(DelayMilliseconds), v =>
            {
                if (int.TryParse(v, out int parsed))
                    DelayMilliseconds = parsed;
                else
                    _ = logger.LogWarningAsync(Name,
                        $"[ТАЙМЕР] Получено невалидное текстовое значение задержки '{v}'. Используется стандартная задержка.");
            });
        }

        int finalDelay;
        if (IsDelayRandom)
        {
            int min = Math.Min(MinDelayMilliseconds, MaxDelayMilliseconds);
            int max = Math.Max(MinDelayMilliseconds, MaxDelayMilliseconds);
            finalDelay = Random.Shared.Next(min, max + 1);
        }
        else
        {
            finalDelay = DelayMilliseconds;
        }

        LastOutputValue = finalDelay;

        await logger.LogInfoAsync(Name,
            $"[ТАЙМЕР] Запущено ожидание задержки: {finalDelay} мс.").ConfigureAwait(false);

        await Task.Delay(finalDelay, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
