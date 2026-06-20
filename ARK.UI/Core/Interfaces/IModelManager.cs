using System.IO;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface IModelManager : IAsyncDisposable
{
    ModelType ActiveModelType { get; }
    bool      IsReady         { get; }

    Task         InitializeAsync(CancellationToken ct = default);
    Task         SwitchModelAsync(ModelType type, string modelPath, string language, CancellationToken ct = default);
    Task         SwitchEngineAsync(SpeechEngineMode engine, string language, CancellationToken ct = default);
    Task<string> RecognizeAsync(Stream audioWav, CancellationToken ct = default);
    Task         WhenReadyAsync();
}
