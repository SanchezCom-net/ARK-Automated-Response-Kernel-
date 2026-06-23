using System.Collections.Concurrent;
using System.Windows.Input;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;
using SanitizeUtil = ARK.UI.Core.TextSanitizer;

namespace ARK.UI.Core.Services;

/// <summary>
/// Единственный слушач системных событий (Singleton).
/// Подписки на события регистрируются в КОНСТРУКТОРЕ (не в InitializeAsync),
/// чтобы ни одно событие MacroStatusChanged/SpeechRecognized не было потеряно
/// независимо от порядка фаз StartupOrchestrator.
/// </summary>
public sealed class EventMonitor : IEventMonitor
{
    private const string Component = nameof(EventMonitor);

    private readonly IStorageManager       _storage;
    private readonly IInputService         _inputService;
    private readonly ISpeechTriggerService _speechService;
    private readonly IMacroOrchestrator    _orchestrator;
    private readonly ILogService           _logger;
    private readonly IDataBus?             _dataBus;

    // Кэш: комбинация клавиш → (macroId, triggerNodeId)
    private readonly ConcurrentDictionary<(Key Key, ModifierKeys Mods), (Guid MacroId, Guid TriggerNodeId)> _hotkeyCache = new();

    // Кэш: санитизированная фраза-триггер → (macroId, macroName, triggerNodeId)
    private readonly ConcurrentDictionary<string, (Guid MacroId, string MacroName, Guid TriggerNodeId)> _speechCache = new();

    public EventMonitor(
        IStorageManager       storage,
        IInputService         inputService,
        ISpeechTriggerService speechService,
        IMacroOrchestrator    orchestrator,
        ILogService           logger,
        IDataBus?             dataBus = null)
    {
        _storage       = storage;
        _inputService  = inputService;
        _speechService = speechService;
        _orchestrator  = orchestrator;
        _logger        = logger;
        _dataBus       = dataBus;

        // Подписки — ЗДЕСЬ, в конструкторе. Событие не будет потеряно даже если
        // InitializeAsync/RefreshTriggersCacheAsync ещё не вызывались.
        _inputService.KeyDown           += OnKeyDown;
        _speechService.SpeechRecognized += OnSpeechRecognized;
        _storage.MacroStatusChanged     += OnMacroStatusChanged;
    }

    // ── Инициализация ────────────────────────────────────────────────────────

    /// <summary>Вызывается StartupOrchestrator на фазе MacroIndex. Строит кэш триггеров.</summary>
    public Task InitializeAsync(CancellationToken ct = default)
        => RefreshTriggersCacheAsync(ct);

    public Task RefreshAsync(CancellationToken ct = default)
        => RefreshTriggersCacheAsync(ct);

    // ── Основной public-метод обновления кэша ────────────────────────────────

    public async Task RefreshTriggersCacheAsync(CancellationToken ct = default)
    {
        // SANITY CHECK — первая строка, ДО любых async-операций.
        // Если этот лог отсутствует, метод никогда не вызывался.
        await _logger.LogInfoAsync(Component,
            "[EventMonitor] Начат процесс обновления кэша. Запрашиваем манифесты...")
            .ConfigureAwait(false);

        await BuildCacheAsync(ct).ConfigureAwait(false);
    }

    private void OnMacroStatusChanged(object? sender, Guid macroId)
        => _ = RefreshTriggersCacheAsync(CancellationToken.None);

    // ── Построение кэша (статический обход графа) ────────────────────────────

    private async Task BuildCacheAsync(CancellationToken ct)
    {
        _hotkeyCache.Clear();
        _speechCache.Clear();

        // ── 1. Получить все манифесты из хранилища ────────────────────────────
        IReadOnlyList<MacroManifest> manifests;
        try
        {
            manifests = await _storage.GetAllMacrosAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                "[EventMonitor] ОШИБКА: не удалось получить список макросов.", ex).ConfigureAwait(false);
            return;
        }

        // SANITY CHECK #2 — выводится ДО цикла, даже если макросов 0
        await _logger.LogInfoAsync(Component,
            $"[EventMonitor] Получено макросов из хранилища: {manifests.Count}. Начинаем парсинг...")
            .ConfigureAwait(false);

        if (manifests.Count == 0)
        {
            await _logger.LogInfoAsync(Component,
                "[EventMonitor] Хранилище пустое — нет макросов для регистрации триггеров.")
                .ConfigureAwait(false);
            return;
        }

        int totalHotkeys = 0, totalPhrases = 0;

