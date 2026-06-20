namespace ARK.UI.Core.Interfaces;

public interface ISpeechSynthesisService : IDisposable
{
    bool IsSpeaking { get; }

    Task SpeakAsync(string text, string modelPath,
        double speed = 1.0, double volume = 1.0,
        CancellationToken ct = default);

    void Stop();
}
