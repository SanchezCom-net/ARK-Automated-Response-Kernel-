using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using WpfApp       = System.Windows.Application;
using WpfClipboard = System.Windows.Clipboard;
using ARK.UI.Core.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SanitizeUtil = ARK.UI.Core.TextSanitizer;

namespace ARK.UI.Core.Services;

public sealed class MacroScheduler : IMacroScheduler, IDisposable
{
    private const string Component = "MacroScheduler";

    private readonly IServiceProvider          _serviceProvider;
    private readonly IInputService             _inputService;
    private readonly IConfigService            _configService;
    private readonly ILogService               _logger;
    private readonly IWindowTrackerService     _windowTracker;
    private readonly ISpeechTriggerService     _speechService;
    private readonly IOllamaBridgeService      _ollamaService;
    private readonly IOverlayService           _overlayService;
    private readonly ISpeechSynthesisService   _ttsService;
    private readonly IVisionService            _visionService;
    private readonly IUiAutomationService      _uiAutomationService;
    private readonly IActionService            _actionService;
    private readonly IQueueService             _queueService;
    private readonly IProcessWatcher           _processWatcher;

    // ── Очереди регионов ──────────────────────────────────────────────────
    // MacroExecutionContext? — начальный контекст (например, SpeechRecognizedText для голосовых макросов)
    private readonly Dictionary<string, Queue<(MacroEntry Macro, BaseNode Node, MacroExecutionContext? InitialContext)>> _queues = new();
    private readonly HashSet<string>  _running   = new();
    private readonly object           _queueLock = new();

    // ── Приоритетный стек исполнения ─────────────────────────────────────
    // Уровень 1 — System Level (IgnoreAllRestrictions): мгновенный запуск, обходит всё
    private int             _systemLevelCount = 0;   // Interlocked
    // Уровень 2 — Exclusive (BlockOthersOnExecution): монополист, блокирует Standard
    private volatile bool   _exclusiveRunning = false;
    private readonly object _stateLock        = new();

    // Ожидающие запуска: Exclusive и Standard, заблокированные монополистом
    private sealed record SystemPending(MacroEntry Macro, BaseNode Node, bool IsExclusive);
    private readonly Queue<SystemPending> _systemPendingQueue = new();

    private CancellationTokenSource? _cts;
    private AppProfile?              _activeProfile;
    // Последнее активное окно (кэш из событий IWindowTrackerService — у трекера нет pull-API)
    private ActiveWindowInfo?        _lastWindowInfo;
    // Хоткеи пользовательских макросов деактивированы до явного EnableHotkeys().
    // Это предотвращает случайный запуск макросов до завершения полной инициализации приложения.
    private volatile bool _hotkeysEnabled;

    // Кэш валидных триггеров: ID макроса → набор ID нод, прямо подключённых к TriggerRootNode.
    // Перестраивается при EnableHotkeys() и при смене активного профиля.
    // Volatile-ссылка: сборка строится в новом словаре и атомарно подменяется.
    private volatile IReadOnlyDictionary<Guid, HashSet<Guid>> _validRootConnected
        = new Dictionary<Guid, HashSet<Guid>>();

    // Окно внимания: 15 сек после окончания озвучки ИИ пользователь может говорить без имени ассистента
    private DateTime _lastTtsFinishedTime = DateTime.MinValue;
    private bool IsAttentionWindowActive
        => (DateTime.UtcNow - _lastTtsFinishedTime).TotalSeconds <= 15;

    public event EventHandler<string?>? ActiveProfileChanged;
    public string? ActiveProfileName { get; private set; }

