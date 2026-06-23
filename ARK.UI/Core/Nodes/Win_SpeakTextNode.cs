using System.IO;
using ARK.UI.Core.Bus;
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

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            var _s = _raw as string ?? _raw?.ToString();
            if (_s is not null) TextToSpeak = _s;
        }

        var tts         = NodeServices!.GetRequiredService<ISpeechSynthesisService>();
        var config      = NodeServices!.GetRequiredService<IConfigService>().Current;
        var currentMode = config.SelectedTtsMode;

        if (currentMode == TtsMode.Disabled)
        {
            await NodeLogger!.LogWarningAsync(Name,
                "[СИНТЕЗ] Синтез речи отключен в глобальных настройках.")
                .ConfigureAwait(false);
            return NodeResult.Failure("Синтез речи отключен.");
        }

        if (string.IsNullOrWhiteSpace(TextToSpeak))
        {
            await NodeLogger!.LogWarningAsync(Name, "Текст для озвучки пуст. Пропуск.").ConfigureAwait(false);
            return NodeResult.Success(null);
        }

        var voiceName = string.IsNullOrWhiteSpace(SelectedVoice) ? config.SelectedTtsVoice : SelectedVoice;
        var modelPath = currentMode == TtsMode.Kokoro
            ? voiceName + ".bin"
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "TTS", "Piper", voiceName + ".onnx");

        await NodeLogger!.LogInfoAsync(Name,
            $"[СИНТЕЗ] Озвучивание текста: '{TextToSpeak}' голосом '{voiceName}'...").ConfigureAwait(false);

        await tts.SpeakAsync(TextToSpeak, modelPath, config.TtsSpeed, config.TtsVolume, ct).ConfigureAwait(false);
        return NodeResult.Success(null);
    }
}
