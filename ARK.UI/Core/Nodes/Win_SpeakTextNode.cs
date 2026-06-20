using System.IO;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class Win_SpeakTextNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(TextToSpeak);

    private string _textToSpeak   = string.Empty;
    private string _selectedVoice = string.Empty;

    public string TextToSpeak
    {
        get => _textToSpeak;
        set { if (_textToSpeak != value)   { _textToSpeak   = value; OnPropertyChanged(); } }
    }

    public string SelectedVoice
    {
        get => _selectedVoice;
        set { if (_selectedVoice != value) { _selectedVoice = value; OnPropertyChanged(); } }
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider, ILogService logger, CancellationToken ct)
    {
        TryApplyContextInput<string>(nameof(TextToSpeak), v => TextToSpeak = v);

        var tts        = serviceProvider.GetRequiredService<ISpeechSynthesisService>();
        var config     = serviceProvider.GetRequiredService<IConfigService>().Current;
        var currentMode = config.SelectedTtsMode;

        if (currentMode == TtsMode.Disabled)
        {
            await logger.LogWarningAsync(Name,
                "[СИНТЕЗ] Синтез речи отключен в глобальных настройках. Переход по ветке On Error.")
                .ConfigureAwait(false);
            return false;
        }

        if (string.IsNullOrWhiteSpace(TextToSpeak))
        {
            await logger.LogWarningAsync(Name, "Текст для озвучки пуст. Пропуск.").ConfigureAwait(false);
            return true;
        }

        var voiceName = string.IsNullOrWhiteSpace(SelectedVoice) ? config.SelectedTtsVoice : SelectedVoice;
        var modelPath = currentMode == TtsMode.Kokoro
            ? voiceName + ".bin"
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "TTS", "Piper", voiceName + ".onnx");

        await logger.LogInfoAsync(Name,
            $"[СИНТЕЗ] Озвучивание текста: '{TextToSpeak}' голосом '{voiceName}'...").ConfigureAwait(false);

        await tts.SpeakAsync(TextToSpeak, modelPath, config.TtsSpeed, config.TtsVolume, ct).ConfigureAwait(false);
        return true;
    }
}
