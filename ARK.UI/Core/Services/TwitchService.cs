using System.IO;
using System.Net.Sockets;
using System.Text;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class TwitchService : ITwitchService
{
    private const string IrcHost    = "irc.chat.twitch.tv";
    private const int    IrcPort    = 6667;
    private const string TtsCommand = "!tts ";
    private const string Component  = "Twitch";

    private readonly ILogService             _logger;
    private readonly IConfigService          _configService;
    private readonly ISpeechSynthesisService _ttsService;
    private readonly IOverlayService         _overlayService;

    private TcpClient?                _client;
    private StreamWriter?             _writer;
    private CancellationTokenSource?  _cts;
    private string                    _channel  = string.Empty;
    private volatile bool             _isConnected;

    public bool IsConnected => _isConnected;

    public event EventHandler<TwitchMessageEventArgs>? OnMessageReceived;
    public event EventHandler<bool>?                   ConnectionStatusChanged;

    public TwitchService(
        ILogService             logger,
        IConfigService          configService,
        ISpeechSynthesisService ttsService,
        IOverlayService         overlayService)
    {
        _logger         = logger;
        _configService  = configService;
        _ttsService     = ttsService;
        _overlayService = overlayService;
    }

    // ── Подключение ──────────────────────────────────────────────────────────

    public async Task ConnectAsync(
        string channel, string username, string oauthToken,
        CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        _channel = channel.TrimStart('#').ToLowerInvariant();
        _cts     = new CancellationTokenSource();

        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(IrcHost, IrcPort, ct).ConfigureAwait(false);

            var stream = _client.GetStream();
            _writer    = new StreamWriter(stream, Encoding.UTF8, bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true
            };
            var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);

            // IRC handshake: PASS перед NICK обязателен для Twitch
            await _writer.WriteLineAsync($"PASS oauth:{oauthToken}").ConfigureAwait(false);
            await _writer.WriteLineAsync($"NICK {username.ToLowerInvariant()}").ConfigureAwait(false);
            await _writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands").ConfigureAwait(false);
            await _writer.WriteLineAsync($"JOIN #{_channel}").ConfigureAwait(false);

            SetConnected(true);
            await _logger.LogInfoAsync(Component,
                $"[TWITCH] Подключено к IRC-чату #{_channel}.").ConfigureAwait(false);

            _ = Task.Run(() => ReceiveLoopAsync(reader, _cts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetConnected(false);
            _client?.Dispose();
            _client  = null;
            _writer?.Dispose();
            _writer  = null;
            await _logger.LogErrorAsync(Component, "[TWITCH] Ошибка подключения к IRC.", ex)
                .ConfigureAwait(false);
        }
    }

    // ── Отключение ───────────────────────────────────────────────────────────

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_writer is not null)
        {
            try { await _writer.WriteLineAsync("QUIT").ConfigureAwait(false); } catch { }
            try { _writer.Dispose(); } catch { }
            _writer = null;
        }

        if (_client is not null)
        {
            try { _client.Dispose(); } catch { }
            _client = null;
        }

        _cts?.Dispose();
        _cts = null;

        if (_isConnected)
        {
            SetConnected(false);
            await _logger.LogInfoAsync(Component, "[TWITCH] Отключено от IRC.").ConfigureAwait(false);
        }
    }

    // ── Цикл чтения ─────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                await ProcessLineAsync(line, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            if (!ct.IsCancellationRequested)
                await _logger.LogInfoAsync(Component,
                    $"[TWITCH] Соединение разорвано: {ex.Message}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component, "[TWITCH] Ошибка в цикле чтения IRC.", ex)
                .ConfigureAwait(false);
        }
        finally
        {
            reader.Dispose();
            if (_isConnected)
            {
                SetConnected(false);
                await _logger.LogInfoAsync(Component, "[TWITCH] IRC-соединение закрыто.").ConfigureAwait(false);
            }
        }
    }

    // ── Парсинг IRC-строки ───────────────────────────────────────────────────

    private async Task ProcessLineAsync(string line, CancellationToken ct)
    {
        // PING → PONG (keepalive Twitch)
        if (line.StartsWith("PING ", StringComparison.Ordinal))
        {
            if (_writer is not null)
                await _writer.WriteLineAsync("PONG " + line[5..]).ConfigureAwait(false);
            return;
        }

        // Разбор IRC-тегов (@key=val;...)
        bool   isMod = false, isSub = false, isVip = false;
        string payload = line;

        if (line.StartsWith('@'))
        {
            var tagEnd = line.IndexOf(' ');
            if (tagEnd > 0)
            {
                ParseTags(line.AsSpan(1, tagEnd - 1), out isMod, out isSub, out isVip);
                payload = line[(tagEnd + 1)..];
            }
        }

        // Ищем PRIVMSG
        var privIdx = payload.IndexOf(" PRIVMSG ", StringComparison.Ordinal);
        if (privIdx < 0) return;

        // Извлекаем ник из :nick!nick@...
        var nick = ExtractNick(payload);
        if (nick.Length == 0) return;

        // Текст после последнего ":"
        var msgPart    = payload[(privIdx + 9)..];
        var colonIdx   = msgPart.IndexOf(':');
        if (colonIdx < 0) return;
        var text = msgPart[(colonIdx + 1)..].TrimEnd('\r', '\n');

        var args = new TwitchMessageEventArgs
        {
            Username     = nick,
            Text         = text,
            IsModerator  = isMod,
            IsSubscriber = isSub,
            IsVip        = isVip
        };
        OnMessageReceived?.Invoke(this, args);

        // !tts команда
        if (text.StartsWith(TtsCommand, StringComparison.OrdinalIgnoreCase))
        {
            if (isMod || isSub || isVip)
                await HandleTtsAsync(nick, text[TtsCommand.Length..], ct).ConfigureAwait(false);
            else
                await _logger.LogInfoAsync(Component,
                    $"[TWITCH] @{nick} не имеет прав для !tts (требуется мод / саб / VIP).")
                    .ConfigureAwait(false);
        }
    }

    private static void ParseTags(
        ReadOnlySpan<char> tags,
        out bool mod, out bool sub, out bool vip)
    {
        mod = sub = vip = false;
        foreach (var pair in tags.Split(';'))
        {
            var p   = tags[pair];
            var eq  = p.IndexOf('=');
            if (eq < 0) continue;
            var key = p[..eq];
            var val = p[(eq + 1)..];
            if (key.SequenceEqual("mod"))        mod = val.SequenceEqual("1");
            else if (key.SequenceEqual("subscriber")) sub = val.SequenceEqual("1");
            else if (key.SequenceEqual("vip"))   vip = val.SequenceEqual("1");
        }
    }

    private static string ExtractNick(string payload)
    {
        if (!payload.StartsWith(':')) return string.Empty;
        var bang = payload.IndexOf('!');
        return bang > 1 ? payload[1..bang] : string.Empty;
    }

    // ── Обработка !tts ──────────────────────────────────────────────────────

    private async Task HandleTtsAsync(string user, string text, CancellationToken ct)
    {
        var cleanText = text.Trim();
        if (string.IsNullOrWhiteSpace(cleanText)) return;

        var cfg = _configService.Current;
        if (cfg.SelectedTtsMode == TtsMode.Disabled)
        {
            await _logger.LogInfoAsync(Component,
                "[TWITCH] !tts проигнорирован — глобальный TTS отключён в настройках.")
                .ConfigureAwait(false);
            return;
        }

        string modelPath = cfg.SelectedTtsMode == TtsMode.Kokoro
            ? cfg.SelectedTtsVoice + ".bin"
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Models", "TTS", "Piper", cfg.SelectedTtsVoice + ".onnx");

        try
        {
            await _logger.LogInfoAsync(Component,
                $"[TWITCH] Озвучено сообщение от @{user}: '{cleanText}'").ConfigureAwait(false);

            await _overlayService.ShowTextAsync(
                $"@{user}: {cleanText}", durationMilliseconds: 5000, ct).ConfigureAwait(false);

            await _ttsService.SpeakAsync(
                cleanText, modelPath, cfg.TtsSpeed, cfg.TtsVolume, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                $"[TWITCH] Ошибка TTS для @{user}.", ex).ConfigureAwait(false);
        }
    }

    // ── Вспомогательный метод ────────────────────────────────────────────────

    private void SetConnected(bool value)
    {
        _isConnected = value;
        ConnectionStatusChanged?.Invoke(this, value);
    }
}