        // ── 2. Обходим ВСЕ макросы (Beta и Release) ──────────────────────────
        // Статус среды не влияет на регистрацию триггеров — рубильником служит TriggerRootNode
        foreach (var manifest in manifests)
        {
            ct.ThrowIfCancellationRequested();

            await _logger.LogInfoAsync(Component,
                $"[EventMonitor] Читаем макрос '{manifest.Name}' (ID: {manifest.Id}).")
                .ConfigureAwait(false);

            MacroDocument doc;
            try
            {
                doc = await _storage.LoadMacroAsync(manifest.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _logger.LogInfoAsync(Component,
                    $"[EventMonitor] Макрос '{manifest.Name}': ОШИБКА загрузки — {ex.Message}. Пропускаем.")
                    .ConfigureAwait(false);
                continue;
            }

            var nodes       = doc.Macro.VisualNodes;
            var connections = doc.Macro.VisualConnections;

            await _logger.LogInfoAsync(Component,
                $"[EventMonitor] Макрос '{manifest.Name}': Нод: {nodes.Count}, Проводов: {connections.Count}.")
                .ConfigureAwait(false);

            // ── 3. Найти TriggerRootNode ──────────────────────────────────────
            var triggerRootVn = nodes.FirstOrDefault(vn => vn.LogicalNode is TriggerRootNode);

            if (triggerRootVn is null)
            {
                await _logger.LogInfoAsync(Component,
                    $"[EventMonitor] Макрос '{manifest.Name}': TriggerRootNode НЕ НАЙДЕНА. " +
                    $"Типы нод: [{string.Join(", ", nodes.Select(vn => vn.LogicalNode?.GetType().Name ?? "null"))}].")
                    .ConfigureAwait(false);
                continue;
            }

            // ── 4. Двойной индекс нод: VisualNode.NodeId И LogicalNode.Id ────
            // Защита от рассинхронизации ID при десериализации JSON
            var nodeById = new Dictionary<Guid, BaseNode>();
            foreach (var vn in nodes)
            {
                if (vn.LogicalNode is null) continue;
                nodeById.TryAdd(vn.NodeId,        vn.LogicalNode);
                nodeById.TryAdd(vn.LogicalNode.Id, vn.LogicalNode);
            }

            // ── 5. Найти провода от TriggerRootNode (по обоим ID) ────────────
            var rootIds = new HashSet<Guid> { triggerRootVn.NodeId, triggerRootVn.LogicalNode.Id };

            var outgoing = connections
                .Where(c => rootIds.Contains(c.SourceNodeId) && !c.IsErrorRoute && !c.IsDataRoute)
                .ToList();

            await _logger.LogInfoAsync(Component,
                $"[EventMonitor] Найдена нода СТАРТ. " +
                $"От неё отходит проводов: {outgoing.Count}. " +
                $"(rootIds=[{string.Join(", ", rootIds)}], " +
                $"SourceNodeId в графе: [{string.Join(", ", connections.Select(c => c.SourceNodeId))}])")
                .ConfigureAwait(false);

            if (outgoing.Count == 0)
            {
                await _logger.LogInfoAsync(Component,
                    $"[EventMonitor] Макрос '{manifest.Name}': нет Signal-проводов от TriggerRootNode — " +
                    $"убедитесь, что SpeechTriggerNode соединён жёлтым проводом с TriggerRootNode.")
                    .ConfigureAwait(false);
                continue;
            }

            // ── 6. Обработать целевые ноды ────────────────────────────────────
            int macroHotkeys = 0, macroPhrases = 0;

            foreach (var conn in outgoing)
            {
                if (!nodeById.TryGetValue(conn.TargetNodeId, out var targetNode))
                {
                    await _logger.LogInfoAsync(Component,
                        $"[EventMonitor] Макрос '{manifest.Name}': TargetNodeId={conn.TargetNodeId} " +
                        $"не найден в nodeById (ключей в индексе: {nodeById.Count}).")
                        .ConfigureAwait(false);
                    continue;
                }

                switch (targetNode)
                {
                    case HotkeyTriggerNode hk when hk.HotKey != Key.None:
                        // TriggerNodeId = ID конкретной HotkeyTriggerNode — с неё стартует NodeEngine
                        _hotkeyCache[(hk.HotKey, hk.HotKeyModifiers)] = (manifest.Id, hk.Id);
                        totalHotkeys++;
                        macroHotkeys++;

                        await _logger.LogInfoAsync(Component,
                            $"[EventMonitor] Обнаружен АКТИВНЫЙ триггер: HotkeyTriggerNode (ID={hk.Id}). " +
                            $"Добавлено фраз/кнопок: 1 ({hk.HotKeyModifiers}+{hk.HotKey}).")
                            .ConfigureAwait(false);
                        break;

                    case SpeechTriggerNode st:
                        // PhrasesText — JSON-поле "phrases_text"; PhrasesList — computed split по \n
                        await _logger.LogInfoAsync(Component,
                            $"[EventMonitor] Макрос '{manifest.Name}': SpeechTriggerNode (ID={st.Id}). " +
                            $"PhrasesText='{st.PhrasesText}', PhrasesList.Count={st.PhrasesList.Count}.")
                            .ConfigureAwait(false);

                        foreach (var phrase in st.PhrasesList)
                        {
                            var clean = SanitizeUtil.Sanitize(phrase);
                            if (string.IsNullOrEmpty(clean)) continue;
                            // TriggerNodeId = ID конкретной SpeechTriggerNode — с неё стартует NodeEngine
                            _speechCache[clean] = (manifest.Id, manifest.Name, st.Id);
                            totalPhrases++;
                            macroPhrases++;
                        }

                        await _logger.LogInfoAsync(Component,
                            $"[EventMonitor] Обнаружен АКТИВНЫЙ триггер: SpeechTriggerNode (ID={st.Id}). " +
                            $"Добавлено фраз/кнопок: {macroPhrases} [{string.Join(", ", st.PhrasesList.Select(p => $"'{p}'"))}].")
                            .ConfigureAwait(false);
                        break;
                }
            }

            await _logger.LogInfoAsync(Component,
                $"[EventMonitor] Макрос '{manifest.Name}': зарегистрировано фраз={macroPhrases}, хоткеев={macroHotkeys}.")
                .ConfigureAwait(false);
        }

        await _logger.LogInfoAsync(Component,
            $"[EventMonitor] Кэш обновлён. Просмотрено макросов: {manifests.Count}. " +
            $"Голосовых триггеров: {totalPhrases}, хоткеев: {totalHotkeys}.")
            .ConfigureAwait(false);

        if (totalPhrases == 0 && totalHotkeys == 0)
            await _logger.LogInfoAsync(Component,
                "[EventMonitor] ВНИМАНИЕ: ни одного активного триггера не зарегистрировано. " +
                "Убедитесь: SpeechTriggerNode/HotkeyTriggerNode соединены проводом с нодой СТАРТ (TriggerRootNode) " +
                "и в SpeechTriggerNode заданы фразы.")
                .ConfigureAwait(false);
    }

