namespace ARK.UI.Core.Services;

/// <summary>
/// Эвристика возможностей моделей Ollama по имени: API /api/tags не сообщает,
/// поддерживает ли модель зрение, поэтому проверяем известные маркеры vision-моделей.
/// </summary>
public static class OllamaModelCapabilities
{
    private static readonly string[] VisionMarkers =
        ["llava", "vl", "vision", "moondream", "bakllava", "minicpm", "gemma3", "pixtral"];

    public static bool IsLikelyMultimodal(string? modelName)
        => !string.IsNullOrWhiteSpace(modelName)
        && VisionMarkers.Any(m => modelName.Contains(m, StringComparison.OrdinalIgnoreCase));
}
