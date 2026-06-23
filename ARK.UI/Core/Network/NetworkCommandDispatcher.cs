using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Services;

namespace ARK.UI.Core.Network;

/// <summary>
/// Разбирает входящие JSON-RPC 2.0 пакеты и диспетчеризует их к сервисам ARK.
/// Поддерживаемые методы: execute_macro, send_prompt, get_status, inject_input.
/// </summary>
public sealed class NetworkCommandDispatcher
{
    private const string Component = "Network";

    private readonly ILogService             _logger;
    private readonly IStorageManager         _storage;
    private readonly IMacroOrchestrator      _orchestrator;
    private readonly IActionService          _actionService;
    private readonly IConfigService          _configService;
    private readonly IObsService             _obsService;
    private readonly ISpeechSynthesisService _ttsService;

    private static readonly JsonSerializerOptions s_json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public NetworkCommandDispatcher(
        ILogService             logger,
        IStorageManager         storage,
        IMacroOrchestrator      orchestrator,
        IActionService          actionService,
        IConfigService          configService,
        IObsService             obsService,
        ISpeechSynthesisService ttsService)
    {
        _logger       = logger;
        _storage      = storage;
        _orchestrator = orchestrator;
        _actionService = actionService;
        _configService = configService;
        _obsService    = obsService;
        _ttsService    = ttsService;
    }

    // ── Точка входа ──────────────────────────────────────────────────────────

    public async Task<string> DispatchAsync(
        string clientIp, string json, CancellationToken ct)
    {
        string? id     = null;
        string? method = null;
        try
        {
            using var doc  = JsonDocument.Parse(json);
            var root       = doc.RootElement;
            id     = root.TryGetProperty("id",     out var idEl)     ? idEl.GetRawText()   : null;
            method = root.TryGetProperty("method", out var methodEl) ? methodEl.GetString() : null;
            JsonElement? paramsEl = root.TryGetProperty("params", out var pEl) ? pEl : null;

            await _logger.LogInfoAsync(Component,
                $"[NETWORK] Получена сетевая команда '{method}' от {clientIp}. Выполнение...")
                .ConfigureAwait(false);

            return method switch
            {
                "execute_macro" => await HandleExecuteMacroAsync(paramsEl, id, ct).ConfigureAwait(false),
                "send_prompt"   => await HandleSendPromptAsync  (paramsEl, id, ct).ConfigureAwait(false),
                "get_status"    => await HandleGetStatusAsync   (id, ct).ConfigureAwait(false),
                "inject_input"  => await HandleInjectInputAsync (paramsEl, id, ct).ConfigureAwait(false),
                _               => JsonRpcError(id, -32601, $"Метод не найден: '{method}'")
            };
        }
        catch (JsonException ex)
        {
            await _logger.LogErrorAsync(Component, "[NETWORK] Ошибка парсинга JSON-RPC пакета.", ex)
                .ConfigureAwait(false);
            return JsonRpcError(id, -32700, $"Ошибка разбора JSON: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return JsonRpcError(id, -32000, "Операция отменена.");
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                $"[NETWORK] Внутренняя ошибка при выполнении команды '{method}'.", ex)
                .ConfigureAwait(false);
            return JsonRpcError(id, -32603, ex.Message);
        }
    }

    // ── Обработчики команд ────────────────────────────────────────────────────