    // ── Обработчики событий ──────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyHookEventArgs e)
    {
        if (!_hotkeyCache.TryGetValue((e.Key, e.Modifiers), out var entry)) return;
        // Передаём TriggerNodeId: NodeEngine стартует строго с HotkeyTriggerNode
        _ = _orchestrator.EnqueueMacroAsync(entry.MacroId, entry.TriggerNodeId);
    }

    private Task OnSpeechRecognized(string text, bool keywordDetected)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        _ = ProcessSpeechAsync(text);
        return Task.CompletedTask;
    }

    private async Task ProcessSpeechAsync(string rawText)
    {
        var cleanText = SanitizeUtil.Sanitize(rawText);

        await _logger.LogInfoAsync(Component,
            $"[EventMonitor] Получена фраза: '{rawText}'. " +
            $"Нормализовано: '{cleanText}'. Кэш: {_speechCache.Count} триггер(ов).")
            .ConfigureAwait(false);

        if (cleanText.Length == 0 || _speechCache.Count == 0) return;

        foreach (var kvp in _speechCache)
        {
            if (!IsPhraseMatch(cleanText, kvp.Key)) continue;

            await _logger.LogInfoAsync(Component,
                $"[EventMonitor] Совпадение: триггер='{kvp.Key}' → " +
                $"макрос='{kvp.Value.MacroName}' ({kvp.Value.MacroId}), " +
                $"стартовая нода={kvp.Value.TriggerNodeId}. Запускаю...")
                .ConfigureAwait(false);

            var packet = DataBusPacket.Text(Guid.NewGuid(), meta: new Dictionary<string, string>
            {
                ["SpeechRecognizedText"] = rawText
            });
            // rawText кладём в DataBus по CompositeKey пакета; SpeechTriggerNode достанет через метаданные
            _dataBus?.Set(packet.SessionId, packet.DataId, rawText);
            // Передаём TriggerNodeId: NodeEngine стартует строго с SpeechTriggerNode,
            // TriggerRootNode остаётся в Pending
            _ = _orchestrator.EnqueueMacroAsync(kvp.Value.MacroId, kvp.Value.TriggerNodeId, packet);
            return;
        }

        await _logger.LogInfoAsync(Component,
            $"[EventMonitor] Совпадение не найдено для: '{cleanText}'. " +
            $"Активные триггеры: [{string.Join(" | ", _speechCache.Keys.Take(5))}]")
            .ConfigureAwait(false);
    }

    // ── Фразовый матчинг: все слова триггера должны присутствовать в тексте ──

    private static bool IsPhraseMatch(string cleanRecognized, string cleanPhrase)
    {
        if (cleanPhrase.Length == 0) return false;
        var recWords    = cleanRecognized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var phraseWords = cleanPhrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return phraseWords.Length > 0
            && phraseWords.All(pw => recWords.Contains(pw, StringComparer.OrdinalIgnoreCase));
    }
}
