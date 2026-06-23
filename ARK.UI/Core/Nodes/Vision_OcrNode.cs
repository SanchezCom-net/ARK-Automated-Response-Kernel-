using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using ARK.UI.Core.Bus;

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

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            var _s = _raw as string ?? _raw?.ToString();
            if (_s is not null) ImagePath = _s;
        }

        if (string.IsNullOrWhiteSpace(ImagePath))
        {
            await NodeLogger!.LogErrorAsync(Name, "[OCR] Путь к изображению не указан.").ConfigureAwait(false);
            return NodeResult.Failure("Путь к изображению не указан.");
        }

        string absPath = Path.GetFullPath(ImagePath);
        if (!File.Exists(absPath))
        {
            await NodeLogger!.LogErrorAsync(Name, $"[OCR] Файл не найден: {absPath}").ConfigureAwait(false);
            return NodeResult.Failure($"Файл не найден: {absPath}");
        }

        var language = new Windows.Globalization.Language(LanguageCode);
        var engine   = OcrEngine.TryCreateFromLanguage(language);
        if (engine is null)
        {
            await NodeLogger!.LogErrorAsync(Name,
                $"[OCR] Язык «{LanguageCode}» не поддерживается или не установлен в системе.")
                .ConfigureAwait(false);
            return NodeResult.Failure($"OCR: язык «{LanguageCode}» не поддерживается.");
        }

        StorageFile storageFile = await StorageFile.GetFileFromPathAsync(absPath);
        using IRandomAccessStream stream = await storageFile.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder             = await BitmapDecoder.CreateAsync(stream);
        using SoftwareBitmap bitmap       = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        OcrResult ocrResult = await engine.RecognizeAsync(bitmap);
        string    ocrText   = ocrResult.Text;
        LastOutputValue     = ocrText;

        await NodeLogger!.LogInfoAsync(Name, $"[OCR] Распознано: {ocrText.Length} симв.").ConfigureAwait(false);

        var _sid = inputPacket?.SessionId ?? Guid.NewGuid();
        var _out = DataBusPacket.Text(_sid);
        DataBus?.Set(_out.SessionId, _out.DataId, ocrText);
        return NodeResult.Success(_out);
    }
}
