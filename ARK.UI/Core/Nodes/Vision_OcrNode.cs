using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Nodes;

public sealed class Vision_OcrNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(ImagePath);

    private string _imagePath = string.Empty;
    public string ImagePath
    {
        get => _imagePath;
        set { if (_imagePath != value) { _imagePath = value; OnPropertyChanged(); } }
    }

    private string _languageCode = "ru";
    public string LanguageCode
    {
        get => _languageCode;
        set { if (_languageCode != value) { _languageCode = value; OnPropertyChanged(); } }
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        TryApplyContextInput<string>(nameof(ImagePath), v => ImagePath = v);

        if (string.IsNullOrWhiteSpace(ImagePath))
        {
            await logger.LogErrorAsync(Name, "[OCR] Путь к изображению не указан.").ConfigureAwait(false);
            return false;
        }

        string absPath = Path.GetFullPath(ImagePath);
        if (!File.Exists(absPath))
        {
            await logger.LogErrorAsync(Name, $"[OCR] Файл не найден: {absPath}").ConfigureAwait(false);
            return false;
        }

        var language = new Windows.Globalization.Language(LanguageCode);
        var engine   = OcrEngine.TryCreateFromLanguage(language);
        if (engine is null)
        {
            await logger.LogErrorAsync(Name,
                $"[OCR] Язык «{LanguageCode}» не поддерживается или не установлен в системе.")
                .ConfigureAwait(false);
            return false;
        }

        StorageFile storageFile = await StorageFile.GetFileFromPathAsync(absPath);
        using IRandomAccessStream stream = await storageFile.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder             = await BitmapDecoder.CreateAsync(stream);
        using SoftwareBitmap bitmap       = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        OcrResult result = await engine.RecognizeAsync(bitmap);
        LastOutputValue  = result.Text;

        await logger.LogInfoAsync(Name, $"[OCR] Распознано: {result.Text.Length} симв.").ConfigureAwait(false);
        return true;
    }
}
