using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using WpfApp = System.Windows.Application;
using WpfClipboard = System.Windows.Clipboard;

namespace ARK.UI.Core.Nodes;

public sealed class ClipboardNode : BaseNode
{
    public static readonly ClipboardActionType[]  AllActionTypes  = Enum.GetValues<ClipboardActionType>();
    public static readonly ClipboardDataType[]    AllDataTypes    = Enum.GetValues<ClipboardDataType>();
    public static readonly ClipboardImageAction[] AllImageActions = Enum.GetValues<ClipboardImageAction>();

    public override string DefaultDataInputPropertyName => nameof(TextToWrite);

    // ── Тип данных (Text / Image) ─────────────────────────────────────────

    private ClipboardDataType _selectedDataType = ClipboardDataType.Text;
    public ClipboardDataType SelectedDataType
    {
        get => _selectedDataType;
        set { if (_selectedDataType != value) { _selectedDataType = value; OnPropertyChanged(); } }
    }

    // ── Текстовый режим ───────────────────────────────────────────────────

    private ClipboardActionType _actionType = ClipboardActionType.ReadText;
    public ClipboardActionType ActionType
    {
        get => _actionType;
        set { if (_actionType != value) { _actionType = value; OnPropertyChanged(); } }
    }

    private string _textToWrite = string.Empty;
    public string TextToWrite
    {
        get => _textToWrite;
        set { if (_textToWrite != value) { _textToWrite = value; OnPropertyChanged(); } }
    }

    // ── Графический режим ─────────────────────────────────────────────────

    private ClipboardImageAction _selectedImageAction = ClipboardImageAction.ReadImage;
    public ClipboardImageAction SelectedImageAction
    {
        get => _selectedImageAction;
        set { if (_selectedImageAction != value) { _selectedImageAction = value; OnPropertyChanged(); } }
    }

    // ── Выполнение ────────────────────────────────────────────────────────

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        // ── Полиморфный приём: DataPacket с серебряного провода ───────────
        DataPacket? packet = null;
        TryApplyContextInput<DataPacket>(nameof(TextToWrite), v => packet = v);

        if (packet is not null)
        {
            DebugSink?.Invoke(
                $"[БУФЕР] DataPacket получен: тип={packet.Type}, " +
                $"payload={packet.Payload?.GetType().Name}, meta=\"{packet.MetaData}\"");
            return await ExecuteFromPacketAsync(packet, logger).ConfigureAwait(false);
        }

        // ── Ручной режим: SelectedDataType + ActionType / SelectedImageAction ─
        DebugSink?.Invoke(
            $"[БУФЕР] Ручной режим: тип={SelectedDataType}, действие=" +
            (SelectedDataType == ClipboardDataType.Text
                ? ActionType.ToString()
                : SelectedImageAction.ToString()));