    public MacroScheduler(
        IServiceProvider        serviceProvider,
        IInputService           inputService,
        IConfigService          configService,
        ILogService             logger,
        IWindowTrackerService   windowTracker,
        ISpeechTriggerService   speechService,
        IOllamaBridgeService    ollamaService,
        IOverlayService         overlayService,
        ISpeechSynthesisService ttsService,
        IVisionService          visionService,
        IUiAutomationService    uiAutomationService,
        IActionService          actionService,
        IQueueService           queueService,
        IProcessWatcher         processWatcher)
    {
        _serviceProvider = serviceProvider;
        _inputService    = inputService;
        _configService   = configService;
        _logger          = logger;
        _windowTracker   = windowTracker;
        _speechService   = speechService;
        _ollamaService   = ollamaService;
        _overlayService  = overlayService;
        _ttsService           = ttsService;
        _visionService        = visionService;
        _uiAutomationService  = uiAutomationService;
        _actionService        = actionService;
        _queueService         = queueService;
        _processWatcher       = processWatcher;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _windowTracker.ActiveWindowChanged    += OnActiveWindowChanged;
        _inputService.KeyDown                 += OnGlobalKeyDown;
        _speechService.SpeechRecognized       += OnSpeechRecognizedAsync;
        _processWatcher.ProcessStarted        += OnProcessStarted;

        _ = _logger.LogInfoAsync(Component,
            "MacroScheduler запущен: слушает смену окон, глобальные горячие клавиши и процессы ОС. Ожидание EnableHotkeys().");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Разрешает обработку пользовательских хоткеев. Вызывается из App после полной инициализации
    /// (загрузки профилей, регистрации хуков). Без этого вызова KeyDown-события игнорируются.
    /// </summary>
    public void EnableHotkeys()
    {
        _hotkeysEnabled = true;
        _ = RebuildAllValidTriggersAsync(_cts?.Token ?? CancellationToken.None);
        _ = _logger.LogInfoAsync(Component,
            "Хоткеи пользовательских макросов активированы. Система готова к работе.");
    }

    public void Stop()
    {
        _windowTracker.ActiveWindowChanged    -= OnActiveWindowChanged;
        _inputService.KeyDown                 -= OnGlobalKeyDown;
        _speechService.SpeechRecognized       -= OnSpeechRecognizedAsync;
        _processWatcher.ProcessStarted        -= OnProcessStarted;

        if (_cts is not null)
        {
            try
            {
                if (!_cts.IsCancellationRequested)
                    _cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }
        }

        _ = _logger.LogInfoAsync(Component, "MacroScheduler остановлен.");
    }

    // ── Кэш валидности триггеров (Root-Path Validation) ──────────────────────

    // Перестраивает кэш для всех профилей текущей конфигурации.
    // Только ноды, прямо подключённые к выходу TriggerRootNode, признаются легитимными.
    private async Task RebuildAllValidTriggersAsync(CancellationToken ct)
    {
        var newCache = new Dictionary<Guid, HashSet<Guid>>();

        foreach (var profile in _configService.Current.Profiles)
        {
            foreach (var macro in GetAllMacros(profile))
                await BuildMacroTriggerEntryAsync(macro, newCache, ct).ConfigureAwait(false);

            foreach (var region in GetAllRegions(profile))
                foreach (var macro in region.Macros)
                    await BuildMacroTriggerEntryAsync(macro, newCache, ct).ConfigureAwait(false);
        }

        _validRootConnected = newCache;
    }

    private async Task BuildMacroTriggerEntryAsync(
        MacroEntry macro,
        Dictionary<Guid, HashSet<Guid>> cache,
        CancellationToken ct)
    {
        if (!macro.IsEnabled || macro.VisualNodes.Count == 0) return;

        var triggerRoot = macro.VisualNodes
            .Select(vn => vn.LogicalNode)
            .OfType<TriggerRootNode>()
            .FirstOrDefault();

        if (triggerRoot is null)
        {
            bool hasTriggers = macro.VisualNodes.Any(vn =>
                vn.LogicalNode is HotkeyTriggerNode or SpeechTriggerNode);

            if (hasTriggers)
                await _logger.LogWarningAsync(Component,
                    $"[MacroScheduler] ПРЕДУПРЕЖДЕНИЕ: Макрос '{macro.Name}' (ID: {macro.Id}) " +
                    "не имеет ноды 'СТАРТ' (TriggerRootNode). Его триггеры не зарегистрированы.")
                    .ConfigureAwait(false);
            return;
        }

        // Набор ID нод, прямо подключённых к TriggerRootNode через OnSuccess-провод
        var rootChildren = macro.VisualConnections
            .Where(c => c.SourceNodeId == triggerRoot.Id && !c.IsErrorRoute && !c.IsDataRoute)
            .Select(c => c.TargetNodeId)
            .ToHashSet();

        cache[macro.Id] = rootChildren;

        // Формируем списки активных и игнорируемых триггеров для лога
        var activeTriggers  = new List<string>();
        var ignoredTriggers = new List<string>();

        foreach (var vn in macro.VisualNodes)
        {
            var node = vn.LogicalNode;
            if (node is not (HotkeyTriggerNode or SpeechTriggerNode)) continue;

            (rootChildren.Contains(node.Id) ? activeTriggers : ignoredTriggers)
                .Add($"'{node.Name}' [{node.GetType().Name}]");
        }

        if (activeTriggers.Count > 0 || ignoredTriggers.Count > 0)
        {
            var activeList = activeTriggers.Count > 0
                ? string.Join(", ", activeTriggers) : "(нет)";

            await _logger.LogInfoAsync(Component,
                $"[MacroScheduler] Макрос '{macro.Name}': Найдена точка входа 'СТАРТ'. " +
                $"Активные зарегистрированные триггеры: {activeList}.")
                .ConfigureAwait(false);

            foreach (var ignored in ignoredTriggers)
                await _logger.LogWarningAsync(Component,
                    $"[MacroScheduler] ПРЕДУПРЕЖДЕНИЕ: Триггер {ignored} макроса '{macro.Name}' " +
                    "проигнорирован, так как не подключен к выходу ноды 'СТАРТ'.")
                    .ConfigureAwait(false);
        }
    }

    // Нода считается легитимным триггером, только если она прямо подключена к TriggerRootNode.
    private bool IsRootConnected(MacroEntry macro, Guid nodeId)
        => _validRootConnected.TryGetValue(macro.Id, out var set) && set.Contains(nodeId);

    // Макрос имеет валидную запись в кэше (TriggerRootNode найдена и обработана).
    private bool HasRootEntry(MacroEntry macro)
        => _validRootConnected.ContainsKey(macro.Id);

    // ── Обработка смены активного окна ────────────────────────────────────────

    private void OnActiveWindowChanged(object? sender, ActiveWindowInfo info)
        => _ = HandleWindowChangedAsync(info, _cts?.Token ?? CancellationToken.None);

    private async Task HandleWindowChangedAsync(ActiveWindowInfo info, CancellationToken ct)
    {
        _lastWindowInfo = info;
        try
        {
            var profiles = _configService.Current.Profiles;

            AppProfile? match =
                profiles.FirstOrDefault(p =>
                    !p.IsGlobal
                    && string.Equals(p.TargetProcessName, info.ProcessName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(p.WindowTitleFilter)
                        || info.WindowTitle.Contains(p.WindowTitleFilter, StringComparison.OrdinalIgnoreCase))
                    && (!p.FocusRequired || true))
                ?? profiles.FirstOrDefault(p => p.IsGlobal);

            var prevProfile = _activeProfile;
            _activeProfile  = match;

            if (match != prevProfile)
                await RebuildAllValidTriggersAsync(ct).ConfigureAwait(false);

            if (match is not null)
            {
                ActiveProfileName = match.FriendlyName;
                ActiveProfileChanged?.Invoke(this, match.FriendlyName);

                await _logger.LogInfoAsync(Component,
                    $"Активный профиль: [{match.FriendlyName}] → процесс {info.ProcessName}, заголовок \"{info.WindowTitle}\".")
                    .ConfigureAwait(false);
            }
            else
            {
                ActiveProfileName = null;
                ActiveProfileChanged?.Invoke(this, null);

                await _logger.LogInfoAsync(Component,
                    $"Профиль для '{info.ProcessName}' не найден. Активны только глобальные макросы.")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                $"Ошибка обработки смены окна '{info.ProcessName}'.", ex).ConfigureAwait(false);
        }
    }

    // ── Авто-активация профиля при запуске процесса ───────────────────────────

    private void OnProcessStarted(object? sender, ProcessWatcherEventArgs e)
        => _ = HandleProcessStartedAsync(e.ProcessName, _cts?.Token ?? CancellationToken.None);

    private async Task HandleProcessStartedAsync(string processName, CancellationToken ct)
    {
        try
        {
            var profiles = _configService.Current.Profiles;
            var matching = profiles
                .Where(p => !p.IsGlobal
                    && string.Equals(p.TargetProcessName, processName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matching.Count == 0) return;

            foreach (var profile in matching)
            {
                var macroCount = GetAllMacros(profile).Count()
                               + GetAllRegions(profile).Sum(r => r.Macros.Count);

                await _logger.LogInfoAsync(Component,
                    $"[ПРОЦЕССЫ] Запущен '{processName}'. " +
                    $"Профиль '{profile.FriendlyName}' загружен в активный статус ({macroCount} макросов).")
                    .ConfigureAwait(false);

                // Если активный профиль не установлен — назначаем этот без ожидания смены окна.
                if (_activeProfile is null)
                {
                    _activeProfile    = profile;
                    ActiveProfileName = profile.FriendlyName;
                    ActiveProfileChanged?.Invoke(this, profile.FriendlyName);
                }
            }
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                $"Ошибка обработки запуска процесса '{processName}'.", ex).ConfigureAwait(false);
        }
    }

    // ── Глобальные горячие клавиши ────────────────────────────────────────────

    private void OnGlobalKeyDown(object? sender, KeyHookEventArgs e)
    {
        if (_cts is null || !_hotkeysEnabled) return;
        _ = HandleHotKeyAsync(e.Key, e.Modifiers, _cts.Token);
    }

    private async Task HandleHotKeyAsync(Key key, ModifierKeys modifiers, CancellationToken ct)
    {
        if (key == Key.None) return;

        var profiles = _configService.Current.Profiles;

        IEnumerable<AppProfile> candidates = _activeProfile is not null
            ? profiles.Where(p => p == _activeProfile || p.IsGlobal)
            : profiles.Where(p => p.IsGlobal);

        // Legacy (ProfileRegion) и новые (прямые макросы профиля/папок)
        List<(ProfileRegion Region, MacroEntry Macro, BaseNode Node)>? legacyMatches = null;
        List<(MacroEntry Macro, BaseNode Node)>?                       newMatches    = null;

        foreach (var profile in candidates)
        {
            // Legacy path: регионы внутри профиля
            foreach (var region in GetAllRegions(profile))
            {
                foreach (var macro in region.Macros)
                {
                    if (!macro.IsEnabled) continue;
                    if (!HasRootEntry(macro)) continue; // нет TriggerRootNode → игнорируем
                    foreach (var vn in macro.VisualNodes)
                    {
                        if (vn.LogicalNode is not HotkeyTriggerNode tn) continue;
                        if (tn.HotKey == Key.None) continue;
                        if (tn.HotKey != key || tn.HotKeyModifiers != modifiers) continue;
                        if (!IsRootConnected(macro, tn.Id)) continue; // не подключён к СТАРТ → игнорируем
                        legacyMatches ??= [];
                        legacyMatches.Add((region, macro, vn.LogicalNode));
                    }
                }
            }

            // New path: прямые макросы в профиле / папках
            foreach (var macro in GetAllMacros(profile))
            {
                if (!macro.IsEnabled) continue;

                // TriggerRootNode-aware путь: триггеры — спутниковые ноды, сканируем напрямую
                var triggerRoot = macro.VisualNodes
                    .Select(vn => vn.LogicalNode)
                    .OfType<TriggerRootNode>()
                    .FirstOrDefault();

                if (triggerRoot is not null)
                {
                    foreach (var vn in macro.VisualNodes)
                    {
                        if (vn.LogicalNode is not HotkeyTriggerNode tn) continue;
                        if (tn.HotKey == Key.None || tn.HotKey != key || tn.HotKeyModifiers != modifiers) continue;
                        if (!IsRootConnected(macro, tn.Id)) continue; // не подключён к СТАРТ → игнорируем
                        newMatches ??= [];
                        newMatches.Add((macro, triggerRoot));
                    }
                }
                // Макросы без TriggerRootNode не активируются (архитектурное правило Root-Path Validation).
                // Предупреждение логируется в BuildMacroTriggerEntryAsync при перестройке кэша.
            }
        }

        int totalMatches = (legacyMatches?.Count ?? 0) + (newMatches?.Count ?? 0);
        if (totalMatches == 0) return;

        await _logger.LogInfoAsync(Component,
            $"Нажата горячая клавиша {modifiers}+{key}. Найдено совпадений: {totalMatches}.")
            .ConfigureAwait(false);

        // Legacy-путь: передаём в старый API (он сам разберёт Concurrent/StrictQueue)
        if (legacyMatches is not null)
        {
            foreach (var (region, macro, node) in legacyMatches
                .OrderBy(m => m.Macro.QueuePriority == 0 ? int.MaxValue : m.Macro.QueuePriority))
            {
                await _logger.LogInfoAsync(Component,
                    $"[LEGACY] Запуск '{node.Name}' в регион '{region.RegionName}'.")
                    .ConfigureAwait(false);
                EnqueueMacro(region, macro, node);
            }
        }

        // New-путь: через универсальный EnqueueMacro с поддержкой эксклюзивности
        if (newMatches is not null)
        {
            foreach (var (macro, node) in newMatches
                .OrderBy(m => m.Macro.QueuePriority == 0 ? int.MaxValue : m.Macro.QueuePriority))
            {
                await _logger.LogInfoAsync(Component,
                    $"Запуск '{node.Name}' (QueuePriority={macro.QueuePriority}).")
                    .ConfigureAwait(false);
                EnqueueMacro(macro, node);
            }
        }
    }

    // ── Голосовые команды ─────────────────────────────────────────────────────────

    // Триггеры захвата экрана: сверяются с санитизированным текстом (lowercase, без пунктуации),
    // поэтому многословные фразы вида "что на экране" корректно матчатся через Contains
    private static readonly string[] ScreenKeywords =
        ["посмотри", "глянь", "что на экране", "видишь на экране",
         "экран", "скриншот", "смотри", "вижу", "screen", "screenshot"];

    private static bool IsScreenRequest(string cleanText)
        => ScreenKeywords.Any(k => cleanText.Contains(k, StringComparison.Ordinal));

    // Детектор голосового запроса зрения: при триггере захватывает экран,
    // конвертирует JPEG → Base64 и вкладывает в Images сообщения для Ollama.
    // К каждому запросу прикрепляется скрытый системный контекст (окно + буфер + UIA).
    private async Task<(ChatMessage Message, bool IsAllowed)> BuildUserMessageAsync(string rawText, CancellationToken ct)
    {
        var (contextXml, isAllowed) = await BuildSystemContextAsync(rawText, ct).ConfigureAwait(false);
        var content    = contextXml.Length > 0 ? $"{contextXml}\n\n{rawText}" : rawText;

        if (!IsScreenRequest(SanitizeSpeechText(rawText)))
            return (new ChatMessage("user", content), isAllowed);

        await _logger.LogInfoAsync(Component,
            "[VISION] Зафиксирован голосовой триггер захвата экрана.").ConfigureAwait(false);

        try
        {
            var jpeg = await _visionService.CapturePrimaryScreenAsync(ct).ConfigureAwait(false);
            await _logger.LogInfoAsync(Component,
                $"[VISION] Скриншот захвачен ({jpeg.Length / 1024} КБ JPEG), сконвертирован в Base64.")
                .ConfigureAwait(false);
            return (new ChatMessage("user", content, [Convert.ToBase64String(jpeg)]), isAllowed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                "[VISION] Ошибка захвата экрана. Запрос отправляется без изображения.", ex)
                .ConfigureAwait(false);
            return (new ChatMessage("user", content), isAllowed);
        }
    }

    // ── System Context Injector ───────────────────────────────────────────────────

    // Потолок текста буфера обмена в контексте — защита промпта от мегабайтных вставок
    private const int ClipboardContextLimit = 2000;

    // Собирает скрытый XML-блок <system_context>: активное окно + буфер обмена + UIA-элементы.
    // Возвращает (xml, isAllowed): isAllowed = true если в тексте обнаружен запрос на автоматизацию.
    // Любой сбой деградирует до ("", false) — голосовой запрос уходит без контекста.
    private async Task<(string Xml, bool IsAllowed)> BuildSystemContextAsync(string rawText, CancellationToken ct)
    {
        try
        {
            var window    = _lastWindowInfo;
            var isAllowed = IsActionIntent(rawText);

            // STA-безопасное чтение буфера обмена через Dispatcher
            var clipboardText = string.Empty;
            try
            {
                clipboardText = await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                    WpfClipboard.ContainsText() ? WpfClipboard.GetText() : string.Empty);
            }
            catch (Exception ex)
            {
                // CLIPBRD_E_CANT_OPEN: буфер захвачен другим процессом — контекст без него
                await _logger.LogWarningAsync(Component,
                    $"[CONTEXT] Буфер обмена недоступен: {ex.Message}").ConfigureAwait(false);
            }
            if (clipboardText.Length > ClipboardContextLimit)
                clipboardText = clipboardText[..ClipboardContextLimit];

            var elements = await _uiAutomationService.GetClickableElementsAsync(ct).ConfigureAwait(false);

            var xml = new XElement("system_context",
                new XElement("active_window",
                    new XAttribute("process", window?.ProcessName ?? "unknown"),
                    new XAttribute("title",   window?.WindowTitle ?? string.Empty)),
                new XElement("clipboard", new XCData(clipboardText)),
                new XElement("authorization",
                    new XAttribute("allowed", isAllowed ? "true" : "false")),
                new XElement("screen_buttons",
                    elements.Select(e => new XElement("button",
                        new XAttribute("name", e.Name),
                        new XAttribute("type", e.ControlType),
                        new XAttribute("x", (int)e.CenterX),
                        new XAttribute("y", (int)e.CenterY)))));

            await _logger.LogInfoAsync(nameof(MacroScheduler),
                $"[CONTEXT] Авторизация команд ИИ: {(isAllowed ? "РАЗРЕШЕНО" : "ЗАБЛОКИРОВАНО")}")
                .ConfigureAwait(false);
            await _logger.LogInfoAsync(nameof(MacroScheduler),
                "[CONTEXT] Системный контекст (Окно + Буфер + UIA) успешно внедрен в запрос.")
                .ConfigureAwait(false);

            return (xml.ToString(), isAllowed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                "[CONTEXT] Ошибка сбора системного контекста. Запрос уходит без него.", ex)
                .ConfigureAwait(false);
            return (string.Empty, false);
        }
    }

