using System.IO;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface IModelWrapper : IAsyncDisposable
{
    ModelType Type    { get; }
    bool      IsReady { get; }

    Task         InitializeAsync(string modelPath, string language, CancellationToken ct = default);
    Task<string> RecognizeAsync(Stream audioWav, CancellationToken ct = default);
}