        return SelectedDataType == ClipboardDataType.Text
            ? await ExecuteTextAsync(serviceProvider, logger, cancellationToken).ConfigureAwait(false)
            : await ExecuteImageAsync(logger).ConfigureAwait(false);
    }

    // ── Полиморфный путь: DataPacket → Clipboard ──────────────────────────

    private async Task<bool> ExecuteFromPacketAsync(DataPacket packet, ILogService logger)
    {
        switch (packet.Type)
        {
            case DataType.Text:
            {
                string text = packet.Payload?.ToString() ?? string.Empty;
                DebugSink?.Invoke($"[БУФЕР] [ТЕКСТ] Записываю ({text.Length} симв.): \"{text}\"...");

                string? err = await SetClipboardTextWithRetryAsync(text).ConfigureAwait(false);

                if (err is not null)
                {
                    DebugSink?.Invoke($"[БУФЕР] ⚠ OLE-ошибка при записи текста: {err}");
                    await logger.LogWarningAsync(Name, $"[БУФЕР] DataPacket.Text ошибка: {err}").ConfigureAwait(false);
                    return false;
                }

                LastOutputValue = new DataPacket { Payload = text, Type = DataType.Text };
                await logger.LogInfoAsync(Name, $"[БУФЕР] DataPacket.Text записан ({text.Length} симв.)").ConfigureAwait(false);
                DebugSink?.Invoke($"[БУФЕР] ✓ Текст записан ({text.Length} симв.) → DataPacket.Text в выходной порт.");
                return true;
            }

            case DataType.Image:
            {
                DebugSink?.Invoke($"[БУФЕР] [ИЗОБРАЖЕНИЕ] Payload: {packet.Payload?.GetType().Name}...");

                string? err = null;
                await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        switch (packet.Payload)
                        {
                            case BitmapSource bmp:
                                WpfClipboard.SetImage(bmp);
                                break;
                            case string path when File.Exists(path):
                                var frame = BitmapFrame.Create(
                                    new Uri(Path.GetFullPath(path)),
                                    BitmapCreateOptions.None,
                                    BitmapCacheOption.OnLoad);
                                WpfClipboard.SetImage(frame);
                                break;
                            default:
                                err = "Payload не является BitmapSource или корректным путём к файлу.";
                                break;
                        }
                    }
                    catch (Exception ex) { err = ex.Message; }
                }).Task.ConfigureAwait(false);

                if (err is not null)
                {
                    DebugSink?.Invoke($"[БУФЕР] ⚠ Ошибка записи изображения: {err}");
                    await logger.LogWarningAsync(Name, $"[БУФЕР] DataPacket.Image ошибка: {err}").ConfigureAwait(false);
                    return false;
                }

                LastOutputValue = packet;
                await logger.LogInfoAsync(Name, "[БУФЕР] DataPacket.Image записан в буфер обмена.").ConfigureAwait(false);
                DebugSink?.Invoke("[БУФЕР] ✓ Изображение записано → DataPacket.Image в выходной порт.");
                return true;
            }

            case DataType.File:
            {
                DebugSink?.Invoke($"[БУФЕР] [ФАЙЛ] Payload: {packet.Payload?.GetType().Name}...");

                string? err      = null;
                int     fileCount = 0;
                await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var files = new StringCollection();
                        switch (packet.Payload)
                        {
                            case StringCollection sc:
                                foreach (string? s in sc) if (s is not null) files.Add(s);
                                break;
                            case string path:
                                files.Add(path);
                                break;
                            case IEnumerable<string> paths:
                                foreach (var p in paths) files.Add(p);
                                break;
                            default:
                                err = "Payload не является путём или StringCollection.";
                                break;
                        }
                        if (err is null)
                        {
                            WpfClipboard.SetFileDropList(files);
                            fileCount = files.Count;
                        }
                    }
                    catch (Exception ex) { err = ex.Message; }
                }).Task.ConfigureAwait(false);

                if (err is not null)
                {
                    DebugSink?.Invoke($"[БУФЕР] ⚠ Ошибка записи файлов: {err}");
                    await logger.LogWarningAsync(Name, $"[БУФЕР] DataPacket.File ошибка: {err}").ConfigureAwait(false);
                    return false;
                }

                LastOutputValue = packet;
                await logger.LogInfoAsync(Name, $"[БУФЕР] DataPacket.File: {fileCount} файл(ов) в буфере.").ConfigureAwait(false);
                DebugSink?.Invoke($"[БУФЕР] ✓ {fileCount} файл(ов) помещено в буфер обмена.");
                return true;
            }

            default:
                DebugSink?.Invoke($"[БУФЕР] ⚠ Тип DataPacket '{packet.Type}' не поддерживается буфером обмена.");
                await logger.LogWarningAsync(Name, $"[БУФЕР] Неподдерживаемый тип пакета: {packet.Type}").ConfigureAwait(false);
                return false;
        }
    }

    // ── Ручной текстовый путь ─────────────────────────────────────────────

    private async Task<bool> ExecuteTextAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        switch (ActionType)
        {
            case ClipboardActionType.WriteText:
            {
                DebugSink?.Invoke("[БУФЕР] Режим записи текста. Ищем данные на серебряном проводе...");
                bool received = TryApplyContextInput<string>(nameof(TextToWrite), v => TextToWrite = v);
                DebugSink?.Invoke(received
                    ? $"[ВХОД] Данные приняты. Значение: \"{TextToWrite}\""
                    : $"[ВХОД] Провод не подключён. Статическое значение: \"{TextToWrite}\"");

                // Empty Write Guard: пустая запись создаёт призрачную ячейку в истории Win+V.
                if (string.IsNullOrWhiteSpace(TextToWrite))
                {
                    DebugSink?.Invoke("[БУФЕР] [ПРОПУСК] Текст для записи пуст. Физическая запись в буфер обмена Windows отменена для сохранения чистоты истории (Win+V).");
                    await logger.LogInfoAsync(Name, "[БУФЕР] WriteText пропущен: пустое значение.").ConfigureAwait(false);
                    return true;
                }

                DebugSink?.Invoke($"[БУФЕР] Записываю в буфер: \"{TextToWrite}\"...");
                string? err = await SetClipboardTextWithRetryAsync(TextToWrite).ConfigureAwait(false);

                if (err is not null)
                {
                    await logger.LogWarningAsync(Name, $"[БУФЕР] Ошибка записи: {err}").ConfigureAwait(false);
                    DebugSink?.Invoke($"[БУФЕР] ⚠ OLE-ошибка: {err}");
                    return false;
                }

                LastOutputValue = new DataPacket { Payload = TextToWrite, Type = DataType.Text };
                await logger.LogInfoAsync(Name, $"[БУФЕР] Текст записан ({TextToWrite.Length} симв.)").ConfigureAwait(false);
                DebugSink?.Invoke($"[БУФЕР] ✓ Текст записан ({TextToWrite.Length} симв.) → DataPacket.Text в выходной порт.");
                return true;
            }

            case ClipboardActionType.ReadText:
            {
                DebugSink?.Invoke("[БУФЕР] Чтение текста из буфера обмена...");
                var (clipText, readErr) = await GetClipboardTextWithRetryAsync().ConfigureAwait(false);

                if (readErr is not null)
                {
                    DebugSink?.Invoke($"[БУФЕР] ⚠ OLE-ошибка при чтении: {readErr}");
                    await logger.LogWarningAsync(Name, $"[БУФЕР] ReadText ошибка: {readErr}").ConfigureAwait(false);
                    return false;
                }

                LastOutputValue = new DataPacket
                {
                    Payload  = clipText,
                    Type     = DataType.Text,
                    MetaData = $"length:{clipText.Length}"
                };

                if (string.IsNullOrEmpty(clipText))
                    DebugSink?.Invoke("[БУФЕР] ⚠ Буфер пуст. DataPacket с пустым Payload.");
                else
                {
                    await logger.LogInfoAsync(Name, $"[БУФЕР] Прочитано: «{clipText}»").ConfigureAwait(false);
                    DebugSink?.Invoke($"[ВЫХОД] Считан текст ({clipText.Length} симв.) → DataPacket.Text в выходной порт.");
                }
                return true;
            }

            case ClipboardActionType.CopyActiveText:
            {
                DebugSink?.Invoke("[БУФЕР] Ctrl+C: копирую выделенное в активном окне...");
                var action = serviceProvider.GetRequiredService<IActionService>();
                await action.PressKeyWithModifiersAsync(Key.C, ModifierKeys.Control, cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);

                string copied = string.Empty;
                await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (WpfClipboard.ContainsText()) copied = WpfClipboard.GetText();
                }).Task.ConfigureAwait(false);

                LastOutputValue = new DataPacket { Payload = copied, Type = DataType.Text, MetaData = "ctrl+c" };
                await logger.LogInfoAsync(Name, $"[БУФЕР] Ctrl+C → «{copied}»").ConfigureAwait(false);
                DebugSink?.Invoke($"[ВЫХОД] Ctrl+C → \"{copied}\" → DataPacket.Text в выходной порт.");
                return true;
            }

            default:
                return true;
        }
    }

    // ── Ручной графический путь ───────────────────────────────────────────

    private async Task<bool> ExecuteImageAsync(ILogService logger)
    {
        switch (SelectedImageAction)
        {
            case ClipboardImageAction.ReadImage:
            {
                DebugSink?.Invoke("[БУФЕР] Чтение изображения из буфера обмена...");
                bool hasImage = await WpfApp.Current.Dispatcher.InvokeAsync(
                    WpfClipboard.ContainsImage).Task.ConfigureAwait(false);

                if (!hasImage)
                {
                    DebugSink?.Invoke("[БУФЕР] ⚠ Буфер не содержит изображения.");
                    break;
                }

                string? savedPath = await WpfApp.Current.Dispatcher.InvokeAsync<string?>(() =>
                {
                    BitmapSource? bmp = WpfClipboard.GetImage();
                    if (bmp is null) return null;
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    string path = Path.GetFullPath("Models/TTS/temp_clip.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                    return path;
                }).Task.ConfigureAwait(false);

                if (savedPath is not null)
                {
                    LastOutputValue = new DataPacket
                    {
                        Payload  = savedPath,
                        Type     = DataType.Image,
                        MetaData = "path"
                    };
                    await logger.LogInfoAsync(Name, "[БУФЕР] Изображение прочитано → сохранено на диск.").ConfigureAwait(false);
                    DebugSink?.Invoke($"[ВЫХОД] Изображение: \"{savedPath}\" → DataPacket.Image в выходной порт.");
                }
                break;
            }

            case ClipboardImageAction.WriteImage:
            {
                DebugSink?.Invoke("[БУФЕР] Запись изображения. Ищем путь на серебряном проводе...");
                bool received = TryApplyContextInput<string>(nameof(TextToWrite), v => TextToWrite = v);
                DebugSink?.Invoke(received
                    ? $"[ВХОД] Путь принят: \"{TextToWrite}\""
                    : $"[ВХОД] Провод не подключён. Путь: \"{TextToWrite}\"");

                if (string.IsNullOrWhiteSpace(TextToWrite) || !File.Exists(TextToWrite))
                {
                    DebugSink?.Invoke($"[БУФЕР] ⚠ Файл не найден: \"{TextToWrite}\"");
                    break;
                }

                await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                {
                    var frame = BitmapFrame.Create(
                        new Uri(Path.GetFullPath(TextToWrite)),
                        BitmapCreateOptions.None,
                        BitmapCacheOption.OnLoad);
                    WpfClipboard.SetImage(frame);
                }).Task.ConfigureAwait(false);

                await logger.LogInfoAsync(Name, "[БУФЕР] Изображение записано в буфер обмена.").ConfigureAwait(false);
                DebugSink?.Invoke($"[БУФЕР] ✓ Изображение '{TextToWrite}' записано в буфер обмена.");
                break;
            }

            case ClipboardImageAction.Clear:
            {
                DebugSink?.Invoke("[БУФЕР] Очистка буфера обмена...");
                await WpfApp.Current.Dispatcher.InvokeAsync(WpfClipboard.Clear).Task.ConfigureAwait(false);
                await logger.LogInfoAsync(Name, "[БУФЕР] Буфер обмена очищён.").ConfigureAwait(false);
                DebugSink?.Invoke("[БУФЕР] ✓ Буфер обмена очищён.");
                break;
            }
        }

        return true;
    }

    // ── Retry helpers: до 5 попыток при CLIPBRD_E_CANT_OPEN (0x800401D0) ──

    private static async Task<string?> SetClipboardTextWithRetryAsync(string text)
    {
        const uint CannotOpen = 0x800401D0;
        string safe = text.Length > 0 ? text : " ";

        for (int attempt = 0; attempt < 5; attempt++)
        {
            bool    written      = false;
            string? nonRetryable = null;

            await WpfApp.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    WpfClipboard.SetText(safe);
                    written = true;
                }
                catch (COMException ex) when ((uint)ex.ErrorCode == CannotOpen)
                {
                    // CLIPBRD_E_CANT_OPEN — буфер занят; выходим с written=false, nonRetryable=null
                }
                catch (Exception ex)
                {
                    nonRetryable = ex.Message;
                }
            }).Task.ConfigureAwait(false);

            if (written)               return null;
            if (nonRetryable is not null) return nonRetryable;
            // Буфер занят: ждём 50 мс и повторяем
            await Task.Delay(50).ConfigureAwait(false);
        }

        return "Буфер обмена Windows заблокирован другим процессом (CLIPBRD_E_CANT_OPEN) после 5 попыток.";
    }

    // Возвращает (text, null) при успехе, ("", message) при сбое.
    private static async Task<(string Text, string? Error)> GetClipboardTextWithRetryAsync()
    {
        const uint CannotOpen = 0x800401D0;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            string? text         = null;
            string? nonRetryable = null;

            await WpfApp.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    text = WpfClipboard.ContainsText() ? WpfClipboard.GetText() : string.Empty;
                }
                catch (COMException ex) when ((uint)ex.ErrorCode == CannotOpen)
                {
                    // CLIPBRD_E_CANT_OPEN — буфер занят; text остаётся null → повтор
                }
                catch (Exception ex)
                {
                    nonRetryable = ex.Message;
                }
            }).Task.ConfigureAwait(false);

            if (text is not null)          return (text, null);
            if (nonRetryable is not null)  return (string.Empty, nonRetryable);
            // Буфер занят: ждём 50 мс и повторяем
            await Task.Delay(50).ConfigureAwait(false);
        }

        return (string.Empty,
            "Буфер обмена Windows заблокирован другим процессом (CLIPBRD_E_CANT_OPEN) после 5 попыток.");
    }
}
