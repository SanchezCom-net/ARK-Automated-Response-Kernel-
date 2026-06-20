using System.Text.Json.Serialization;

namespace ARK.Voice.Worker;

/// <summary>
/// Зеркало ARK.UI.Core.Models.WhisperWorkerConfig.
/// Десериализуется из Base64-JSON аргумента --config-b64 при старте WhisperHost.exe.
/// </summary>
internal sealed record WhisperWorkerConfig
{
    [JsonPropertyName("model_path")] public string ModelPath  { get; init; } = string.Empty;
    [JsonPropertyName("language")]   public string Language   { get; init; } = "ru";
    [JsonPropertyName("use_gpu")]    public bool   UseGpu     { get; init; } = false;
    [JsonPropertyName("precision")]  public string Precision  { get; init; } = "f16";
    [JsonPropertyName("model_type")] public string ModelType  { get; init; } = "base";
    [JsonPropertyName("gpu_device")] public int    GpuDevice  { get; init; } = 0;
}