    private async Task<string> HandleExecuteMacroAsync(
        JsonElement? p, string? id, CancellationToken ct)
    {
        var macroName = p?.TryGetProperty("macroName", out var mn) == true ? mn.GetString() : null;
        var macroId   = p?.TryGetProperty("macroId",   out var mi) == true ? mi.GetString() : null;
        var nameOrId  = macroName ?? macroId;

        if (string.IsNullOrWhiteSpace(nameOrId))
            return JsonRpcError(id, -32602, "Требуется параметр 'macroName' или 'macroId'.");

        // Поиск по GUID или имени среди всех макросов
        Guid? foundId = null;
        if (Guid.TryParse(nameOrId, out var parsedGuid))
        {
            foundId = parsedGuid;
        }
        else
        {
            var all = await _storage.GetAllMacrosAsync(ct).ConfigureAwait(false);
            var match = all.FirstOrDefault(m =>
                m.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
            if (match is not null) foundId = match.Id;
        }

        if (foundId is null)
            return JsonRpcError(id, -32602, $"Макрос '{nameOrId}' не найден.");

        await _orchestrator.EnqueueMacroAsync(foundId.Value, ct: ct).ConfigureAwait(false);

        await _logger.LogInfoAsync(Component,
            $"[NETWORK] Команда 'execute_macro' успешно выполнена: '{nameOrId}'.")
            .ConfigureAwait(false);
        return JsonRpcOk(id, new { started = true, macro = nameOrId });
    }

    private async Task<string> HandleSendPromptAsync(
        JsonElement? p, string? id, CancellationToken ct)
    {
        var text = p?.TryGetProperty("text", out var t) == true ? t.GetString() : null;

        if (string.IsNullOrWhiteSpace(text))
            return JsonRpcError(id, -32602, "Требуется параметр 'text'.");

        await _logger.LogInfoAsync(Component,
            $"[NETWORK] Команда 'send_prompt' получена: '{text}'. AI-маршрутизация недоступна в V3.")
            .ConfigureAwait(false);
        return JsonRpcOk(id, new { queued = false, reason = "AI routing not available in V3" });
    }

    private async Task<string> HandleGetStatusAsync(string? id, CancellationToken ct)
    {
        var cfg    = _configService.Current;
        string win = GetActiveWindowName();

        await _logger.LogInfoAsync(Component,
            $"[NETWORK] Команда 'get_status' выполнена. activeWindow='{win}'.")
            .ConfigureAwait(false);

        return JsonRpcOk(id, new
        {
            isAiEnabled  = cfg.IsAiEnabled,
            isSpeaking   = _ttsService.IsSpeaking,
            activeWindow = win,
            obsConnected = _obsService.IsConnected
        });
    }

    private async Task<string> HandleInjectInputAsync(
        JsonElement? p, string? id, CancellationToken ct)
    {
        var action = p?.TryGetProperty("action", out var a) == true ? a.GetString() : null;

        switch (action)
        {
            case "click":
            {
                double x = p?.TryGetProperty("x", out var xEl) == true ? xEl.GetDouble() : 0;
                double y = p?.TryGetProperty("y", out var yEl) == true ? yEl.GetDouble() : 0;
                await _actionService.ClickAsync(x, y, ct).ConfigureAwait(false);
                break;
            }
            case "right_click":
            {
                double x = p?.TryGetProperty("x", out var xEl) == true ? xEl.GetDouble() : 0;
                double y = p?.TryGetProperty("y", out var yEl) == true ? yEl.GetDouble() : 0;
                await _actionService.RightClickAsync(x, y, ct).ConfigureAwait(false);
                break;
            }
            case "move":
            {
                double x = p?.TryGetProperty("x", out var xEl) == true ? xEl.GetDouble() : 0;
                double y = p?.TryGetProperty("y", out var yEl) == true ? yEl.GetDouble() : 0;
                await _actionService.MoveAsync(x, y, ct).ConfigureAwait(false);
                break;
            }
            case "key":
            {
                var keyStr = p?.TryGetProperty("key", out var kEl) == true ? kEl.GetString() : null;
                if (!string.IsNullOrEmpty(keyStr)
                    && Enum.TryParse<Key>(keyStr, ignoreCase: true, out var key))
                    await _actionService.PressKeyAsync(key, ct).ConfigureAwait(false);
                else
                    return JsonRpcError(id, -32602, $"Неверное значение ключа: '{keyStr}'.");
                break;
            }
            default:
                return JsonRpcError(id, -32602, $"Неизвестное действие: '{action}'.");
        }

        await _logger.LogInfoAsync(Component,
            $"[NETWORK] Команда 'inject_input' (action={action}) успешно выполнена.")
            .ConfigureAwait(false);
        return JsonRpcOk(id, new { done = true, action });
    }

    // ── Вспомогательные методы ────────────────────────────────────────────────

    private static string GetActiveWindowName()
    {
        var hwnd = Win32Api.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return string.Empty;
        Win32Api.GetWindowThreadProcessId(hwnd, out uint pid);
        try   { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return string.Empty; }
    }

    private static string JsonRpcOk(string? id, object result)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, s_json);

    private static string JsonRpcError(string? id, int code, string message)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, s_json);
}
