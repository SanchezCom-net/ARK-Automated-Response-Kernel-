using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class OllamaBridgeService : IOllamaBridgeService
{
    private const string Component  = "OllamaBridgeService";
    private const int    MaxHistory = 10;

    private readonly IConfigService    _configService;
    private readonly ILogService       _logger;
    private readonly HttpClient        _http    = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly List<ChatMessage> _history = [];

    public OllamaBridgeService(IConfigService configService, ILogService logger)
    {
        _configService = configService;
        _logger        = logger;
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        ChatMessage userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var token in StreamInternalAsync(userMessage, false, cancellationToken))
            yield return token;
    }

    private async IAsyncEnumerable<string> StreamInternalAsync(
        ChatMessage userMessage,
        bool        isRetry,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Если ИИ отключён в конфигурации — немедленный выход без сетевых запросов
        if (!_configService.Current.IsAiEnabled && !isRetry)
        {
            await _logger.LogWarningAsync(Component,
                "Запрос к Ollama отклонён: AI отключён в настройках (IsAiEnabled = false). " +
                "Включите AI в настройках для использования языковой модели.")
                .ConfigureAwait(false);
            yield break;
        }

        var model = _configService.Current.OllamaModelName;

        // Диагностика: изображение уходит на модель без поддержки зрения
        if (userMessage.Images is { Count: > 0 } && !OllamaModelCapabilities.IsLikelyMultimodal(model))
        {
            await _logger.LogWarningAsync(Component,
                $"[VISION] Модель '{model}' выглядит текстовой — изображение будет проигнорировано. " +
                "Для работы компьютерного зрения выберите мультимодальную модель " +
                "(например, llava, qwen2-vl или llama3.2-vision).").ConfigureAwait(false);
        }

        // Контекст: системный промпт + валидированная история + новый запрос (возможно с Images)
        var systemPrompt = _configService.Current.AiSystemPrompt;

        // Гарантия агентных возможностей: кастомная персона из AiCharacterDialog
        // (сохранённая в config.json) не знает о командных тегах — дополняем её
        if (!systemPrompt.Contains("<click", StringComparison.OrdinalIgnoreCase))
            systemPrompt = $"{systemPrompt}\n\n{AgentCapabilities.Prompt}";
        var clean = SanitizeHistory(_history);
        var msgs  = new List<ChatMessage>(clean.Count + 2) { new("system", systemPrompt) };
        msgs.AddRange(clean);
        msgs.Add(userMessage);

        var url  = $"{_configService.Current.OllamaApiUrl.TrimEnd('/')}/api/chat";
        var body = JsonSerializer.Serialize(new
        {
            model,
            messages = msgs,
            stream   = true,
            options  = new
            {
                temperature    = 0.7,
                repeat_penalty = 1.15,
                repeat_last_n  = 64,
                // 16k: мультимодальные запросы (Base64-скриншот) не влезают в дефолтные 4096
                // токенов Ollama → exceed_context_size_error (HTTP 400)
                num_ctx        = 16384
            }
        }, SerializeOpts);

        HttpResponseMessage? response = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { yield break; }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component, "Ошибка подключения к Ollama.", ex)
                .ConfigureAwait(false);
            yield break;
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode    = (int)response.StatusCode;
            var reasonPhrase  = response.ReasonPhrase;
            var extractedError = await ReadOllamaErrorAsync(response, cancellationToken)
                .ConfigureAwait(false);
            response.Dispose();

            await _logger.LogErrorAsync(Component,
                extractedError.Length > 0
                    ? $"Ollama API Error ({statusCode}): {extractedError}"
                    : $"Ollama вернул {statusCode}: {reasonPhrase}.")
                .ConfigureAwait(false);

            if (statusCode == 400 && !isRetry)
            {
                // Fallback Vision → Text-Only: модель отказалась принимать изображение.
                // Срезаем картинку и спасаем диалог повтором в чистом текстовом режиме.
                if (userMessage.Images is { Count: > 0 } && IsImageRejectionError(extractedError))
                {
                    await _logger.LogWarningAsync(Component,
                        "[ИИ] Модель не приняла скриншот. Повторяю запрос в чистом текстовом режиме.")
                        .ConfigureAwait(false);
                    userMessage.Images = null;
                    await foreach (var tok in StreamInternalAsync(userMessage, true, cancellationToken))
                        yield return tok;
                    yield break;
                }

                await _logger.LogInfoAsync(Component,
                    "[ИИ] Обнаружена критическая ошибка контекста Ollama (400). " +
                    "Автоматический сброс сессии диалога для восстановления работоспособности.")
                    .ConfigureAwait(false);
                ResetSession();
                await foreach (var tok in StreamInternalAsync(userMessage, true, cancellationToken))
                    yield return tok;
            }
            yield break;
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        var fullReply = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            OllamaChunk? chunk = null;
            try { chunk = JsonSerializer.Deserialize<OllamaChunk>(line, DeserializeOpts); }
            catch (JsonException) { continue; }

            if (chunk?.Message?.Content is { Length: > 0 } token)
            {
                fullReply.Append(token);
                yield return token;
            }
            if (chunk?.Done == true) break;
        }

        response.Dispose();

        // Zero-Image History: Base64 отправлен — немедленно освобождаем ссылку,
        // в историю пишется только текст с технической пометкой. Картинки не
        // накапливаются в контексте Ollama (VRAM) и в памяти процесса при долгой беседе.
        var hadImages = userMessage.Images is { Count: > 0 };
        userMessage.Images = null;

        var storedContent = hadImages
            ? $"{userMessage.Content}\n[Изображение обработано и удалено из VRAM]"
            : userMessage.Content;
        _history.Add(new ChatMessage("user", storedContent));
        if (fullReply.Length > 0)
            _history.Add(new ChatMessage("assistant", fullReply.ToString()));
        TrimHistory();

        if (hadImages)
            await _logger.LogInfoAsync(Component,
                "[VISION] Изображение обработано и удалено из истории диалога (Zero-Image History).")
                .ConfigureAwait(false);
    }

    // ── Диагностика ошибок Ollama API ────────────────────────────────────────────

    // Маркеры отказа модели обрабатывать изображения (формулировки зависят от версии Ollama)
    private static readonly string[] ImageRejectionMarkers =
        ["does not support images", "invalid parameter", "cannot decode",
         "unable to decode", "image input", "vision"];

    private static bool IsImageRejectionError(string error)
        => error.Length > 0
        && ImageRejectionMarkers.Any(m => error.Contains(m, StringComparison.OrdinalIgnoreCase));

    // Извлекает поле "error" из тела ответа Ollama: {"error":"сообщение"}.
    // Не-JSON тело возвращается как есть (обрезанное) — лучше сырая диагностика, чем никакой.
    private async Task<string> ReadOllamaErrorAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _logger.LogWarningAsync(Component,
                $"Не удалось прочитать тело ошибки Ollama: {ex.Message}").ConfigureAwait(false);
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err))
            {
                // Старая схема: {"error":"сообщение"}
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString() ?? string.Empty;

                // Новая схема: {"error":{"code":400,"message":"...","type":"..."}}
                if (err.ValueKind == JsonValueKind.Object)
                {
                    var message = err.TryGetProperty("message", out var msg)
                        && msg.ValueKind == JsonValueKind.String
                            ? msg.GetString() : null;
                    var type = err.TryGetProperty("type", out var typ)
                        && typ.ValueKind == JsonValueKind.String
                            ? typ.GetString() : null;

                    if (!string.IsNullOrEmpty(message))
                        return type is { Length: > 0 } ? $"{message} [{type}]" : message;
                }
            }
        }
        catch (JsonException) { }

        return body.Length > 500 ? body[..500] : body;
    }

    // Гарантирует строгое чередование user→assistant; объединяет дублирующиеся роли
    private static List<ChatMessage> SanitizeHistory(List<ChatMessage> history)
    {
        var clean = new List<ChatMessage>(history.Count);
        foreach (var msg in history)
        {
            if (clean.Count == 0)
            {
                if (msg.Role == "user") clean.Add(msg);
                continue;
            }
            var last = clean[^1];
            if (msg.Role == last.Role)
                clean[^1] = new ChatMessage(last.Role, last.Content + "\n" + msg.Content);
            else
                clean.Add(msg);
        }
        // Хвостовое user-сообщение удаляется: новый запрос добавляется отдельно
        if (clean.Count > 0 && clean[^1].Role == "user")
            clean.RemoveAt(clean.Count - 1);
        return clean;
    }

    public void ResetSession() => _history.Clear();

    private void TrimHistory()
    {
        while (_history.Count > MaxHistory)
            _history.RemoveAt(0);
    }

    private static readonly JsonSerializerOptions DeserializeOpts =
        new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions SerializeOpts =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record OllamaChunk(
        [property: JsonPropertyName("message")] ChunkMessage? Message,
        [property: JsonPropertyName("done")]    bool          Done);

    private sealed record ChunkMessage(
        [property: JsonPropertyName("content")] string? Content);
}
