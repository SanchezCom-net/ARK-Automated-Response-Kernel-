using System.Text.RegularExpressions;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

/// <summary>
/// Управляет активацией системы по ключевым словам ("Виктория", "Викторию", и т.п.).
///
/// Логика состояний:
///   Idle   — команды из SpeechTriggerService игнорируются.
///   Active — все распознанные команды обрабатываются (ActivationTimeoutMs мс после активации).
///
/// Поиск по границе слова (\b) исключает ложные срабатывания внутри длинных слов.
/// В .NET \b корректно работает с Unicode (кириллицей): ПодвигВиктория ≠ совпадение.
///
/// Паттерны компилируются лениво (Lazy) при первом вызове Evaluate — ПОСЛЕ того,
/// как ConfigService.LoadAsync() загрузил appsettings.json из файла.
/// </summary>
public sealed class TriggerService : ITriggerService
{
    private const string Component = "TriggerService";

    private readonly ILogService    _logger;
    private readonly IConfigService _configService;

    // Lazy: компиляция откладывается до первого Evaluate(),
    // который всегда вызывается ПОСЛЕ ConfigService.LoadAsync().
    private readonly Lazy<(string Keyword, Regex Pattern)[]> _patterns;

    private readonly System.Threading.Timer _timer;
    private volatile bool _isActive;

    public TriggerState State    => _isActive ? TriggerState.Active : TriggerState.Idle;
    public bool         IsActive => _isActive;

    public event System.Action<string>? Activated;
    public event System.Action?         Deactivated;

    public TriggerService(ILogService logger, IConfigService configService)
    {
        _logger        = logger;
        _configService = configService;

        _patterns = new Lazy<(string, Regex)[]>(
            CompilePatterns, LazyThreadSafetyMode.ExecutionAndPublication);

        _timer = new System.Threading.Timer(OnTimeout, null, Timeout.Infinite, Timeout.Infinite);
    }

    // ── Публичный API ─────────────────────────────────────────────────────────

    public bool Evaluate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return _isActive;

        var keyword = FindKeyword(text);
        if (keyword is not null)
        {
            bool wasIdle = !_isActive;
            _isActive = true;

            // Перезапуск (или первый запуск) таймера истечения активации
            _timer.Change(
                TimeSpan.FromMilliseconds(_configService.AppSettings.Trigger.ActivationTimeoutMs),
                Timeout.InfiniteTimeSpan);

            if (wasIdle)
            {
                _ = _logger.LogInfoAsync(Component, $"[Trigger] Активация по имени: {keyword}");
                Activated?.Invoke(keyword);
            }

            return true;
        }

        return _isActive; // нет ключевого слова: true только если уже Active
    }

    // ── Внутренняя логика ─────────────────────────────────────────────────────

    private string? FindKeyword(string text)
    {
        foreach (var (keyword, pattern) in _patterns.Value)
        {
            if (pattern.IsMatch(text))
                return keyword;
        }
        return null;
    }

    private (string Keyword, Regex Pattern)[] CompilePatterns()
    {
        var keywords = _configService.AppSettings.Trigger.ActivationKeywords;
        var result   = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => (k, new Regex(
                $@"\b{Regex.Escape(k)}\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)))
            .ToArray();

        _ = _logger.LogInfoAsync(Component,
            $"[Trigger] Паттерны скомпилированы: [{string.Join(", ", keywords)}]. " +
            $"Таймаут активации: {_configService.AppSettings.Trigger.ActivationTimeoutMs / 1000} сек.");

        return result;
    }

    private void OnTimeout(object? _)
    {
        _isActive = false;
        _ = _logger.LogInfoAsync(Component,
            $"[Trigger] Активация истекла ({_configService.AppSettings.Trigger.ActivationTimeoutMs / 1000} сек) — Idle.");
        Deactivated?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
