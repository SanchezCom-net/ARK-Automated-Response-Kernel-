using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using WpfApp = System.Windows.Application;
using NAudio.Wave;
using KokoroSharp;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Services;
using ARK.UI.Resources;
using ARK.UI.Views;

namespace ARK.UI.ViewModels;

public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IConfigService           _configService;
    private readonly INetworkService          _networkService;
    private readonly ISpeechTriggerService    _speechService;
    private readonly ILogService              _logService;
    private readonly IOllamaBridgeService     _ollamaService;
    private readonly ISpeechSynthesisService  _synthesisService;

    private string _selectedMicrophone  = string.Empty;
    private bool             _isTurboModelSelected;
    private SpeechEngineMode _selectedSpeechEngine   = SpeechEngineMode.Auto;
    private string           _selectedSpeechLanguage = "ru";
    private double           _vadThreshold           = 0.02;
    private bool   _isSpeechEnabled;
    private bool   _useWakeWordGatekeeper = true;
    private string _activationNames  = "Виктория, Викторию, Виктории";
    private string _aiAssistantNames = "Аркаша, Аркадий";
    private bool   _isModelLoaded;
    private string _modelStatusText     = string.Empty;
    private string _ollamaApiUrl        = "http://localhost:11434";
    private string _selectedOllamaModel = "qwen2.5-coder:7b";
    private string _webSocketUrl        = "ws://localhost:8080";
    private bool   _isNetworkConnected;
    private string   _networkStatusText     = string.Empty;
    private double   _microphoneLevel;
    private DateTime _lastUiUpdate          = DateTime.MinValue;
    private bool     _isOllamaAvailable     = true;
    private bool     _isAiEnabled           = true;
    private int      _loadId;
    private CancellationTokenSource? _urlDebounce;
    private CancellationTokenSource? _retryCts;

    // ── TTS ────────────────────────────────────────────────────────────────
    private TtsMode _selectedTtsMode  = TtsMode.Disabled;
    private string  _selectedTtsVoice = string.Empty;
    private double  _ttsSpeed         = 1.0;
    private double  _ttsVolume        = 0.8;
    private bool    _isPreviewPlaying;
    private bool    _showAiSubtitles  = true;

    public static readonly TtsMode[]         AllTtsModes      = Enum.GetValues<TtsMode>();
    public static readonly SpeechEngineMode[] AllSpeechEngines = Enum.GetValues<SpeechEngineMode>();
    public static readonly string[]           SpeechLanguages  = ["ru", "en"];

    private static readonly HttpClient _tagsHttpClient =
        new() { Timeout = TimeSpan.FromSeconds(2) };

    public ObservableCollection<string> Microphones     { get; } = [];
    public ObservableCollection<string> OllamaModels    { get; } = [];
    public ObservableCollection<string> AvailableVoices { get; } = [];

    public string SelectedMicrophone
    {
        get => _selectedMicrophone;
        set => SetProperty(ref _selectedMicrophone, value);
    }

    public bool IsTurboModelSelected
    {
        get => _isTurboModelSelected;
        set
        {
            if (!SetProperty(ref _isTurboModelSelected, value)) return;
            OnPropertyChanged(nameof(IsBaseModelSelected));
        }
    }

    public bool IsBaseModelSelected
    {
        get => !_isTurboModelSelected;
        set { if (value && _isTurboModelSelected) IsTurboModelSelected = false; }
    }

    public SpeechEngineMode SelectedSpeechEngine
    {
        get => _selectedSpeechEngine;
        set
        {
            if (!SetProperty(ref _selectedSpeechEngine, value)) return;
            OnPropertyChanged(nameof(IsWhisperSelected));
        }
    }

    // True — показываем выбор Base/Turbo только для Whisper
    public bool IsWhisperSelected => _selectedSpeechEngine != SpeechEngineMode.Vosk;

    public string SelectedSpeechLanguage
    {
        get => _selectedSpeechLanguage;
        set => SetProperty(ref _selectedSpeechLanguage, value);
    }

    public double VadThreshold
    {
        get => _vadThreshold;
        set => SetProperty(ref _vadThreshold, value);
    }

    public bool IsSpeechEnabled
    {
        get => _isSpeechEnabled;
        set => SetProperty(ref _isSpeechEnabled, value);
    }

    public bool UseWakeWordGatekeeper
    {
        get => _useWakeWordGatekeeper;
        set => SetProperty(ref _useWakeWordGatekeeper, value);
    }

    public string ActivationNames
    {
        get => _activationNames;
        set
        {
            // Валидация: максимум 3 элемента через запятую; лишние отсекаются
            var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var clamped = parts.Length > 3
                ? string.Join(", ", parts.Take(3))
                : value;
            SetProperty(ref _activationNames, clamped);
        }
    }

    public string AiAssistantNames
    {
        get => _aiAssistantNames;
        set => SetProperty(ref _aiAssistantNames, value ?? string.Empty);
    }

    public bool IsModelLoaded
    {
        get => _isModelLoaded;
        private set => SetProperty(ref _isModelLoaded, value);
    }

    public string ModelStatusText
    {
        get => _modelStatusText;
        private set => SetProperty(ref _modelStatusText, value);
    }

    public string OllamaApiUrl
    {
        get => _ollamaApiUrl;
        set
        {
            if (!SetProperty(ref _ollamaApiUrl, value)) return;
            _ = DebounceLoadModelsAsync();
        }
    }

    public string SelectedOllamaModel
    {
        get => _selectedOllamaModel;
        set => SetProperty(ref _selectedOllamaModel, value);
    }

    public string WebSocketUrl
    {
        get => _webSocketUrl;
        set => SetProperty(ref _webSocketUrl, value);
    }

    public bool IsNetworkConnected
    {
        get => _isNetworkConnected;
        private set => SetProperty(ref _isNetworkConnected, value);
    }

    public string NetworkStatusText
    {
        get => _networkStatusText;
        private set => SetProperty(ref _networkStatusText, value);
    }

    // Уровень сигнала микрофона [0.0–1.0] — обновляется из потока NAudio через Dispatcher
    public double MicrophoneLevel
    {
        get => _microphoneLevel;
        private set
        {
            if (!SetProperty(ref _microphoneLevel, value)) return;
            OnPropertyChanged(nameof(IsMicrophonePeaking));
        }
    }

    // True при пиковых значениях (>0.8) — DataTrigger в XAML переключает цвет индикатора
    public bool IsMicrophonePeaking => _microphoneLevel > 0.8;

    public bool IsOllamaAvailable
    {
        get => _isOllamaAvailable;
        private set => SetProperty(ref _isOllamaAvailable, value);
    }

    public bool IsAiEnabled
    {
        get => _isAiEnabled;
        set => SetProperty(ref _isAiEnabled, value);
    }

    public TtsMode SelectedTtsMode
    {
        get => _selectedTtsMode;
        set
        {
            if (!SetProperty(ref _selectedTtsMode, value)) return;
            OnPropertyChanged(nameof(IsTtsActive));
            LoadAvailableVoices();
        }
    }

    public bool IsTtsActive => _selectedTtsMode != TtsMode.Disabled;

    public string SelectedTtsVoice
    {
        get => _selectedTtsVoice;
        set => SetProperty(ref _selectedTtsVoice, value);
    }

    public double TtsSpeed
    {
        get => _ttsSpeed;
        set => SetProperty(ref _ttsSpeed, value);
    }

    public double TtsVolume
    {
        get => _ttsVolume;
        set => SetProperty(ref _ttsVolume, value);
    }

    public bool IsPreviewPlaying
    {
        get => _isPreviewPlaying;
        private set => SetProperty(ref _isPreviewPlaying, value);
    }

    public bool ShowAiSubtitles
    {
        get => _showAiSubtitles;
        set => SetProperty(ref _showAiSubtitles, value);
    }

    public ICommand RefreshMicrophonesCommand  { get; }
    public ICommand SaveSettingsCommand        { get; }
    public ICommand ReconnectNetworkCommand    { get; }
    public ICommand ResetSessionCommand        { get; }
    public ICommand RefreshOllamaModelsCommand { get; }
    public ICommand OpenAiCharacterCommand     { get; }
    public ICommand PlayVoicePreviewCommand    { get; }

    public SettingsViewModel(
        IConfigService           configService,
        INetworkService          networkService,
        ISpeechTriggerService    speechService,
        ILogService              logService,
        IOllamaBridgeService     ollamaService,
        ISpeechSynthesisService  synthesisService)
    {
        _configService    = configService;
        _networkService   = networkService;
        _speechService    = speechService;
        _logService       = logService;
        _ollamaService    = ollamaService;
        _synthesisService = synthesisService;

        var config            = configService.Current;
        _isSpeechEnabled         = config.SpeechEnabled;
        _useWakeWordGatekeeper   = config.UseWakeWordGatekeeper;
        _aiAssistantNames        = string.IsNullOrWhiteSpace(config.AiAssistantNames)
            ? "Аркаша, Аркадий"
            : config.AiAssistantNames;
        _activationNames      = string.IsNullOrWhiteSpace(config.ActivationNames)
            ? "Виктория, Викторию, Виктории"
            : config.ActivationNames;
        _vadThreshold         = config.SpeechRmsThreshold;
        _isTurboModelSelected     = config.WhisperModelPath.Contains("turbo",
            StringComparison.OrdinalIgnoreCase);
        _selectedSpeechEngine   = config.SelectedSpeechEngine;
        _selectedSpeechLanguage = string.IsNullOrWhiteSpace(config.SpeechLanguage)
            ? "ru" : config.SpeechLanguage;
        _ollamaApiUrl         = string.IsNullOrWhiteSpace(config.OllamaApiUrl)
            ? "http://localhost:11434" : config.OllamaApiUrl;
        _selectedOllamaModel  = string.IsNullOrWhiteSpace(config.OllamaModelName)
            ? "qwen2.5-coder:7b" : config.OllamaModelName;
        _webSocketUrl         = !string.IsNullOrWhiteSpace(config.WebSocketUrl)
            ? config.WebSocketUrl : "ws://localhost:8080";
        _isNetworkConnected   = networkService.IsConnected;
        _networkStatusText    = _isNetworkConnected ? Strings.Net_Connected : Strings.Net_Disconnected;
        _isModelLoaded        = speechService.IsRunning;
        _modelStatusText      = _isModelLoaded ? Strings.AiSettings_StatusLoaded : Strings.AiSettings_StatusNotLoaded;
        _isAiEnabled           = config.IsAiEnabled;
        _selectedTtsMode       = config.SelectedTtsMode;
        _selectedTtsVoice      = config.SelectedTtsVoice;
        _ttsSpeed              = config.TtsSpeed > 0 ? config.TtsSpeed : 1.0;
        _ttsVolume             = config.TtsVolume is >= 0.0f and <= 1.0f ? config.TtsVolume : 0.8;
        _showAiSubtitles       = config.ShowAiSubtitles;

        LoadMicrophones();
        LoadAvailableVoices();

        networkService.ConnectionStatusChanged += OnConnectionStatusChanged;
        LocalizationService.CultureChanged     += OnCultureChanged;
        speechService.LevelUpdated             += OnLevelUpdated;

        // Запускаем лёгкий мониторинг для VU-Meter если речь ещё не запущена
        if (!speechService.IsRunning)
            _ = speechService.StartMonitoringAsync();

        RefreshMicrophonesCommand  = new RelayCommand(_ => LoadMicrophones());
        SaveSettingsCommand        = new AsyncRelayCommand(OnSaveSettingsAsync);
        ReconnectNetworkCommand    = new AsyncRelayCommand(OnReconnectAsync);
        ResetSessionCommand        = new RelayCommand(_ =>
        {
            _ollamaService.ResetSession();
            _ = _logService.LogInfoAsync("SettingsVM",
                "[ИИ] Сессия диалога успешно сброшена. Память ассистента очищена.");
        });
        RefreshOllamaModelsCommand = new AsyncRelayCommand(_ => LoadOllamaModelsAsync());
        OpenAiCharacterCommand     = new RelayCommand(_ => OpenAiCharacterDialog());
        PlayVoicePreviewCommand    = new AsyncRelayCommand(OnPlayVoicePreviewAsync);

        _ = LoadOllamaModelsAsync();
    }

    // ── Динамическая загрузка моделей Ollama ──────────────────────────────────

    private async Task DebounceLoadModelsAsync()
    {
        _urlDebounce?.Cancel();
        _urlDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _urlDebounce = cts;
        try
        {
            await Task.Delay(700, cts.Token).ConfigureAwait(false);
            if (!cts.IsCancellationRequested)
                await LoadOllamaModelsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public async Task LoadOllamaModelsAsync()
    {
        // Сбрасываем авто-опрос — выполняем явную проверку
        _retryCts?.Cancel();
        _retryCts?.Dispose();
        _retryCts = null;

        var id = System.Threading.Interlocked.Increment(ref _loadId);
        var savedModel = _configService.Current.OllamaModelName;

        List<string> names;
        try
        {
            var url = $"{_ollamaApiUrl.TrimEnd('/')}/api/tags";
            using var response = await _tagsHttpClient.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            names = doc.RootElement.GetProperty("models")
                .EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? string.Empty)
                .Where(n => n.Length > 0)
                .ToList();
        }
        catch
        {
            names = [];
        }

        if (id != System.Threading.Volatile.Read(ref _loadId)) return;

        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            OllamaModels.Clear();
            if (names.Count == 0)
            {
                OllamaModels.Add(Strings.AiSettings_OllamaOffline);
                SelectedOllamaModel = Strings.AiSettings_OllamaOffline;
                IsOllamaAvailable   = false;
            }
            else
            {
                foreach (var n in names) OllamaModels.Add(n);
                SelectedOllamaModel = names.Contains(savedModel) ? savedModel : names[0];
                IsOllamaAvailable   = true;
            }
        });

        // Ollama недоступна — запускаем тихий авто-опрос каждые 3 сек
        if (names.Count == 0)
        {
            var cts = new CancellationTokenSource();
            _retryCts = cts;
            _ = OllamaRetryLoopAsync(cts.Token);
        }
    }

    // Фоновый опрос: проверяет Ollama каждые 3 сек до первого успешного ответа
    private async Task OllamaRetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try   { await Task.Delay(3000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;

            var savedModel = _configService.Current.OllamaModelName;
            List<string> names;
            try
            {
                var url = $"{_ollamaApiUrl.TrimEnd('/')}/api/tags";
                using var response = await _tagsHttpClient.GetAsync(url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                names = doc.RootElement.GetProperty("models")
                    .EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString() ?? string.Empty)
                    .Where(n => n.Length > 0)
                    .ToList();
            }
            catch (OperationCanceledException) { return; }
            catch { names = []; }

            if (names.Count > 0)
            {
                WpfApp.Current?.Dispatcher.Invoke(() =>
                {
                    OllamaModels.Clear();
                    foreach (var n in names) OllamaModels.Add(n);
                    SelectedOllamaModel = names.Contains(savedModel) ? savedModel : names[0];
                    IsOllamaAvailable   = true;
                });
                return; // Ollama появилась — завершаем цикл
            }
        }
    }

    // ── Загрузка устройств захвата ─────────────────────────────────────────────

    private void LoadMicrophones()
    {
        Microphones.Clear();
        var count = WaveIn.DeviceCount;
        for (var i = 0; i < count; i++)
            Microphones.Add(WaveIn.GetCapabilities(i).ProductName);

        var device = _configService.Current.SpeechDeviceNumber;
        SelectedMicrophone = device < Microphones.Count && Microphones.Count > 0
            ? Microphones[device]
            : Microphones.Count > 0 ? Microphones[0] : string.Empty;
    }

    // ── Сканирование голосов TTS ───────────────────────────────────────────────

    private const string KokoroDownloadRequired = "[ТРЕБУЕТСЯ СКАЧАТЬ МОДЕЛИ KOKORO]";

    private static readonly string KokoroOnnxPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "TTS", "Kokoro", "kokoro-v1.0.onnx");

    private void LoadAvailableVoices()
    {
        AvailableVoices.Clear();

        if (_selectedTtsMode == TtsMode.Kokoro)
        {
            if (!File.Exists(KokoroOnnxPath))
            {
                AvailableVoices.Add(KokoroDownloadRequired);
            }
            else
            {
                try
                {
                    if (KokoroVoiceManager.Voices.Count == 0)
                        KokoroVoiceManager.LoadVoicesFromPath();
                    foreach (var v in KokoroVoiceManager.Voices.OrderBy(v => v.Name))
                        AvailableVoices.Add(v.Name);
                }
                catch
                {
                    AvailableVoices.Add(Strings.AiSettings_TtsNoVoices);
                }
            }
        }
        else
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "TTS", "Piper");
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.onnx").OrderBy(x => x))
                    AvailableVoices.Add(Path.GetFileNameWithoutExtension(f));
            }
        }

        if (AvailableVoices.Count == 0)
            AvailableVoices.Add(Strings.AiSettings_TtsNoVoices);

        SelectedTtsVoice = AvailableVoices.Contains(_selectedTtsVoice)
            ? _selectedTtsVoice
            : AvailableVoices[0];
    }

    private async Task OnPlayVoicePreviewAsync(object? _)
    {
        if (IsPreviewPlaying)
        {
            _synthesisService.Stop();
            IsPreviewPlaying = false;
            return;
        }

        if (SelectedTtsMode == TtsMode.Disabled) return;
        if (string.IsNullOrEmpty(SelectedTtsVoice)
            || SelectedTtsVoice == Strings.AiSettings_TtsNoVoices
            || SelectedTtsVoice == KokoroDownloadRequired)
        {
            if (SelectedTtsMode == TtsMode.Kokoro)
                _ = _logService.LogWarningAsync("SettingsVM",
                    "[KOKORO] Для работы скачайте файл и положите его в Models/TTS/Kokoro/:\n" +
                    "  Модель: https://huggingface.co/hexgrad/Kokoro-82M/resolve/main/kokoro-v1.0.onnx\n" +
                    "  Голоса встроены в пакет KokoroSharp — отдельного скачивания не требуется.");
            return;
        }

        string modelPath;
        if (SelectedTtsMode == TtsMode.Kokoro)
        {
            modelPath = SelectedTtsVoice + ".bin"; // воспринимается SpeechSynthesisService как имя голоса
        }
        else
        {
            modelPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Models", "TTS", "Piper",
                SelectedTtsVoice + ".onnx");
            if (!File.Exists(modelPath)) return;
        }

        IsPreviewPlaying = true;
        try
        {
            await _synthesisService.SpeakAsync(
                "Привет! Настройки синтезатора речи применены успешно. Как тебе мой новый голос?",
                modelPath, TtsSpeed, TtsVolume).ConfigureAwait(false);
        }
        finally
        {
            IsPreviewPlaying = false;
        }
    }

    // ── Команды ───────────────────────────────────────────────────────────────

    private async Task OnSaveSettingsAsync(object? _)
    {
        if (!ValidateActivationNames(out var validationError))
        {
            _ = _logService.LogErrorAsync("SettingsVM",
                $"[ВАЛИДАЦИЯ] Сохранение отменено: {validationError}");
            System.Windows.MessageBox.Show(
                validationError,
                "Конфликт имён активации",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var config = _configService.Current;
        config.SpeechEnabled            = IsSpeechEnabled;
        config.UseWakeWordGatekeeper    = UseWakeWordGatekeeper;
        config.ActivationNames       = string.IsNullOrWhiteSpace(ActivationNames)
            ? "Виктория, Викторию, Виктории"
            : ActivationNames;
        config.AiAssistantNames      = string.IsNullOrWhiteSpace(AiAssistantNames)
            ? "Аркаша, Аркадий"
            : AiAssistantNames;
        config.SpeechRmsThreshold    = (float)VadThreshold;
        config.WhisperModelPath      = IsTurboModelSelected
            ? SpeechTriggerService.TurboModelPath
            : SpeechTriggerService.BaseModelPath;
        config.SpeechDeviceNumber    = Math.Max(0, Microphones.IndexOf(SelectedMicrophone));
        config.SelectedSpeechEngine  = SelectedSpeechEngine;
        config.SpeechLanguage        = SelectedSpeechLanguage;
        config.OllamaApiUrl          = OllamaApiUrl;
        if (IsOllamaAvailable)
        {
            config.OllamaModelName   = SelectedOllamaModel;

            // Диагностика зрения: текстовая модель молча проигнорирует поле images
            if (!OllamaModelCapabilities.IsLikelyMultimodal(SelectedOllamaModel))
                _ = _logService.LogWarningAsync("SettingsVM",
                    $"[VISION] Выбранная модель '{SelectedOllamaModel}' не поддерживает изображения. " +
                    "Для работы компьютерного зрения выберите мультимодальную модель " +
                    "(например, llava, qwen2-vl или llama3.2-vision).");
        }
        config.WebSocketUrl          = WebSocketUrl;
        config.IsAiEnabled           = IsAiEnabled;
        config.SelectedTtsMode       = SelectedTtsMode;
        if (SelectedTtsVoice != Strings.AiSettings_TtsNoVoices
            && SelectedTtsVoice != KokoroDownloadRequired)
            config.SelectedTtsVoice  = SelectedTtsVoice;
        config.TtsSpeed              = (float)TtsSpeed;
        config.TtsVolume             = (float)TtsVolume;
        config.ShowAiSubtitles       = ShowAiSubtitles;

        await _configService.SaveAsync().ConfigureAwait(false);

        // Применяем движок/язык без перезапуска приложения
        if (IsSpeechEnabled)
        {
            await _speechService.SwitchEngineAsync(
                SelectedSpeechEngine, SelectedSpeechLanguage).ConfigureAwait(false);
        }
        else
        {
            await _speechService.StopAsync().ConfigureAwait(false);
            // Возобновляем мониторинг — VU-Meter остаётся рабочим для подбора порога VAD
            await _speechService.StartMonitoringAsync().ConfigureAwait(false);
            MicrophoneLevel = 0;
        }

        IsModelLoaded   = _speechService.IsRunning;
        ModelStatusText = IsModelLoaded ? Strings.AiSettings_StatusLoaded : Strings.AiSettings_StatusNotLoaded;
    }

    private void OpenAiCharacterDialog()
    {
        var config = _configService.Current;
        var dialog = new AiCharacterDialog(config.AiAssistantNames, config.AiSystemPrompt);
        if (dialog.ShowDialog() != true) return;

        if (dialog.ResultName is not null)
        {
            config.AiAssistantNames = dialog.ResultName;
            AiAssistantNames        = dialog.ResultName;
        }
        if (dialog.ResultPrompt is not null) config.AiSystemPrompt = dialog.ResultPrompt;
        _ = _configService.SaveAsync();
        _ = _logService.LogInfoAsync("SettingsVM", "[ИИ] Характер ассистента обновлён.");
    }

    private bool ValidateActivationNames(out string? errorMessage)
    {
        errorMessage = null;

        var gatekeeperNames = (ActivationNames ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant())
            .Where(n => n.Length > 0)
            .ToHashSet();

        var aiNames = (AiAssistantNames ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant())
            .Where(n => n.Length > 0)
            .ToList();

        foreach (var name in aiNames)
        {
            if (!gatekeeperNames.Contains(name)) continue;
            errorMessage =
                $"Конфликт имён! Имя «{name}» используется одновременно как имя активации макросов " +
                "и как имя ИИ-ассистента. Имена должны быть уникальными во избежание одновременного срабатывания!";
            return false;
        }

        return true;
    }

    private async Task OnReconnectAsync(object? _)
    {
        if (!Uri.TryCreate(_webSocketUrl, UriKind.Absolute, out var uri)) return;
        await _networkService.ConnectAsync(uri).ConfigureAwait(false);
    }

    private void OnConnectionStatusChanged(object? sender, bool connected)
    {
        IsNetworkConnected = connected;
        NetworkStatusText  = connected ? Strings.Net_Connected : Strings.Net_Disconnected;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        NetworkStatusText = _isNetworkConnected ? Strings.Net_Connected : Strings.Net_Disconnected;
        ModelStatusText   = _isModelLoaded ? Strings.AiSettings_StatusLoaded : Strings.AiSettings_StatusNotLoaded;
    }

    // Вызывается из потока NAudio (~30 раз/сек при BufferMilliseconds=33)
    private void OnLevelUpdated(double rms)
    {
        // Дросселирование до ~30 FPS — отсекаем лишние Dispatcher.InvokeAsync вызовы
        var now = DateTime.UtcNow;
        if ((now - _lastUiUpdate).TotalMilliseconds < 33) return;
        _lastUiUpdate = now;

        WpfApp.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Усиливаем сигнал ×5 для наглядности шкалы; пик — мгновенный, спад — сглаженный
            double target = Math.Clamp(rms * 5.0, 0.0, 1.0);
            MicrophoneLevel = target > _microphoneLevel
                ? target
                : _microphoneLevel * 0.8 + target * 0.2;
        });
    }

    public void Dispose()
    {
        _speechService.LevelUpdated             -= OnLevelUpdated;
        _networkService.ConnectionStatusChanged -= OnConnectionStatusChanged;
        LocalizationService.CultureChanged      -= OnCultureChanged;

        var urlDebounce = Interlocked.Exchange(ref _urlDebounce, null);
        if (urlDebounce != null)
        {
            try   { if (!urlDebounce.IsCancellationRequested) urlDebounce.Cancel(); }
            catch (ObjectDisposedException) { }
            finally { urlDebounce.Dispose(); }
        }

        var retryCts = Interlocked.Exchange(ref _retryCts, null);
        if (retryCts != null)
        {
            try   { if (!retryCts.IsCancellationRequested) retryCts.Cancel(); }
            catch (ObjectDisposedException) { }
            finally { retryCts.Dispose(); }
        }

        _synthesisService.Stop();

        if (!_speechService.IsRunning)
            _ = _speechService.StopMonitoringAsync();
    }
}
