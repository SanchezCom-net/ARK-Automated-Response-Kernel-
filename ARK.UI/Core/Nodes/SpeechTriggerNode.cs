using System.Text.Json.Serialization;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
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

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        var cleanPhrases = PhrasesList;
        var kw           = SanitizeUtil.Sanitize(RequiredKeyword);
        DebugSink?.Invoke($"[ТРИГГЕР] Запуск. Шаблонов фраз: {cleanPhrases.Count}. Ключевое слово: '{kw}'.");

        // ── Input: текст прилетает по серебряному проводу ─────────────────
        string incomingText = string.Empty;
        bool hasInput = TryApplyContextInput<string>("Text", v => incomingText = v);

        if (hasInput && !string.IsNullOrEmpty(incomingText))
        {
            var sanitizedInput = SanitizeUtil.Sanitize(incomingText);

            await logger.LogInfoAsync(Name, $"[SpeechTriggerNode] Проверка ноды '{Name}':").ConfigureAwait(false);
            await logger.LogInfoAsync(Name, $" -> Источник: серебряный провод данных. Входящий текст: '{sanitizedInput}'").ConfigureAwait(false);

            if (kw.Length > 0)
            {
                bool kwFound = sanitizedInput.Contains(kw, StringComparison.Ordinal);
                await logger.LogInfoAsync(Name,
                    $" -> Обязательное ключевое слово: '{kw}' (Статус нахождения: {(kwFound ? "НАЙДЕНО" : "НЕ НАЙДЕНО")})")
                    .ConfigureAwait(false);
                if (!kwFound)
                {
                    await logger.LogInfoAsync(Name, " -> СТАТУС: ОТКЛОНЕН — ключевое слово не найдено в данных").ConfigureAwait(false);
                    DebugSink?.Invoke("[ТРИГГЕР] Ключевое слово не найдено во входном тексте. Пропуск.");
                    return false;
                }
            }

            if (cleanPhrases.Count == 0)
            {
                await logger.LogInfoAsync(Name, " -> Шаблоны фраз не заданы. Проверка невозможна.").ConfigureAwait(false);
                await logger.LogInfoAsync(Name, " -> СТАТУС: ОТКЛОНЕН — нет шаблонов для сравнения").ConfigureAwait(false);
                DebugSink?.Invoke("[ТРИГГЕР] Шаблоны не заданы. Пропуск.");
                return false;
            }

            foreach (var phrase in cleanPhrases)
            {
                var (isMatch, matched, total) = EvaluatePhraseMatch(sanitizedInput, phrase, kw);
                double score = total > 0 ? matched * 100.0 / total : 100.0;

                await logger.LogInfoAsync(Name,
                    $" -> Сравнение входной фразы '{sanitizedInput}' с шаблоном '{Sanitize(phrase)}'")
                    .ConfigureAwait(false);
                await logger.LogInfoAsync(Name,
                    $" -> Результат Fuzzy Matching: {score:F0}% (Необходимый порог: 100%)")
                    .ConfigureAwait(false);
                await logger.LogInfoAsync(Name,
                    $" -> СТАТУС: {(isMatch ? "УСПЕШНО ЗАПУЩЕН" : "ОТКЛОНЕН ПО ПОРОГУ СОВПАДЕНИЯ")}")
                    .ConfigureAwait(false);

                if (!isMatch) continue;

                DebugSink?.Invoke($"[ТРИГГЕР] Совпадение с шаблоном '{phrase}'. Активирую по входному тексту.");
                if (IsDataOutputEnabled)
                    LastOutputValue = new DataPacket { Type = DataType.Text, Payload = incomingText };
                DebugSink?.Invoke($"[ВЫХОД] DataPacket записан: «{incomingText}»");
                await logger.LogInfoAsync(Name, $"[ГОЛОС] Нода активирована внешним текстом: '{incomingText}'").ConfigureAwait(false);
                return true;
            }

            await logger.LogInfoAsync(Name, " -> Ни один шаблон не прошёл порог совпадения. Нода не активирована.").ConfigureAwait(false);
            DebugSink?.Invoke("[ТРИГГЕР] Входной текст не совпал ни с одним из синонимов. Пропуск.");
            return false;
        }

        // ── Режим интерактивного теста ─────────────────────────────────────
        if (CurrentContext?.Variables.ContainsKey("IsInteractiveTest") == true)
        {
            string testPhrase = cleanPhrases.FirstOrDefault() ?? "Тестовая голосовая команда";
            DebugSink?.Invoke($"[ТРИГГЕР] Режим интерактивного теста — активирую мгновенно с фразой: \"{testPhrase}\"");
            LastOutputValue = new DataPacket { Type = DataType.Text, Payload = testPhrase };
            DebugSink?.Invoke($"[ВЫХОД] DataPacket записан: «{testPhrase}»");
            return true;
        }

        // ── Реальный режим: нода активирована MacroScheduler-ом ──────────
        DebugSink?.Invoke("[ТРИГГЕР] Реальный режим — нода активирована MacroScheduler-ом.");

        var rawSpeech  = CurrentContext?.Variables.TryGetValue("SpeechRecognizedText", out var sv) == true
            ? sv?.ToString() ?? string.Empty : string.Empty;
        var speechText = SanitizeUtil.Sanitize(rawSpeech);

        await logger.LogInfoAsync(Name, $"[SpeechTriggerNode] Проверка ноды '{Name}':").ConfigureAwait(false);
        await logger.LogInfoAsync(Name, $" -> Распознанная фраза (санирована): '{speechText}'").ConfigureAwait(false);

        if (kw.Length > 0)
        {
            bool kwFound = speechText.Contains(kw, StringComparison.Ordinal);
            await logger.LogInfoAsync(Name,
                $" -> Обязательное ключевое слово: '{kw}' (Статус нахождения: {(kwFound ? "НАЙДЕНО" : "НЕ НАЙДЕНО")})")
                .ConfigureAwait(false);
            await logger.LogInfoAsync(Name,
                $" -> СТАТУС: {(kwFound ? "УСПЕШНО ЗАПУЩЕН" : "ОТКЛОНЕН — ключевое слово отсутствует")}")
                .ConfigureAwait(false);
            DebugSink?.Invoke($"[IntentProcessor] «{kw}» {(kwFound ? "⊆" : "⊄")} «{speechText}» {(kwFound ? "✓" : "✗")}");

            if (!kwFound) return false;

            if (IsDataOutputEnabled)
                LastOutputValue = new DataPacket { Type = DataType.Text, Payload = speechText };
            return true;
        }

        await logger.LogInfoAsync(Name, " -> Ключевое слово не задано — нода активируется безусловно.").ConfigureAwait(false);
        await logger.LogInfoAsync(Name, " -> СТАТУС: УСПЕШНО ЗАПУЩЕН").ConfigureAwait(false);
        return true;
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
