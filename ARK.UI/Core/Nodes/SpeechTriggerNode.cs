using System.Text.Json.Serialization;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using SanitizeUtil = ARK.UI.Core.TextSanitizer;

namespace ARK.UI.Core.Nodes;

public sealed class SpeechTriggerNode : BaseNode
{
    // "Text" — виртуальный ключ для инъекции распознанной фразы извне
    public override string DefaultDataInputPropertyName => "Text";

    // ── Фразы-синонимы (многострочный текст, по одной фразе на строку) ──────

    private string _phrasesText = string.Empty;

    [JsonPropertyName("phrases_text")]
    public string PhrasesText
    {
        get => _phrasesText;
        set { _phrasesText = value ?? string.Empty; OnPropertyChanged(); }
    }

    // Вычисляемый список — разбивка по строкам, пустые строки отфильтрованы
    [JsonIgnore]
    public IReadOnlyList<string> PhrasesList => _phrasesText
        .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => !string.IsNullOrEmpty(line))
        .ToList();

    // ── Фильтр по ключевому слову/корню ──────────────────────────────────────

    private string _requiredKeyword = string.Empty;
    public string RequiredKeyword
    {
        get => _requiredKeyword;
        set { if (_requiredKeyword != value) { _requiredKeyword = value; OnPropertyChanged(); } }
    }

    public override Task OnStartListeningAsync(CancellationToken ct)
    {
        DebugSink?.Invoke($"[TRIGGER INIT] SpeechTrigger [{PhrasesList.Count} фраз] → IsListening=true, ожидает голосовую команду.");
        return Task.CompletedTask;
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var cleanPhrases = PhrasesList;
        var kw           = SanitizeUtil.Sanitize(RequiredKeyword);
        DebugSink?.Invoke($"[ТРИГГЕР] Запуск. Шаблонов фраз: {cleanPhrases.Count}. Ключевое слово: '{kw}'.");

        // ── Сбор входного текста: приоритет серебряный провод → контекст от EventMonitor ──

        string incomingText = string.Empty;
        bool   hasInput     = false;

        // 1. Серебряный провод (DataBus)
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _rawST))
        {
            var _stStr = _rawST as string ?? _rawST?.ToString();
            if (_stStr is not null) { incomingText = _stStr; hasInput = true; }
        }

        // 2. MacroExecutionContext.Variables["SpeechRecognizedText"] — от EventMonitor через MacroOrchestrator
        if (!hasInput)
        {
            var ctxSpeech = CurrentContext?.Variables.TryGetValue("SpeechRecognizedText", out var _csv) == true
                ? _csv?.ToString() : null;
            if (!string.IsNullOrEmpty(ctxSpeech)) { incomingText = ctxSpeech; hasInput = true; }
        }

        // ── Матчинг входного текста (DataBus или контекст) ────────────────────────────────

        if (hasInput && !string.IsNullOrEmpty(incomingText))
        {
            var sanitizedInput = SanitizeUtil.Sanitize(incomingText);

            await NodeLogger!.LogInfoAsync(Name, $"[SpeechTriggerNode] Проверка ноды '{Name}':").ConfigureAwait(false);
            await NodeLogger!.LogInfoAsync(Name, $" -> Источник: входной текст. '{sanitizedInput}'").ConfigureAwait(false);

            if (kw.Length > 0)
            {
                bool kwFound = sanitizedInput.Contains(kw, StringComparison.Ordinal);
                await NodeLogger!.LogInfoAsync(Name,
                    $" -> Обязательное ключевое слово: '{kw}' (Статус нахождения: {(kwFound ? "НАЙДЕНО" : "НЕ НАЙДЕНО")})")
                    .ConfigureAwait(false);
                if (!kwFound)
                {
                    await NodeLogger!.LogInfoAsync(Name, " -> СТАТУС: ОТКЛОНЕН — ключевое слово не найдено в данных").ConfigureAwait(false);
                    DebugSink?.Invoke("[ТРИГГЕР] Ключевое слово не найдено во входном тексте. Пропуск.");
                    return NodeResult.Failure("Ключевое слово не найдено.");
                }
            }

            if (cleanPhrases.Count == 0)
            {
                await NodeLogger!.LogInfoAsync(Name, " -> Шаблоны фраз не заданы. Проверка невозможна.").ConfigureAwait(false);
                await NodeLogger!.LogInfoAsync(Name, " -> СТАТУС: ОТКЛОНЕН — нет шаблонов для сравнения").ConfigureAwait(false);
                DebugSink?.Invoke("[ТРИГГЕР] Шаблоны не заданы. Пропуск.");
                return NodeResult.Failure("Шаблоны фраз не заданы.");
            }

            foreach (var phrase in cleanPhrases)
            {
                var (isMatch, matchedW, total) = EvaluatePhraseMatch(sanitizedInput, phrase, kw);
                double score = total > 0 ? matchedW * 100.0 / total : 100.0;

                await NodeLogger!.LogInfoAsync(Name,
                    $" -> Сравнение входной фразы '{sanitizedInput}' с шаблоном '{Sanitize(phrase)}'")
                    .ConfigureAwait(false);
                await NodeLogger!.LogInfoAsync(Name,
                    $" -> Результат Fuzzy Matching: {score:F0}% (Необходимый порог: 100%)")
                    .ConfigureAwait(false);
                await NodeLogger!.LogInfoAsync(Name,
                    $" -> СТАТУС: {(isMatch ? "УСПЕШНО ЗАПУЩЕН" : "ОТКЛОНЕН ПО ПОРОГУ СОВПАДЕНИЯ")}")
                    .ConfigureAwait(false);

                if (!isMatch) continue;

                DebugSink?.Invoke($"[ТРИГГЕР] Совпадение с шаблоном '{phrase}'. Активирую по входному тексту.");
                LastOutputValue = new DataPacket { Type = DataType.Text, Payload = incomingText };
                DebugSink?.Invoke($"[ВЫХОД] DataBus записан: «{incomingText}»");
                await NodeLogger!.LogInfoAsync(Name, $"[ГОЛОС] Нода активирована текстом: '{incomingText}'").ConfigureAwait(false);
                LogToBlackBox($"[ГОЛОС] Активирован текстом: '{sanitizedInput}'");
                var _sid0 = inputPacket?.SessionId ?? Guid.NewGuid();
                var _out0 = DataBusPacket.Text(_sid0);
                DataBus?.Set(_out0.SessionId, _out0.DataId, incomingText);
                return NodeResult.Success(_out0);
            }

            await NodeLogger!.LogInfoAsync(Name, " -> Ни один шаблон не прошёл порог совпадения. Нода не активирована.").ConfigureAwait(false);
            DebugSink?.Invoke("[ТРИГГЕР] Входной текст не совпал ни с одним из синонимов. Пропуск.");
            return NodeResult.Failure("Ни один шаблон не совпал.");
        }

        // ── Режим интерактивного теста ─────────────────────────────────────
        if (CurrentContext?.Variables.ContainsKey("IsInteractiveTest") == true)
        {
            string testPhrase = cleanPhrases.FirstOrDefault() ?? "Тестовая голосовая команда";
            DebugSink?.Invoke($"[ТРИГГЕР] Режим интерактивного теста — активирую мгновенно с фразой: \"{testPhrase}\"");
            LastOutputValue = new DataPacket { Type = DataType.Text, Payload = testPhrase };
            DebugSink?.Invoke($"[ВЫХОД] DataBus записан: «{testPhrase}»");
            var _sid1 = inputPacket?.SessionId ?? Guid.NewGuid();
            var _out1 = DataBusPacket.Text(_sid1);
            DataBus?.Set(_out1.SessionId, _out1.DataId, testPhrase);
            return NodeResult.Success(_out1);
        }

        // ── Реальный V3-режим: подписываемся на ISpeechTriggerService ────────
        if (!IsListening)
        {
            DebugSink?.Invoke("[ТРИГГЕР] IsListening=false — Speech-триггер не зарегистрирован.");
            return NodeResult.Failure("Speech-триггер неактивен (IsListening=false).");
        }

        var speechService = NodeServices!.GetRequiredService<ISpeechTriggerService>();
        DebugSink?.Invoke($"[ТРИГГЕР] V3: подписываюсь на ISpeechTriggerService, жду совпадения ({cleanPhrases.Count} фраз)...");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<string, bool, Task> handler = (recognizedText, _) =>
        {
            var sanitized = SanitizeUtil.Sanitize(recognizedText);
            if (kw.Length > 0 && !sanitized.Contains(kw, StringComparison.Ordinal))
                return Task.CompletedTask;
            if (cleanPhrases.Count > 0 && !cleanPhrases.Any(p => IsPhraseMatch(sanitized, p)))
                return Task.CompletedTask;
            tcs.TrySetResult(recognizedText);
            return Task.CompletedTask;
        };

        speechService.SpeechRecognized += handler;
        string speechMatched;
        try
        {
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct), useSynchronizationContext: false);
            speechMatched = await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            speechService.SpeechRecognized -= handler;
        }

        var matchedSanitized = SanitizeUtil.Sanitize(speechMatched);
        DebugSink?.Invoke($"[ТРИГГЕР] Голосовая команда совпала: '{matchedSanitized}'");
        await NodeLogger!.LogInfoAsync(Name, $"[ГОЛОС] Нода активирована командой: '{matchedSanitized}'").ConfigureAwait(false);
        LogToBlackBox($"[ГОЛОС] Распознана фраза: '{matchedSanitized}'");

        LastOutputValue = new DataPacket { Type = DataType.Text, Payload = speechMatched };
        var sid = inputPacket?.SessionId ?? Guid.NewGuid();
        var outPacket = DataBusPacket.Text(sid);
        DataBus?.Set(outPacket.SessionId, outPacket.DataId, speechMatched);
        return NodeResult.Success(outPacket);
    }

    private static string Sanitize(string text) => SanitizeUtil.Sanitize(text);

    internal static bool IsPhraseMatch(string recognizedText, string triggerPhrase)
        => EvaluatePhraseMatch(recognizedText, triggerPhrase).IsMatch;

    private static (bool IsMatch, int MatchedWords, int TotalWords) EvaluatePhraseMatch(
        string recognizedText, string triggerPhrase, string strippedKeyword = "")
    {
        if (string.IsNullOrWhiteSpace(recognizedText) || string.IsNullOrWhiteSpace(triggerPhrase))
            return (false, 0, 0);

        var cleanRec  = Sanitize(recognizedText);
        var cleanTmpl = Sanitize(triggerPhrase);

        if (strippedKeyword.Length > 0)
        {
            cleanRec  = cleanRec.Replace(strippedKeyword,  " ");
            cleanTmpl = cleanTmpl.Replace(strippedKeyword, " ");
        }

        var recWords  = cleanRec.Split(' ',  StringSplitOptions.RemoveEmptyEntries);
        var tmplWords = cleanTmpl.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tmplWords.Length == 0) return (true, 0, 0);
        int matched = tmplWords.Count(tw => recWords.Contains(tw));
        return (matched == tmplWords.Length, matched, tmplWords.Length);
    }

    // ── Fuzzy-матчинг: Levenshtein + word-order-independent ───────────────

    [ThreadStatic] private static int[]? _levRow0;
    [ThreadStatic] private static int[]? _levRow1;

    private static int LevenshteinDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.IsEmpty) return b.Length;
        if (b.IsEmpty) return a.Length;

        int lenA = a.Length, lenB = b.Length;
        int needed = lenB + 1;

        if (_levRow0 is null || _levRow0.Length < needed) _levRow0 = new int[needed];
        if (_levRow1 is null || _levRow1.Length < needed) _levRow1 = new int[needed];

        var row  = _levRow0.AsSpan(0, needed);
        var prev = _levRow1.AsSpan(0, needed);

        for (int j = 0; j <= lenB; j++) prev[j] = j;

        for (int i = 1; i <= lenA; i++)
        {
            row[0] = i;
            for (int j = 1; j <= lenB; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                row[j] = Math.Min(Math.Min(row[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            var tmp = prev; prev = row; row = tmp;
        }

        return prev[lenB];
    }

    private static double WordSimilarity(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 1.0;
        return 1.0 - (double)LevenshteinDistance(a, b) / maxLen;
    }

    private static double ComputeFuzzyScore(string inputText, string templatePhrase)
    {
        if (string.IsNullOrWhiteSpace(inputText) || string.IsNullOrWhiteSpace(templatePhrase)) return 0.0;

        var inputWords    = Sanitize(inputText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var templateWords = Sanitize(templatePhrase).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (templateWords.Length == 0) return 0.0;
        if (inputWords.Length    == 0) return 0.0;

        double totalScore = 0.0;
        foreach (var tw in templateWords)
        {
            double bestMatch = 0.0;
            foreach (var iw in inputWords)
            {
                double sim = WordSimilarity(tw.AsSpan(), iw.AsSpan());
                if (sim > bestMatch) bestMatch = sim;
            }
            totalScore += bestMatch;
        }

        return totalScore / templateWords.Length;
    }
}