    // ── Win32 Action Tool Agent: исполнение XML-команд из ответа ИИ ───────────────

    private static readonly Regex AgentTagNameRegex =
        new(@"^<\s*(\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AgentAttrRegex =
        new("(\\w+)\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Выполняет полностью закрывшийся командный тег, перехваченный AgentCommandFilter.
    // Fire-and-forget из цикла стриминга: ошибки логируются, стрим не прерывается.
    private async Task ExecuteAgentCommandAsync(string rawTag, bool isAllowed, CancellationToken ct)
    {
        try
        {
            var nameMatch = AgentTagNameRegex.Match(rawTag);
            if (!nameMatch.Success) return;
            var commandName = nameMatch.Groups[1].Value;

            if (!isAllowed)
            {
                await _logger.LogWarningAsync(Component,
                    $"[AGENT] [BLOCKED] Попытка несанкционированного действия '{commandName}' заблокирована (нет явного запроса пользователя на автоматизацию).")
                    .ConfigureAwait(false);
                return;
            }

            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in AgentAttrRegex.Matches(rawTag))
                attrs[m.Groups[1].Value] = WebUtility.HtmlDecode(m.Groups[2].Value);

            switch (commandName.ToLowerInvariant())
            {
                case "click":
                {
                    if (!TryGetAgentPoint(attrs, out var x, out var y))
                    {
                        await _logger.LogWarningAsync(Component,
                            $"[AGENT] Команда клика без валидных координат: {rawTag}").ConfigureAwait(false);
                        return;
                    }
                    attrs.TryGetValue("name", out var name);
                    await _logger.LogInfoAsync(Component,
                        $"[AGENT] Выполнение команды клика по элементу '{name}' (X: {x}, Y: {y})")
                        .ConfigureAwait(false);
                    await _actionService.ClickAsync(x, y, ct).ConfigureAwait(false);
                    break;
                }

                case "type":
                {
                    attrs.TryGetValue("text", out var text);
                    var hasPoint = TryGetAgentPoint(attrs, out var x, out var y);
                    await _logger.LogInfoAsync(Component,
                        $"[AGENT] Выполнение команды ввода текста в координаты (X: {x}, Y: {y})")
                        .ConfigureAwait(false);
                    if (hasPoint)
                    {
                        await _actionService.ClickAsync(x, y, ct).ConfigureAwait(false);
                        await Task.Delay(100, ct).ConfigureAwait(false);   // фокус успевает встать
                    }
                    if (!string.IsNullOrEmpty(text))
                        await _actionService.TypeTextAsync(text, ct).ConfigureAwait(false);
                    break;
                }

                case "run":
                {
                    attrs.TryGetValue("path", out var path);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        await _logger.LogWarningAsync(Component,
                            $"[AGENT] Команда запуска без path: {rawTag}").ConfigureAwait(false);
                        return;
                    }
                    attrs.TryGetValue("args", out var args);
                    await _logger.LogInfoAsync(Component,
                        $"[AGENT] Запуск процесса: '{path}' с аргументами '{args}'").ConfigureAwait(false);
                    // UseShellExecute: поддерживает и exe, и URL, и документы
                    Process.Start(new ProcessStartInfo(path, args ?? string.Empty) { UseShellExecute = true });
                    break;
                }

                case "write_clipboard":
                {
                    attrs.TryGetValue("text", out var text);
                    await _logger.LogInfoAsync(Component,
                        "[AGENT] Запись текста в буфер обмена.").ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(text))
                        await WpfApp.Current.Dispatcher.InvokeAsync(() => WpfClipboard.SetText(text));
                    break;
                }

                default:
                    await _logger.LogWarningAsync(Component,
                        $"[AGENT] Неизвестная команда: {rawTag}").ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                $"[AGENT] Ошибка выполнения команды: {rawTag}", ex).ConfigureAwait(false);
        }
    }

    private static bool TryGetAgentPoint(Dictionary<string, string> attrs, out int x, out int y)
    {
        x = 0;
        y = 0;
        return attrs.TryGetValue("x", out var xs) && int.TryParse(xs, out x)
            && attrs.TryGetValue("y", out var ys) && int.TryParse(ys, out y);
    }

    // Разбивает AiAssistantNames (через запятую) на санитизированный массив синонимов.
    // Пример: "Аркаша, Аркадий" → ["аркаша", "аркадий"]
    private string[] GetAssistantNames()
    {
        var raw = _configService.Current.AiAssistantNames;
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(SanitizeSpeechText)
                  .Where(n => n.Length > 0)
                  .ToArray();
    }

    // Проверяет, что фраза начинается с одного из синонимов имени ассистента
    private bool IsAiActivationPhrase(string cleanText)
    {
        foreach (var name in GetAssistantNames())
            if (cleanText.StartsWith(name, StringComparison.Ordinal))
                return true;
        return false;
    }


    // Невербальные токены Whisper внутри фразы: [кашель], [музыка], (дыхание) и т.п.
    private static readonly Regex NonVerbalTokenRegex =
        new(@"[\[\(][^\]\)]*[\]\)]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Проверка наличия хотя бы одной буквы (Unicode) — отличает речь от чистого шума
    private static readonly Regex HasLetterRegex =
        new(@"\p{L}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Схлопывание пробелов для TTS/NonVerbal-методов (SanitizeSpeechText → TextSanitizer.Sanitize)
    private static readonly Regex SpeechSpaceRegex =
        new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Изолированные слова-паразиты, игнорируемые вне активного окна внимания
    private static readonly FrozenSet<string> JunkWords =
        FrozenSet.Create(StringComparer.Ordinal,
            "угу", "ага", "э э", "м м", "э", "м", "да", "нет", "так");

    private static readonly string[] ActionIntentKeywords =
        ["нажми", "кликни", "открой", "запусти", "введи", "напечатай", "скопируй", "вставь",
         "click", "type", "run", "open"];

    private static bool IsActionIntent(string userText)
        => ActionIntentKeywords.Any(k => userText.Contains(k, StringComparison.OrdinalIgnoreCase));

    private async Task OnSpeechRecognizedAsync(string rawText, bool activationNameDetected)
    {
        if (!_hotkeysEnabled) return;

        // Шаг 1: Удаляем невербальные токены Whisper ([кашель], [музыка], (дыхание) и т.п.)
        var cleaned = RemoveNonVerbalTokens(rawText);

        // Если после удаления не осталось ни одной буквы — чистый шум, игнорируем
        if (!HasLetterRegex.IsMatch(cleaned))
        {
            await _logger.LogInfoAsync(nameof(MacroScheduler),
                $"[ГОЛОС] Отклонена невербальная фраза: '{rawText}'. Реальных слов не обнаружено.")
                .ConfigureAwait(false);
            return;
        }

        // Barge-in: пользователь начал говорить — прерываем текущую озвучку
        _ttsService.Stop();

        var cleanText = SanitizeSpeechText(cleaned);
        if (cleanText.Length == 0) return;

        // Фильтр слов-паразитов: блокируем только вне активного диалога
        if (!IsAttentionWindowActive && IsSingleJunkWord(cleanText))
        {
            await _logger.LogInfoAsync(nameof(MacroScheduler),
                $"[ГОЛОС] Отклонено слово-паразит вне диалога: '{rawText}'.")
                .ConfigureAwait(false);
            return;
        }

        // Гейткипер уже подтвердил имя активации и отсёк его — логируем и продолжаем
        if (activationNameDetected)
            await _logger.LogInfoAsync(nameof(MacroScheduler),
                $"[MacroScheduler] Имя успешно распознано и отсечено. Обрабатываем полезную нагрузку: '{cleanText}'")
                .ConfigureAwait(false);

        // Прямая адресация по имени ассистента — пропускаем поиск макросов
        if (IsAiActivationPhrase(cleanText))
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            var (aiMsg, aiAllowed) = await BuildUserMessageAsync(cleaned, ct).ConfigureAwait(false);
            await RouteToAiAsync(aiMsg, aiAllowed, ct).ConfigureAwait(false);
            _lastTtsFinishedTime = DateTime.UtcNow;
            await _logger.LogInfoAsync(Component,
                "[CONVERSATION] Ответ озвучен. Окно внимания открыто на 15 секунд. Вы можете говорить без упоминания имени.")
                .ConfigureAwait(false);
            return;
        }

        var profiles = _configService.Current.Profiles;
        IEnumerable<AppProfile> candidates = _activeProfile is not null
            ? profiles.Where(p => p == _activeProfile || p.IsGlobal)
            : profiles.Where(p => p.IsGlobal);

        // Контекст с распознанным текстом: SpeechTriggerNode читает его для fuzzy-матчинга
        var speechCtx = new MacroExecutionContext();
        speechCtx.Variables["SpeechRecognizedText"] = cleanText;

        int skippedByKeyword  = 0;
        int passedKeywordFilter = 0;

        foreach (var profile in candidates)
        {
            // Legacy path: регионы
            foreach (var region in GetAllRegions(profile))
            {
                foreach (var macro in region.Macros)
                {
                    if (!macro.IsEnabled) continue;
                    var matchedKw = FindKeywordMatch(macro, cleanText);
                    if (matchedKw is null) { skippedByKeyword++; continue; }
                    passedKeywordFilter++;
                    if (matchedKw.Length > 0)
                        await _logger.LogInfoAsync(Component,
                            $"[MacroScheduler] УСПЕХ: Ключевое слово '{matchedKw}' обнаружено! Запускаем макрос '{macro.Name}' (ID: {macro.Id}).")
                            .ConfigureAwait(false);
                    var startNode = await MatchSpeechMacro(macro, cleanText).ConfigureAwait(false);
                    if (startNode is null) continue;
                    await _logger.LogInfoAsync(Component,
                        $"[ГОЛОС] Распознана команда '{rawText}'. Запуск макроса '{macro.Name}'. " +
                        $"Цепочка действий стартует с ноды '{startNode.Name}' (ID: {startNode.Id}).")
                        .ConfigureAwait(false);
                    EnqueueMacro(region, macro, startNode, speechCtx);
                    return;
                }
            }

            // New path: прямые макросы профиля/папок
            foreach (var macro in GetAllMacros(profile))
            {
                if (!macro.IsEnabled) continue;
                var matchedKw = FindKeywordMatch(macro, cleanText);
                if (matchedKw is null) { skippedByKeyword++; continue; }
                passedKeywordFilter++;
                if (matchedKw.Length > 0)
                    await _logger.LogInfoAsync(Component,
                        $"[MacroScheduler] УСПЕХ: Ключевое слово '{matchedKw}' обнаружено! Запускаем макрос '{macro.Name}' (ID: {macro.Id}).")
                        .ConfigureAwait(false);
                var startNode = await MatchSpeechMacro(macro, cleanText).ConfigureAwait(false);
                if (startNode is null) continue;
                await _logger.LogInfoAsync(Component,
                    $"[ГОЛОС] Распознана команда '{rawText}'. Запуск макроса '{macro.Name}'. " +
                    $"Цепочка действий стартует с ноды '{startNode.Name}' (ID: {startNode.Id}).")
                    .ConfigureAwait(false);
                EnqueueMacro(macro, startNode, speechCtx);
                return;
            }
        }

        if (passedKeywordFilter == 0 && skippedByKeyword > 0)
            await _logger.LogInfoAsync(Component,
                $"[MacroScheduler] Голосовой ввод: '{cleanText}'. Ни один макрос не содержит подходящего " +
                "ключевого слова (искали корни в индексах макросов). Запрос отклонен.")
                .ConfigureAwait(false);
        else if (skippedByKeyword > 0)
            await _logger.LogInfoAsync(Component,
                $"[MacroScheduler] Отсеяно по ключевым словам: {skippedByKeyword}. " +
                $"Кандидатов прошло фильтр: {passedKeywordFilter}. Полное сравнение фраз не дало совпадений.")
                .ConfigureAwait(false);
    }

    private async Task RouteToAiAsync(ChatMessage message, bool isAllowed, CancellationToken ct)
    {
        if (!_configService.Current.IsAiEnabled)
        {
            await _logger.LogInfoAsync(Component,
                "[ИИ] ИИ-ассистент отключен пользователем. Запрос проигнорирован.")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await _logger.LogInfoAsync(Component,
                message.Images is { Count: > 0 }
                    ? "[ИИ] Запрос со скриншотом → Ollama. Генерируется ответ..."
                    : "[ИИ] Отправка запроса в Ollama. Генерируется ответ...").ConfigureAwait(false);

            var cfg       = _configService.Current;
            var ttsActive = cfg.SelectedTtsMode == TtsMode.Standard;
            var rawStream = _ollamaService.StreamResponseAsync(message, ct);

            // Канал оверлея: создаётся только при ShowAiSubtitles=true
            Channel<string>? overlayCh = cfg.ShowAiSubtitles
                ? Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true })
                : null;
            Task overlayTask = overlayCh is not null
                ? _overlayService.ShowStreamingTextAsync(overlayCh.Reader.ReadAllAsync(ct), ct)
                : Task.CompletedTask;

            // Канал TTS: предложения озвучиваются последовательно фоновым потребителем
            Channel<string>? ttsCh = ttsActive
                ? Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true })
                : null;
            Task ttsTask = ttsCh is not null
                ? ConsumeTtsQueueAsync(ttsCh.Reader, cfg, ct)
                : Task.CompletedTask;

            try
            {
                var sentBuf     = new StringBuilder();
                var agentFilter = new AgentCommandFilter();

                // Видимый текст (без командных XML-тегов) → оверлей и TTS-буфер
                async Task ForwardVisibleAsync(string visible)
                {
                    if (visible.Length == 0) return;

                    if (overlayCh is not null)
                        await overlayCh.Writer.WriteAsync(visible, ct).ConfigureAwait(false);

                    if (ttsCh is not null)
                    {
                        sentBuf.Append(visible);
                        if (ShouldFlushSentence(sentBuf))
                        {
                            var sentence = SanitizeTextForSpeech(sentBuf.ToString().TrimEnd());
                            sentBuf.Clear();
                            if (!string.IsNullOrWhiteSpace(sentence))
                                await ttsCh.Writer.WriteAsync(sentence, ct).ConfigureAwait(false);
                        }
                    }
                }

                await foreach (var token in rawStream.WithCancellation(ct).ConfigureAwait(false))
                {
                    // Перехват XML-команд агента: технический код не озвучивается и не показывается
                    var (visible, commands) = agentFilter.Process(token);

                    foreach (var rawTag in commands)
                        _ = ExecuteAgentCommandAsync(rawTag, isAllowed, ct);   // мгновенно, не блокируя стрим

                    await ForwardVisibleAsync(visible).ConfigureAwait(false);
                }

                // Незавершённый тег на конце стрима возвращается как обычный текст
                await ForwardVisibleAsync(agentFilter.Flush()).ConfigureAwait(false);

                // Хвост буфера: последняя фраза без знака конца предложения
                if (ttsCh is not null && sentBuf.Length > 0)
                {
                    var tail = SanitizeTextForSpeech(sentBuf.ToString().Trim());
                    if (!string.IsNullOrWhiteSpace(tail))
                        await ttsCh.Writer.WriteAsync(tail, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                overlayCh?.Writer.Complete();
                ttsCh?.Writer.Complete();
                await Task.WhenAll(overlayTask, ttsTask).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component, "[ИИ] Ошибка обращения к Ollama.", ex)
                .ConfigureAwait(false);
        }
    }

    // Последовательно синтезирует предложения из канала — не блокирует поток токенов
    private async Task ConsumeTtsQueueAsync(
        ChannelReader<string> reader, AppConfig cfg, CancellationToken ct)
    {
        // Kokoro: передаём "voiceName.bin" — SpeechSynthesisService распознаёт режим по расширению.
        // Piper: полный путь к модели в Models/TTS/Piper/.
        var voicePath = cfg.SelectedTtsMode == TtsMode.Kokoro
            ? cfg.SelectedTtsVoice + ".bin"
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "TTS",
                           "Piper", cfg.SelectedTtsVoice + ".onnx");

        await foreach (var sentence in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;
            try
            {
                await _ttsService.SpeakAsync(
                    sentence, voicePath, cfg.TtsSpeed, cfg.TtsVolume, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync(Component,
                    "[TTS] Ошибка озвучки предложения.", ex).ConfigureAwait(false);
            }
        }
    }

    // Буфер флашится при обнаружении конца предложения достаточной длины
    private static bool ShouldFlushSentence(StringBuilder sb)
    {
        if (sb.Length < 3) return false;
        char last = sb[sb.Length - 1];
        if (last is '.' or '!' or '?') return true;
        if (last == ':' && sb.Length >= 15) return true;
        if (last == '\n' && sb.Length >= 20) return true;
        return false;
    }

    private static readonly Regex _ttsCodeBlockRegex =
        new(@"```[\s\S]*?```", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _ttsInlineCodeRegex =
        new(@"`([^`]+)`",      RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _ttsBoldItalicRegex =
        new(@"\*{1,3}([^*\n]+)\*{1,3}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _ttsHeadingRegex =
        new(@"(?m)^#{1,6}\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _ttsImageRegex =
        new(@"!\[[^\]]*\]\([^)]+\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _ttsLinkRegex =
        new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Emoji: суррогатные пары (большинство современных смайликов) + BMP-символы (☺ ♪ ✓ и др.)
    private static readonly Regex _ttsEmojiRegex = new(
        @"\p{Cs}|[←-⟿]|[⤀-⧿]|[⬀-⯿]|️|‍",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Текстовые смайлики: :) :( :-D ;) xD =) =D и т.п.
    private static readonly Regex _ttsEmoticonRegex = new(
        @"[;:=8B][-~^]?[)(DdPpOo3@$|\\\/]|[xX][dD]|[xX]\)|>[\._]+<|\^[_.\-]\^",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Оставшиеся спецсимволы Markdown → пробел
    private static readonly Regex _ttsSpecialCharsRegex = new(
        @"[*#~\[\]_|]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Очищает Markdown, эмодзи и спецсимволы перед передачей в piper
    private static string SanitizeTextForSpeech(string text)
    {
        text = _ttsCodeBlockRegex.Replace(text,      string.Empty);
        text = _ttsInlineCodeRegex.Replace(text,     "$1");
        text = _ttsBoldItalicRegex.Replace(text,     "$1");
        text = _ttsHeadingRegex.Replace(text,        string.Empty);
        text = _ttsImageRegex.Replace(text,          string.Empty);
        text = _ttsLinkRegex.Replace(text,           "$1");
        text = _ttsEmojiRegex.Replace(text,          string.Empty);
        text = _ttsEmoticonRegex.Replace(text,       string.Empty);
        text = _ttsSpecialCharsRegex.Replace(text,   " ");
        return SpeechSpaceRegex.Replace(text.Trim(), " ");
    }

    // Удаляет невербальные токены Whisper в скобках, оставляя только настоящие слова
    private static string RemoveNonVerbalTokens(string text)
        => SpeechSpaceRegex.Replace(NonVerbalTokenRegex.Replace(text, " "), " ").Trim();

    // Возвращает true, если санитизированный текст — ровно одно слово-паразит
    private static bool IsSingleJunkWord(string cleanText)
        => JunkWords.Contains(cleanText);

    private static string SanitizeSpeechText(string text)
        => SanitizeUtil.Sanitize(text);

    // Быстрый пре-фильтр по кэшу ключевых слов.
    // Возвращает: "" — нет фильтра (макрос всегда проходит); "слово" — найденное ключевое слово; null — не прошёл.
    // cleanText уже санирован (нижний регистр, без пунктуации) — Ordinal достаточно.
    private static string? FindKeywordMatch(MacroEntry macro, string cleanText)
    {
        if (macro.CachedVoiceKeywords.Count == 0) return string.Empty; // нет фильтра → проходит
        foreach (var kw in macro.CachedVoiceKeywords)
            if (cleanText.Contains(kw, StringComparison.Ordinal))
                return kw; // ключевое слово найдено
        return null; // ни одно не совпало → отсев
    }

    private static bool IsPhraseMatch(string recognizedText, string triggerPhrase)
    {
        if (string.IsNullOrWhiteSpace(triggerPhrase)) return false;
        var sanitized = SanitizeSpeechText(triggerPhrase);
        if (sanitized.Length == 0) return false;
        var recognizedWords = recognizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var triggerWords    = sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return triggerWords.All(tw => recognizedWords.Contains(tw));
    }

    // ── Обход дерева (legacy: регионы; new: прямые макросы) ─────────────────

    private static IEnumerable<ProfileRegion> GetAllRegions(AppProfile profile)
    {
        foreach (var r in profile.Regions)   yield return r;
        foreach (var f in profile.Folders)
            foreach (var r in GetAllRegions(f)) yield return r;
    }

    private static IEnumerable<ProfileRegion> GetAllRegions(VisualFolder folder)
    {
        foreach (var r in folder.Regions)   yield return r;
        foreach (var f in folder.SubFolders)
            foreach (var r in GetAllRegions(f)) yield return r;
    }

    // Обход прямых макросов (новая архитектура: макросы лежат в профиле/папках напрямую)
    private static IEnumerable<MacroEntry> GetAllMacros(AppProfile profile)
    {
        foreach (var m in profile.Macros) yield return m;
        foreach (var f in profile.Folders)
            foreach (var m in GetAllMacros(f)) yield return m;
    }

    private static IEnumerable<MacroEntry> GetAllMacros(VisualFolder folder)
    {
        foreach (var m in folder.Macros) yield return m;
        foreach (var f in folder.SubFolders)
            foreach (var m in GetAllMacros(f)) yield return m;
    }

    // ── Приоритетный стек исполнения ─────────────────────────────────────

    private static Win_BypassQueueNode? GetBypassNode(MacroEntry macro)
        => macro.VisualNodes.Select(vn => vn.LogicalNode).OfType<Win_BypassQueueNode>().FirstOrDefault();

    // ── Постановка в очередь / немедленный запуск (LEGACY API) ───────────

    public void EnqueueMacro(ProfileRegion region, MacroEntry macro, BaseNode startNode, MacroExecutionContext? initialContext = null)
    {
        if (!_hotkeysEnabled) return;

        var bypass = GetBypassNode(macro);
        if (bypass is not null) { EnqueueWithPriority(macro, startNode, bypass); return; }

        StandardEnqueueLegacy(region, macro, startNode, initialContext);
    }

    // ── Постановка в очередь / немедленный запуск (NEW API) ───────────────

    public void EnqueueMacro(MacroEntry macro, BaseNode startNode, MacroExecutionContext? initialContext = null)
    {
        if (!_hotkeysEnabled) return;

        var bypass = GetBypassNode(macro);
        if (bypass is not null) { EnqueueWithPriority(macro, startNode, bypass); return; }

        StandardEnqueueNew(macro, startNode, initialContext);
    }

    // ── Внутренние методы исполнения ──────────────────────────────────────

    // Единый цикл очереди региона (работает и с QueueRegion, и с legacy ProfileRegion)
    private async Task RunRegionQueueAsync(
        string queueKey,
        QueueRegion?    qRegion,
        ProfileRegion?  legacyRegion,
        MacroEntry firstMacro, BaseNode first,
        CancellationToken ct,
        MacroExecutionContext? firstContext = null)
    {
        var currentMacro    = firstMacro;
        BaseNode? current   = first;
        MacroExecutionContext? currentContext = firstContext;

        while (current is not null)
        {
            if (currentMacro.IsEnabled)
                await ExecuteNodeAsync(qRegion, legacyRegion, currentMacro, current, ct, currentContext).ConfigureAwait(false);
            else
            {
                var regionName = qRegion?.Name ?? legacyRegion?.RegionName ?? "—";
                await _logger.LogInfoAsync(Component,
                    $"[{regionName}] Макрос '{current.Name}' пропущен (IsEnabled=false).")
                    .ConfigureAwait(false);
            }

            lock (_queueLock)
            {
                if (_queues.TryGetValue(queueKey, out var queue) && queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    currentMacro    = item.Macro;
                    current         = item.Node;
                    currentContext  = item.InitialContext;
                }
                else
                {
                    _running.Remove(queueKey);
                    current = null;
                }
            }
        }
    }

    // Конкурентное исполнение одного макроса (не участвует в _stateLock — не влияет на приоритеты)
    private async Task ExecuteWithReleaseAsync(
        QueueRegion? qRegion, ProfileRegion? legacyRegion,
        MacroEntry macro, BaseNode startNode,
        CancellationToken ct,
        MacroExecutionContext? initialContext = null)
    {
        await ExecuteNodeAsync(qRegion, legacyRegion, macro, startNode, ct, initialContext).ConfigureAwait(false);
    }

    // ── Приоритетный стек исполнения ─────────────────────────────────────

    // Маршрутизирует макрос с Win_BypassQueueNode в нужный уровень приоритета
    private void EnqueueWithPriority(MacroEntry macro, BaseNode startNode, Win_BypassQueueNode bypass)
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        // ── УРОВЕНЬ 1: System Level — мгновенный запуск, обходит всё ──────────
        if (bypass.IgnoreAllRestrictions)
        {
            if (bypass.BlockOthersOnExecution)
                lock (_stateLock) { _exclusiveRunning = true; }

            Interlocked.Increment(ref _systemLevelCount);
            _ = ExecuteSystemLevelAsync(macro, startNode, bypass.BlockOthersOnExecution, ct);
            _ = _logger.LogInfoAsync(Component,
                $"[SYSTEM] Макрос '{macro.Name}' запущен на системном уровне.");
            return;
        }

        // ── УРОВЕНЬ 2: Exclusive — монополист, ждёт если другой Exclusive или System Level ──
        if (bypass.BlockOthersOnExecution)
        {
            bool canStart;
            lock (_stateLock)
            {
                canStart = !_exclusiveRunning && _systemLevelCount == 0;
                if (canStart) _exclusiveRunning = true;
                else _systemPendingQueue.Enqueue(new SystemPending(macro, startNode, true));
            }
            if (canStart)
            {
                _ = ExecuteExclusiveAsync(macro, startNode, ct);
                _ = _logger.LogInfoAsync(Component, $"[EXCL] Макрос '{macro.Name}' запущен в монопольном режиме.");
            }
            else
                _ = _logger.LogInfoAsync(Component,
                    $"[EXCL] Макрос '{macro.Name}' ожидает: excl={_exclusiveRunning}, sys={_systemLevelCount}");
            return;
        }

        // ── Без флагов: нода есть, но флаги сброшены — обходим регион-очереди
        StandardEnqueueNew(macro, startNode);
    }

    // Standard-запуск с проверкой Exclusive-блокировки (новый QueueRegion API)
    private void StandardEnqueueNew(MacroEntry macro, BaseNode startNode, MacroExecutionContext? initialContext = null)
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        lock (_stateLock)
        {
            if (_exclusiveRunning)
            {
                _systemPendingQueue.Enqueue(new SystemPending(macro, startNode, false));
                _ = _logger.LogInfoAsync(Component,
                    $"Макрос '{macro.Name}' ожидает завершения монополиста.");
                return;
            }
        }

        QueueRegion? qRegion = macro.RegionId.HasValue
            ? _queueService.GetRegionById(macro.RegionId.Value)
            : null;

        if (qRegion is null || qRegion.ExecutionMode == "Concurrent")
        {
            _ = ExecuteWithReleaseAsync(qRegion, null, macro, startNode, ct, initialContext);
            return;
        }

        var key = qRegion.Id.ToString();
        lock (_queueLock)
        {
            if (!_queues.TryGetValue(key, out var queue))
                _queues[key] = queue = new Queue<(MacroEntry, BaseNode, MacroExecutionContext?)>();
            if (_running.Contains(key))
            {
                queue.Enqueue((macro, startNode, initialContext));
                _ = _logger.LogInfoAsync(Component,
                    $"Макрос '{macro.Name}' поставлен в очередь региона '{qRegion.Name}'.");
                return;
            }
            _running.Add(key);
        }
        _ = RunRegionQueueAsync(key, qRegion, null, macro, startNode, ct, initialContext);
    }

    // Standard-запуск с проверкой Exclusive-блокировки (legacy ProfileRegion API)
    private void StandardEnqueueLegacy(ProfileRegion region, MacroEntry macro, BaseNode startNode, MacroExecutionContext? initialContext = null)
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        lock (_stateLock)
        {
            if (_exclusiveRunning)
            {
                _systemPendingQueue.Enqueue(new SystemPending(macro, startNode, false));
                _ = _logger.LogInfoAsync(Component,
                    $"Макрос '{macro.Name}' ожидает завершения монополиста.");
                return;
            }
        }

        if (region.ExecutionMode == "Concurrent")
        {
            _ = ExecuteWithReleaseAsync(null, region, macro, startNode, ct, initialContext);
            return;
        }

        lock (_queueLock)
        {
            if (!_queues.TryGetValue(region.RegionName, out var queue))
                _queues[region.RegionName] = queue = new Queue<(MacroEntry, BaseNode, MacroExecutionContext?)>();
            if (_running.Contains(region.RegionName))
            {
                queue.Enqueue((macro, startNode, initialContext));
                _ = _logger.LogInfoAsync(Component,
                    $"Макрос '{startNode.Name}' поставлен в очередь региона '{region.RegionName}'.");
                return;
            }
            _running.Add(region.RegionName);
        }
        _ = RunRegionQueueAsync(region.RegionName, null, region, macro, startNode, ct, initialContext);
    }

    // УРОВЕНЬ 1: System Level — выполняется немедленно, флаги не проверяются
    private async Task ExecuteSystemLevelAsync(
        MacroEntry macro, BaseNode startNode, bool isBlocking, CancellationToken ct)
    {
        try
        {
            await ExecuteNodeAsync(null, null, macro, startNode, ct).ConfigureAwait(false);
        }
        finally
        {
            int remaining = Interlocked.Decrement(ref _systemLevelCount);
            if (isBlocking)
                lock (_stateLock) { _exclusiveRunning = false; }
            if (remaining == 0 || isBlocking)
                TryDrainPending();
        }
    }

    // УРОВЕНЬ 2: Exclusive — монополист; после завершения дренирует очередь ожидания
    private async Task ExecuteExclusiveAsync(MacroEntry macro, BaseNode startNode, CancellationToken ct)
    {
        try
        {
            await ExecuteNodeAsync(null, null, macro, startNode, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock) { _exclusiveRunning = false; }
            TryDrainPending();
        }
    }

    // Дренирует очередь ожидания: запускает Exclusive и/или Standard-макросы, ставшие доступными
    private void TryDrainPending()
    {
        List<SystemPending>? toStart = null;

        lock (_stateLock)
        {
            while (_systemPendingQueue.Count > 0)
            {
                var next = _systemPendingQueue.Peek();

                bool canStart = next.IsExclusive
                    ? !_exclusiveRunning && _systemLevelCount == 0
                    : !_exclusiveRunning;

                if (!canStart) break;

                _systemPendingQueue.Dequeue();
                if (next.IsExclusive) _exclusiveRunning = true;
                toStart ??= [];
                toStart.Add(next);

                if (next.IsExclusive) break; // Только один Exclusive одновременно
            }
        }

        if (toStart is null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        foreach (var p in toStart)
        {
            if (p.IsExclusive)
                _ = ExecuteExclusiveAsync(p.Macro, p.Node, ct);
            else
                PushToRegionQueue(p.Macro, p.Node, ct);
        }
    }

    // Проталкивает Standard-макрос из pending в его регион-очередь (или запускает напрямую)
    // initialContext не передаётся из TryDrainPending (pending-элементы — не голосовые триггеры)
    private void PushToRegionQueue(MacroEntry macro, BaseNode startNode, CancellationToken ct)
    {
        QueueRegion? qRegion = macro.RegionId.HasValue
            ? _queueService.GetRegionById(macro.RegionId.Value)
            : null;

        if (qRegion is null || qRegion.ExecutionMode == "Concurrent")
        {
            _ = ExecuteWithReleaseAsync(qRegion, null, macro, startNode, ct);
            return;
        }

        var key = qRegion.Id.ToString();
        lock (_queueLock)
        {
            if (!_queues.TryGetValue(key, out var queue))
                _queues[key] = queue = new Queue<(MacroEntry, BaseNode, MacroExecutionContext?)>();
            if (_running.Contains(key))
            {
                queue.Enqueue((macro, startNode, null));
                return;
            }
            _running.Add(key);
        }
        _ = RunRegionQueueAsync(key, qRegion, null, macro, startNode, ct);
    }

    // Выполняет один макрос через INodeEngine
    private async Task ExecuteNodeAsync(
        QueueRegion?   qRegion,
        ProfileRegion? legacyRegion,
        MacroEntry macro, BaseNode startNode, CancellationToken ct,
        MacroExecutionContext? initialContext = null)
    {
        var regionName = qRegion?.Name ?? legacyRegion?.RegionName ?? "(без региона)";
        try
        {
            await _logger.LogInfoAsync(Component,
                $"[{regionName}] Запуск макроса '{startNode.Name}'...").ConfigureAwait(false);

            IEnumerable<BaseNode> allNodes = macro.VisualNodes.Count > 0
                ? macro.VisualNodes.Select(vn => vn.LogicalNode)
                : [startNode];

            var engine = _serviceProvider.GetRequiredService<INodeEngine>();
            engine.RegisterNodes(allNodes);
            engine.RegisterConnections(macro.VisualConnections);

            var entryId = startNode.Id;
            var ctx = initialContext ?? new MacroExecutionContext();
            await engine.StartAsync(entryId, ctx, ct).ConfigureAwait(false);

            await _logger.LogInfoAsync(Component,
                $"[{regionName}] Макрос '{startNode.Name}' завершён.").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _logger.LogInfoAsync(Component,
                $"[{regionName}] Макрос '{startNode.Name}' отменён.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                $"[{regionName}] Ошибка выполнения макроса '{startNode.Name}'.", ex)
                .ConfigureAwait(false);
        }
    }



    // ── Вспомогательный поиск совпадения голосовой команды ───────────────

    // Возвращает стартовую ноду если макрос соответствует фразе, иначе null.
    // При наличии TriggerRootNode возвращает первую ноду действия из OnSuccess-цепи SpeechTriggerNode,
    // минуя триггерные ноды (они уже проверены MacroScheduler-ом).
    private async Task<BaseNode?> MatchSpeechMacro(MacroEntry macro, string cleanText)
    {
        // TriggerRootNode-aware путь: триггеры — спутниковые ноды, сканируем напрямую
        var triggerRoot = macro.VisualNodes
            .Select(vn => vn.LogicalNode)
            .OfType<TriggerRootNode>()
            .FirstOrDefault();

        if (triggerRoot is not null)
        {
            var speechTrigger = macro.VisualNodes
                .Select(vn => vn.LogicalNode)
                .OfType<SpeechTriggerNode>()
                .FirstOrDefault();

            if (speechTrigger is not null)
            {
                // Триггер должен быть прямо подключён к TriggerRootNode (Root-Path Validation)
                if (!IsRootConnected(macro, speechTrigger.Id))
                    return null;

                var phrases = speechTrigger.PhrasesList;
                string? matchedPhrase = null;
                for (int pi = 0; pi < phrases.Count; pi++)
                {
                    string phrase = phrases[pi];
                    bool hit = IsPhraseMatch(cleanText, phrase);
                    await _logger.LogInfoAsync(Component,
                        $"[MacroScheduler] Фраза [{pi + 1}/{phrases.Count}]: '{phrase}' — {(hit ? "✓ СОВПАДЕНИЕ" : "✗ нет совпадения")}")
                        .ConfigureAwait(false);
                    if (hit) { matchedPhrase = phrase; break; }
                }
                if (matchedPhrase is null)
                {
                    await _logger.LogInfoAsync(Component,
                        $"[MacroScheduler] Макрос '{macro.Name}': ни одна из {phrases.Count} фраз-триггеров не совпала с '{cleanText}'.")
                        .ConfigureAwait(false);
                    return null;
                }

                // Ищем OnSuccess-связь от SpeechTriggerNode к первой ноде действия.
                // Триггерные ноды пропускаем — фраза уже верифицирована MacroScheduler-ом.
                var successConn = macro.VisualConnections
                    .FirstOrDefault(c => c.SourceNodeId == speechTrigger.Id
                                      && !c.IsErrorRoute
                                      && !c.IsDataRoute);

                if (successConn is not null)
                {
                    var actionNode = macro.VisualNodes
                        .Select(vn => vn.LogicalNode)
                        .FirstOrDefault(n => n.Id == successConn.TargetNodeId);

                    if (actionNode is not null)
                        return actionNode;
                }

                return triggerRoot;
            }

            // Нет SpeechTriggerNode — пробуем совпадение по имени макроса
            var cleanMacro = SanitizeSpeechText(macro.Name);
            if (cleanText.Contains(cleanMacro, StringComparison.Ordinal)
             || cleanMacro.Contains(cleanText, StringComparison.Ordinal))
                return triggerRoot;

            return null;
        }

        // Макросы без TriggerRootNode не активируются (архитектурное правило Root-Path Validation).
        return null;
    }

    // ── Методы для сетевого командного центра ────────────────────────────

    public async Task<bool> ExecuteMacroAsync(string nameOrId, CancellationToken ct)
    {
        var isGuid = Guid.TryParse(nameOrId, out var macroGuid);

        foreach (var profile in _configService.Current.Profiles)
        {
            // Legacy path: регионы
            foreach (var region in GetAllRegions(profile))
            {
                foreach (var macro in region.Macros)
                {
                    if (!macro.IsEnabled) continue;
                    bool matches = isGuid ? macro.Id == macroGuid
                        : macro.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase);
                    if (!matches) continue;

                    var startVn = macro.StartNodeId.HasValue
                        ? macro.VisualNodes.FirstOrDefault(vn => vn.LogicalNode.Id == macro.StartNodeId.Value)
                        : macro.VisualNodes.FirstOrDefault();
                    if (startVn is null) return false;

                    await _logger.LogInfoAsync(Component,
                        $"[NETWORK] Запуск макроса '{macro.Name}' (регион '{region.RegionName}').")
                        .ConfigureAwait(false);
                    EnqueueMacro(region, macro, startVn.LogicalNode);
                    return true;
                }
            }

            // New path: прямые макросы
            foreach (var macro in GetAllMacros(profile))
            {
                if (!macro.IsEnabled) continue;
                bool matches = isGuid ? macro.Id == macroGuid
                    : macro.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;

                var startVn = macro.StartNodeId.HasValue
                    ? macro.VisualNodes.FirstOrDefault(vn => vn.LogicalNode.Id == macro.StartNodeId.Value)
                    : macro.VisualNodes.FirstOrDefault();
                if (startVn is null) return false;

                await _logger.LogInfoAsync(Component,
                    $"[NETWORK] Запуск макроса '{macro.Name}'.").ConfigureAwait(false);
                EnqueueMacro(macro, startVn.LogicalNode);
                return true;
            }
        }
        return false;
    }

    public Task SendNetworkPromptAsync(string text, CancellationToken ct)
    {
        var msg = new ChatMessage("user", text);
        return RouteToAiAsync(msg, false, _cts?.Token ?? ct);
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
