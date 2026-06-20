using System.Net.Http;
using System.Text;
using System.Text.Json;
using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Nodes;

public sealed class Win_TranslateNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(SourceText);

    private static readonly HttpClient _http = new();

    private string _sourceText = string.Empty;
    public string SourceText
    {
        get => _sourceText;
        set { if (_sourceText != value) { _sourceText = value; OnPropertyChanged(); } }
    }

    private string _targetLanguageCode = "ru";
    public string TargetLanguageCode
    {
        get => _targetLanguageCode;
        set { if (_targetLanguageCode != value) { _targetLanguageCode = value; OnPropertyChanged(); } }
    }

    private string _translatedText = string.Empty;
    public string TranslatedText
    {
        get => _translatedText;
        set { if (_translatedText != value) { _translatedText = value; OnPropertyChanged(); } }
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        TryApplyContextInput<string>(nameof(SourceText), v => SourceText = v);

        if (string.IsNullOrWhiteSpace(SourceText))
        {
            await logger.LogInfoAsync(Name, "[ПЕРЕВОД] Исходный текст пуст — нода пропущена.").ConfigureAwait(false);
            return true;
        }

        string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={TargetLanguageCode}&dt=t&q={Uri.EscapeDataString(SourceText)}";

        string responseJson;
        try
        {
            responseJson = await Task.Run(() => _http.GetStringAsync(url, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await logger.LogErrorAsync(Name, "[ПЕРЕВОД] Ошибка обращения к Google Translate API.", ex).ConfigureAwait(false);
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var firstArray = root[0];
                if (firstArray.ValueKind == JsonValueKind.Array && firstArray.GetArrayLength() > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var segment in firstArray.EnumerateArray())
                    {
                        if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0)
                            sb.Append(segment[0].GetString());
                    }

                    string translated = sb.ToString();
                    TranslatedText   = translated;
                    LastOutputValue  = translated;

                    await logger.LogInfoAsync(Name, $"[ПЕРЕВОД] → «{TargetLanguageCode}» ({translated.Length} симв.).")
                        .ConfigureAwait(false);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            await logger.LogErrorAsync(Name, "[ПЕРЕВОД] Ошибка парсинга ответа Google Translate.", ex).ConfigureAwait(false);
            return false;
        }

        await logger.LogErrorAsync(Name, "[ПЕРЕВОД] Неожиданная структура ответа Google Translate.").ConfigureAwait(false);
        return false;
    }
}
