# PROJECT_ARCHITECTURE.md — ARK (Automated Response Kernel)
> Живой технический паспорт проекта. Синхронизируется при каждом изменении кода.
> Дата последнего обновления: 2026-06-17 | Сборка: Debug | Ошибок: 0 | Предупреждений: 0 | Задача: TriggerRootNode — единственная точка входа графа макроса

---

## 1. ОБЩИЕ СВЕДЕНИЯ О СТЕКЕ

| Параметр | Значение |
|---|---|
| Runtime | .NET 9 / `net9.0-windows10.0.19041.0` |
| Язык | C# 13 (file-scoped namespaces, pattern matching, `[LibraryImport]`) |
| UI Framework | WPF (`UseWPF=true`, `UseWindowsForms=true` — для System.Windows.Forms.NotifyIcon) |
| Target Platform | Windows 10/11 x64 (минимум 19041) |
| Nullable | `enable` |
| ImplicitUsings | `enable` |
| AllowUnsafeBlocks | `true` |
| Entrypoint | `ARK.UI.exe` (WinExe) |

### NuGet-зависимости

| Пакет | Версия | Назначение |
|---|---|---|
| `Microsoft.Extensions.DependencyInjection` | 10.0.8 | DI-контейнер |
| `System.Security.Cryptography.ProtectedData` | 10.0.8 | DPAPI шифрование (VaultService) |
| `NAudio` | 2.2.1 | Захват микрофона (WaveInEvent), воспроизведение (WaveOutEvent) |
| `Whisper.net` | 1.9.1 | STT-транскрипция (whisper.cpp биндинги) |
| `Whisper.net.Runtime` | 1.9.1 | CPU-нативные DLL |
| `Whisper.net.Runtime.Cuda12.Windows` | 1.9.1 | GPU-нативные DLL (CUDA 12.x, перезаписывает CPU) |
| `KokoroSharp` | 0.6.5 | TTS-синтез (Kokoro ONNX inference) |
| `Microsoft.ML.OnnxRuntime` | 1.22.0 | ONNX Runtime для KokoroSharp |
| `obs-websocket-dotnet` | 5.0.1 | OBS Studio WebSocket v5 клиент |
| `Vosk` | 0.3.38 | Offline STT (CPU fallback при отсутствии CUDA; требует `libvosk.dll`) |

### Нативные DLL

| DLL | Источник | Назначение |
|---|---|---|
| `rnnoise.dll` (x64) | `Tools\RNNoise\` или `Core\Input\` | RNNoise нейросетевой шумодав |
| `whisper.dll` (x64) | `runtimes\win-x64\native\` | Whisper.net CPU inference |
| `ggml-whisper.dll`, `ggml-base-whisper.dll`, `ggml-cpu-whisper.dll` (x64) | `runtimes\cuda12\win-x64\` | Whisper.net CPU DLL (Whisper.net 1.9.1 naming) |
| `ggml-cuda-whisper.dll` (x64, 538 MB) | `runtimes\cuda12\win-x64\` (Whisper.net.Runtime.Cuda12.Windows) | Whisper.net GPU inference (CUDA 12.x) |
| `libvosk.dll` (x64) | Скачивается отдельно → рядом с exe | Vosk offline STT нативная библиотека |

---

## 2. ПОФАЙЛОВАЯ КАРТА ПРОЕКТА (DIRECTORY TREE)

```
ARK.UI/
├── App.xaml                          # Слияние ResourceDictionary всех Themes/
├── App.xaml.cs                       # Точка входа, DI-регистрация, глобальные перехватчики исключений
├── App.TrayHandlers.cs               # Обработчики контекстного меню трея (partial App)
├── AssemblyInfo.cs
├── MainWindow.xaml / .xaml.cs        # Главное окно (shell); табы → Views/
│
├── Converters/
│   ├── EnumToLocalizedDescriptionConverter.cs  # Enum → локализованная строка через Strings.resx
│   ├── HotKeyTextConverter.cs                   # Key+ModifierKeys → строка "Ctrl+Shift+V"
│   ├── StringResourceConverter.cs               # Ключ ресурса → строка через Strings.ResourceManager
│   └── VisualNodeCardSelector.cs                # DataTemplateSelector для карточек нод на холсте
│
├── Core/
│   ├── Action/
│   │   └── ActionService.cs          # IActionService: SendInput + mouse via user32.dll
│   │
│   ├── Audio/
│   │   └── RnNoiseDenoiser.cs        # P/Invoke rnnoise.dll; Zero-Alloc FIFO; 16→48→16 кГц
│   │
│   ├── Input/
│   │   ├── InputService.cs           # IInputService: глобальные хуки клавиатуры/мыши (SetWindowsHookEx)
│   │   ├── KeyHookEventArgs.cs
│   │   └── MouseButtonHookEventArgs.cs
│   │
│   ├── Interfaces/                   # Контракты всех singleton-сервисов (см. раздел 3)
│   │   ├── IActionService.cs
│   │   ├── IConfigService.cs         # + event System.Action? ConfigSaved
│   │   ├── IHardwareAccelerator.cs   # [NEW] IsGpuAccelerationAvailable; IsCudaAvailable; IsDirectMlAvailable; IsRocmAvailable; PrimaryGpuName; RefreshAsync
│   │   ├── IInputService.cs
│   │   ├── ILogService.cs
│   │   ├── IMacroScheduler.cs
│   │   ├── IProcessWatcher.cs        # [NEW] RunningProcessNames; ProcessStarted/Exited; Start/Stop
│   │   ├── IStartupOrchestrator.cs   # [NEW] IsReady; ReadyStateChanged; PhaseCompleted; RunAsync
│   │   ├── IQueueService.cs          # CRUD регионов/папок очереди; LoadAsync/SaveAsync queues.json
│   │   ├── IModelManager.cs          # Оркестратор моделей (Whisper/Vosk + watchdog)
│   │   ├── IModelWrapper.cs          # Обёртка движка распознавания (IAsyncDisposable)
│   │   ├── INetworkService.cs
│   │   ├── INodeEngine.cs
│   │   ├── IObsService.cs
│   │   ├── IOllamaBridgeService.cs
│   │   ├── IOverlayService.cs
│   │   ├── IProfileService.cs
│   │   ├── ISpeechSynthesisService.cs
│   │   ├── ISpeechTriggerService.cs
│   │   ├── ITwitchService.cs
│   │   ├── IUiAutomationService.cs
│   │   ├── IVaultService.cs
│   │   ├── IVisionService.cs
│   │   └── IWindowTrackerService.cs
│   │
│   ├── Models/
│   │   ├── ActiveWindowInfo.cs       # HWND + ProcessName + Title
│   │   ├── AgentCapabilities.cs      # Флаги возможностей LLM-агента (tools, vision, etc.)
│   │   ├── AppConfig.cs              # config.json: + SpeechLanguage ("ru"/"en"), VoskModelPath
│   │   ├── AppProfile.cs             # Профиль автоматизации (макросы + регионы)
│   │   ├── AppSettings.cs            # appsettings.json: статические настройки среды
│   │   ├── ChatMessage.cs            # Сообщение LLM-чата (role + content)
│   │   ├── ConnectorStep.cs          # Шаг Logic_SequenceNode (StepId + Name)
│   │   ├── DataPacket.cs             # Типизированный пакет данных серебряного провода
│   │   ├── ExecutionContext.cs       # MacroExecutionContext — общая шина данных
│   │   ├── LogEntry.cs               # NDJSON-запись лога (record)
│   │   ├── LogLevel.cs               # Info / Warning / Error
│   │   ├── MacroEntry.cs             # Запись макроса; +RegionId: Guid? (ссылка на QueueRegion)
│   │   ├── NodeCategory.cs           # Enum: Input / Output / Logic / OBS / Win / Vision / Web
│   │   ├── NodeDropPayload.cs        # Drag-and-drop payload нод из панели
│   │   ├── NodeState.cs              # Pending / Executing / Success / Failed
│   │   ├── NodeTemplate.cs           # Шаблон для панели инструментов (Name + discriminator)
│   │   ├── ObsRecordActionType.cs    # Start / Stop / Toggle
│   │   ├── ProfileRegion.cs          # Именованная область экрана для Vision
│   │   ├── SearchArea.cs             # Прямоугольник поиска для ColorSearch/Template
│   │   ├── TtsMode.cs                # Kokoro / Piper
│   │   ├── TwitchMessageEventArgs.cs # Username + Message
│   │   ├── UiElementInfo.cs          # UIA: AutomationId + Name + BoundingRect
│   │   ├── VisualConnection.cs       # Провод между нодами (SourceId → TargetId + флаги)
│   │   ├── VisualConnectionLine.cs   # WPF-отрисовка провода (Line / BezierSegment)
│   │   ├── VisualFolder.cs           # Папка в MacroExplorer
│   │   ├── ModelType.cs              # [NEW] enum { None, Whisper, Vosk }
│   │   ├── WhisperWorkerConfig.cs    # [NEW] sealed record; JSON-сериализация для --config-b64; model_path/language/use_gpu/precision/model_type/gpu_device
│   │   ├── AppSettings.cs            # + GpuSettings секция: ForceGpuInitialization (bool, default=false)
│   │   ├── QueueRegion.cs            # [NEW] Регион очереди: Id, Name, ExecutionMode, Folders
│   │   ├── QueueFolder.cs            # [NEW] Папка внутри региона: Id, Name, SubFolders (глубина=1)
│   │   ├── QueueStore.cs             # [NEW] Корневой контейнер queues.json: ObservableCollection<QueueRegion>
│   │   └── VisualNode.cs             # Карточка ноды на холсте (X, Y, LogicalNode)
│   │
│   ├── Network/
│   │   ├── NetworkCommand.cs         # Команда сетевого протокола ARK
│   │   ├── NetworkCommandDispatcher.cs
│   │   └── NetworkService.cs         # INetworkService: TCP-сервер для внешних клиентов
│   │
│   ├── Nodes/
│   │   ├── BaseNode.cs               # Абстрактный базовый класс; JsonPolymorphic; TryApplyContextInput<T>
│   │   ├── Clipboard_NodeEnums.cs    # ClipboardActionType, ClipboardDataType
│   │   ├── ClipboardNode.cs          # Чтение/запись буфера обмена; DataPacket; 5×50мс retry
│   │   ├── ColorSearchNode.cs        # Поиск цвета на экране через GetPixel (gdi32)
│   │   ├── DelayNode.cs              # Пауза (мс); DefaultDataInputPropertyName = DelayMilliseconds
│   │   ├── HotkeyTriggerNode.cs      # Триггер по горячей клавише (глобальный хук)
│   │   ├── KeyPressNode.cs           # Нажатие клавиши через SendInput
│   │   ├── Logic_BranchNode.cs       # Условное ветвление (CompareNumber, Contains, etc.)
│   │   ├── Logic_CounterNode.cs      # Счётчик с инкрементом/декрементом
│   │   ├── Logic_NodeEnums.cs        # BranchCondition, CounterMode
│   │   ├── Logic_QueueBlockNode.cs   # Барьер синхронизации параллельных веток
│   │   ├── Logic_SequenceNode.cs     # Последовательный выполнитель шагов
│   │   ├── MouseActionNode.cs        # Клик/движение/скролл мыши; CoordinatePicker
│   │   ├── MouseClickNode.cs         # Простой клик по координатам
│   │   ├── NetworkStatusNode.cs      # Проверка сетевого соединения (INetworkService)
│   │   ├── NodeDragAdorner.cs        # WPF Adorner для drag-and-drop нод
│   │   ├── ObsRecordControlNode.cs   # Управление записью OBS (Start/Stop/Toggle)
│   │   ├── ObsSceneMode.cs           # Enum: Set / Toggle
│   │   ├── ObsSetSceneNode.cs        # Смена сцены OBS (legacy → заменён OBS_SceneManagerNode)
│   │   ├── ObsToggleMuteNode.cs      # Mute/Unmute источника OBS (legacy)
│   │   ├── OverlayTextNode.cs        # Отображение текста в полупрозрачном оверлее
│   │   ├── RunProcessNode.cs         # Запуск процесса/файла; guard clause (exe/bat/cmd/lnk)
│   │   ├── SendInputNode.cs          # Отправка ввода (ParseAndApplyHotkeyString)
│   │   ├── SpeechTriggerNode.cs      # Триггер по голосовой команде; Sanitize + IsPhraseMatch
│   │   ├── TemplateMatchNode.cs      # Поиск изображения на экране (Vision)
│   │   ├── TextConditionNode.cs      # Текстовое условие (Between / ContainsWholeWord / etc.)
│   │   ├── TextWriteNode.cs          # Запись текста в файл
│   │   ├── Vision_OcrNode.cs         # OCR распознавание изображения (IVisionService)
│   │   ├── Wait_SmartDelayNode.cs    # Умная задержка: UntilTime / UntilWindowAppears / etc.
│   │   ├── Web_RequestNode.cs        # HTTP-запрос (HttpClient); DefaultDataInputPropertyName = Url
│   │   ├── Win_AudioDeviceNode.cs    # Управление аудиоустройством Windows
│   │   ├── Win_NodeEnums.cs          # PowerAction, WindowAction, ProcessAction
│   │   ├── Win_PowerShellNode.cs     # Выполнение PowerShell/CMD; Base64+UTF-8; ConsoleOutput
│   │   ├── Win_ProcessManagerNode.cs # Управление процессами Windows (список через PhraseItem)
│   │   ├── Win_SpeakTextNode.cs      # TTS озвучка через ISpeechSynthesisService
│   │   ├── Win_SystemPowerNode.cs    # Питание ОС (Lock / Sleep / Shutdown / Reboot)
│   │   ├── Win_ExclusiveGateNode.cs  # Монопольный режим ("exclusive_gate"); УМНАЯ ЛОГИКА
│   │   ├── Win_BypassQueueNode.cs    # [NEW] VIP-обход очереди ("bypass_queue"); ForceImmediate + BlockOthersOnExecution
│   │   ├── TriggerRootNode.cs        # [NEW] Единственная точка входа графа ("trigger_root"); IsRemovable=false; pass-through
│   │   ├── Win_TranslateNode.cs      # Перевод текста (Ollama / внешний API)
│   │   └── Win_WindowManagerNode.cs  # Управление окнами (список через PhraseItem)
│   │   └── OBS/
│   │       ├── IObsCascadeNode.cs                  # Интерфейс каскадного выбора OBS-параметров
│   │       ├── OBS_AudioManagerNode.cs             # Управление аудио OBS (Volume/Mute/Toggle)
│   │       ├── OBS_DynamicContentManagerNode.cs    # Текст/файл/медиа-источники OBS
│   │       ├── OBS_NodeEnums.cs                    # OBS-специфичные enum'ы
│   │       ├── OBS_SceneManagerNode.cs             # Сцены OBS (Set/Toggle + каскад)
│   │       ├── OBS_SourceVisibilityManagerNode.cs  # Видимость источников и фильтров OBS
│   │       └── OBS_StreamAndRecordManagerNode.cs   # Стриминг, запись, буфер повторов OBS
│   │
│   ├── Services/
│   │   ├── AgentCommandFilter.cs       # Фильтрация команд LLM-агента
│   │   ├── ConfigService.cs            # IConfigService: config.json + DPAPI + event ConfigSaved
│   │   ├── DiagnosticsService.cs       # Сбор системной диагностики (CPU, RAM, etc.)
│   │   ├── HyperlinkClickBehavior.cs   # Attached behavior: открыть URL из TextBlock в браузере
│   │   ├── InverseBoolToVisibilityConverter.cs
│   │   ├── JsonLogService.cs           # ILogService: NDJSON лог + экспоненциальное подавление
│   │   ├── LocalizationService.cs      # static: CultureChanged event
│   │   ├── LogHighlightConverter.cs    # Раскраска строк лога по уровню
│   │   ├── LogInlinesBehavior.cs       # Attached behavior: рендер лога с URL-гиперссылками
│   │   ├── LogsDocumentBehavior.cs     # Attached behavior: синхронизация скролла лог-терминала
│   │   ├── MacroScheduler.cs           # IMacroScheduler: планировщик; +IProcessWatcher(ProcessStarted авто-активация); двухпутевой поиск
│   │   ├── ProcessWatcher.cs           # [NEW] IProcessWatcher: poll каждые 2 сек, diff-события ProcessStarted/Exited
│   │   ├── StartupOrchestrator.cs      # IStartupOrchestrator: фазы GPU→Speech→MacroIndex→Processes; Phase_GpuAsync: CudaDiagnostics + WaitForCudaAsync(4,1000ms)
│   │   ├── CudaDiagnostics.cs          # nvidia-smi аудит; LocateCudaRuntime (AppDir→PATH→NVIDIA Toolkit C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v*\bin→System32/SysWOW64); CudaProbeResult.CudaRuntimeDllPath+SearchedPaths[]; LogError с полной инструкцией при отсутствии cudart64_*.dll
│   │   ├── QueueService.cs             # IQueueService: queues.json CRUD; SemaphoreSlim(1,1); static FindFolder
│   │   ├── MarqueeBehavior.cs          # Attached behavior: прокрутка TextBlock при overflow
│   │   ├── NodeEngine.cs               # INodeEngine: Fork/Join граф выполнения нод
│   │   ├── ObsService.cs               # IObsService: WebSocket клиент obs-websocket v5
│   │   ├── ObsService.Query.cs         # IObsService partial: каскадные запросы (scenes/inputs/filters)
│   │   ├── OllamaBridgeService.cs      # IOllamaBridgeService: streaming LLM через HTTP SSE
│   │   ├── OllamaModelCapabilities.cs  # Определение возможностей модели (tools, vision)
│   │   ├── OverlayService.cs           # IOverlayService: полупрозрачное окно поверх всех окон
│   │   ├── PasswordBehavior.cs         # Attached behavior: синхронизация PasswordBox ↔ ViewModel
│   │   ├── PasswordEyeIconConverter.cs # Конвертер для иконки «глазик» (показать/скрыть пароль)
│   │   ├── ProcessIconHelper.cs        # SHGetFileInfoW → BitmapSource иконка процесса
│   │   ├── ProfileService.cs           # IProfileService: загрузка/сохранение профилей (JSON)
│   │   ├── ScrollSyncBehavior.cs       # Attached behavior: синхронизация скролла TextBox+TextBlock
│   │   ├── SpeechSynthesisService.cs   # ISpeechSynthesisService: KokoroSharp + Piper TTS
│   │   ├── HardwareMonitor.cs          # static legacy: DetectCuda() (используется WhisperModelWrapper)
│   │   ├── HardwareAcceleratorService.cs # IHardwareAccelerator: ProbeDetail struct; LogDiagnosticsAsync — причина отказа каждой DLL в лог
│   │   ├── ModelManager.cs             # IModelManager: оркестратор ExternalWhisperService/Vosk; ForceGpuInitialization bypass; OnWhisperFaulted → Vosk fallback
│   │   ├── SpeechTriggerService.cs     # ISpeechTriggerService: делегирует инференс в IModelManager
│   │   ├── ExternalWhisperService.cs   # IModelWrapper(Whisper): --config-b64; OnBeforeStartAsync (GpuStartupDelayMs + GC×2 + лог); ShouldLogStderr (фильтр CUDA-мусора)
│   │   ├── ExternalVoskService.cs      # IModelWrapper(Vosk): внешний процесс VoskHost.exe через Named Pipes
│   │   ├── BaseSpeechHostedService.cs  # Абстракция: IPC-сервер, watchdog (RAM/session/idle), Faulted event
│   │   ├── BaseSpeechHostedService.cs      # [UPDATED] virtual OnBeforeStartAsync(ct) + ShouldLogStderr(line) хуки; вызов OnBeforeStartAsync перед Process.Start
│   │   ├── BaseSpeechHostedService.Process.cs # Partial: ctrl-pipe LogForwarder (log/halt/status); "halt" → _faulted + Faulted?.Invoke(); stderr фильтр через ShouldLogStderr
│   │   ├── WhisperModelWrapper.cs      # IModelWrapper(Whisper in-process, legacy): Whisper.net GPU→CPU fallback
│   │   ├── TwitchService.cs            # ITwitchService: IRC/WebSocket подключение к Twitch
│   │   ├── UiAutomationService.cs      # IUiAutomationService: Windows UI Automation
│   │   ├── UrlCursorBehavior.cs        # Attached behavior: Hand-курсор при наведении на URL
│   │   ├── VaultService.cs             # IVaultService: DPAPI ProtectedData (Entropy = "ARK-Vault")
│   │   ├── Win32Api.cs                 # P/Invoke объявления (user32, gdi32, shell32, shlwapi)
│   │   └── WindowTrackerService.cs     # IWindowTrackerService: отслеживание активного окна
│   │
│   ├── Tts/
│   │   ├── KokoroPhonemeMapper.cs      # Маппинг текста → фонемы для KokoroSharp
│   │   └── KokoroSynthesizer.cs        # (заменён KokoroSharp; файл-заглушка)
│   │
│   └── Vision/
│       └── VisionService.cs            # IVisionService: OCR + Template Matching
│
├── Resources/
│   ├── Strings.resx                    # Основной RU словарь (генерирует Strings.Designer.cs)
│   ├── Strings.en.resx                 # EN-локаль
│   └── app.ico
│
├── Themes/
│   ├── Brushes.xaml                    # Палитра Obsidian Gold (кисти, свечения)
│   ├── Buttons.xaml                    # ObsidianButtonStyle, ObsidianHelpButtonStyle
│   ├── Inputs.xaml                     # ObsidianTextBoxStyle, ObsidianComboBoxStyle, MarqueeTextBlockStyle
│   └── Scrollbars.xaml                 # Кастомный ScrollBar в Obsidian-стиле
│
├── ViewModels/
│   ├── BlueprintEditorViewModel.cs     # Холст нод: drag/drop, провода, выполнение макроса
│   ├── DashboardViewModel.cs           # +IsReady (IStartupOrchestrator.ReadyStateChanged); спиннер пока IsReady=false
│   ├── QueueViewModel.cs               # MVVM вкладки Очередь: RebuildTree → lazy factories; CRUD регионов/папок
│   ├── QueueNodeViewModels.cs          # VM-узлы дерева очереди: ленивая загрузка через SetChildFactory; EnsureChildrenLoaded при expand
│   └── (другие ViewModels для каждой View)
│
└── Views/
    ├── AddMacroToQueueDialog.xaml      # Диалог fuzzy-поиска; +VirtualizingPanel.IsVirtualizing/Recycling на ListBox
    ├── AiCharacterDialog.xaml          # Диалог настройки персонажа AI
    ├── AiSettingsControl.xaml          # Вкладка настроек Ollama/AI
    ├── BlueprintEditorControl.xaml     # Визуальный редактор нод (холст + DataTemplates всех нод)
    ├── CoordinatePickerWindow.xaml     # Прозрачное окно захвата координат мыши
    ├── DashboardWindow.xaml            # +Loading overlay (спиннер инициализации, скрывается при IsReady=true)
    ├── LogsTerminalControl.xaml        # Двухслойный лог-терминал
    ├── MacroExplorerControl.xaml       # Проводник макросов с папками (без Region-управления)
    ├── PriorityDialog.xaml             # [NEW] Диалог задания приоритета макроса в очереди (0–99)
    ├── QueueSettingsControl.xaml       # Вкладка Очередь: VM-дерево, контекстное меню, кнопки + Макрос / Приоритет
    ├── NetworkSettingsControl.xaml     # Настройки TCP-сервера
    ├── ObsSettingsControl.xaml         # Настройки OBS WebSocket
    ├── OverlayWindow.xaml              # Прозрачное поверхностное окно оверлея
    ├── PasswordDialog.xaml             # Диалог ввода пароля (Reveal PasswordBox)
    ├── RenameDialog.xaml               # Диалог переименования
    └── TwitchSettingsControl.xaml      # Настройки Twitch IRC
```

```
ARK.VoskHost/                          # Изолированный хост-процесс Vosk (VoskHost.exe)
├── ARK.VoskHost.csproj                # net9.0-windows, AssemblyName=VoskHost, Vosk 0.3.38
├── Program.cs                         # Main: args parse, pipe connect, VoskProcessor run
├── PipeTransport.cs                   # Client-side Named Pipes: ark-vosk-audio-/ark-vosk-ctrl-
├── VoskProcessor.cs                   # Vosk.VoskRecognizer цикл + VAD
└── VadMonitor.cs                      # Шумовой VAD (Pause/Resume)

ARK.Voice.Worker/                      # [NEW] Изолированный хост-процесс Whisper (WhisperHost.exe)
├── ARK.Voice.Worker.csproj            # net9.0-windows, AssemblyName=WhisperHost, Whisper.net 1.9.1
├── Program.cs                         # Main: --pipe-id + --config-b64 (Base64 JSON), --dry-run, GPU-halt exit 10
├── PipeTransport.cs                   # Client-side Named Pipes: ark-whisper-audio-/ark-whisper-ctrl-; WriteHalt()
├── WhisperWorkerConfig.cs             # [NEW] internal record; десериализуется из --config-b64; model_path/language/use_gpu/precision/model_type/gpu_device
└── WhisperPipeProcessor.cs            # WhisperFactory GPU-HALT (нет CPU fallback); LogForwarder WriteLog; BuildWavStream(short[]→WAV)
```

---

## 3. КОНТРАКТЫ ИНТЕРФЕЙСОВ И СЕРВИСОВ

### IHardwareAccelerator (`Core/Interfaces/IHardwareAccelerator.cs`)
```csharp
public interface IHardwareAccelerator
{
    bool    IsGpuAccelerationAvailable { get; }  // CUDA || DirectML || ROCm
    bool    IsCudaAvailable            { get; }  // ggml-cuda-whisper.dll loadable (Whisper.net 1.9.1)
    bool    IsDirectMlAvailable        { get; }  // DirectML.dll loadable
    bool    IsRocmAvailable            { get; }  // amdhip64.dll loadable
    string? PrimaryGpuName             { get; }  // nvidia-smi → GPU name (NVIDIA only)
    Task RefreshAsync(CancellationToken ct = default);  // Перепроверяет все флаги
    // Fast path: возвращает true немедленно при первом IsCudaAvailable=true; иначе до maxAttempts попыток с delayMs паузой
    Task<bool> WaitForCudaAsync(int maxAttempts, int delayMilliseconds, CancellationToken ct = default);
}
```
**Реализация:** `HardwareAcceleratorService` — singleton; `RefreshAsync` → `Task.Run(DoRefresh)` + `LogDiagnosticsAsync`.
- `ProbeNativeLib(fileName)` → `ProbeDetail record struct(bool Success, string DllPath, string? FailReason)`: 3 уровня диагностики — файл отсутствует / `TryLoad` вернул false (нет транзитивных зависимостей) / исключение с первой строкой StackTrace.
- **Критическое имя (Whisper.net 1.9.1):** проверяет `ggml-cuda-whisper.dll` (не `ggml-cuda.dll` — переименовано в 1.9.1).
- `LogDiagnosticsAsync`: сводная строка + детальная причина по каждой не загруженной DLL (CUDA/DirectML/ROCm/OnnxCUDA).
- `TryGetGpuName` через `nvidia-smi --query-gpu=name` (WaitForExit 2000 мс, no-shell).
- Заменяет статический `HardwareMonitor` в `ModelManager`.

---

### ILogService (`Core/Interfaces/ILogService.cs`)
```csharp
public interface ILogService
{
    string LogDirectory { get; }
    Task LogAsync(LogLevel level, string component, string message,
        Exception? exception = null, CancellationToken cancellationToken = default);
    // Default implementations:
    Task LogInfoAsync(string component, string message, CancellationToken ct = default);
    Task LogWarningAsync(string component, string message, CancellationToken ct = default);
    Task LogErrorAsync(string component, string message, Exception? exception = null, CancellationToken ct = default);
    // Экспоненциальное подавление (горячий путь: ValueTask, zero-allocation):
    ValueTask LogSuppressedAsync(string categoryKey, LogLevel level, string component,
        string message, Exception? exception = null);
    void ResetLogSuppression(string categoryKey);
    ValueTask LogErrorSuppressedAsync(string categoryKey, string component,
        string message, Exception? exception = null);
}
```
**Реализация:** `JsonLogService` — NDJSON, файл `logs/log_{yyyy-MM-dd}.json`, `SemaphoreSlim(1,1)`, `JavaScriptEncoder.Create(UnicodeRanges.All)`. Интервал подавления: 1→2→4→…→300 с.

---

### IVaultService (`Core/Interfaces/IVaultService.cs`)
```csharp
public interface IVaultService
{
    Task<string> EncryptAsync(string clearText, CancellationToken cancellationToken = default);
    Task<string> DecryptAsync(string cipherText, CancellationToken cancellationToken = default);
}
```
**Реализация:** `VaultService` — `System.Security.Cryptography.ProtectedData`.
- Алгоритм: `ProtectedData.Protect/Unprotect` с `DataProtectionScope.CurrentUser`
- Энтропия: `byte[] { 0x41,0x52,0x4B,0x2D,0x56,0x61,0x75,0x6C,0x74 }` = `"ARK-Vault"` (UTF-8)
- Вход/выход: Base64-строка

---

### IConfigService (`Core/Interfaces/IConfigService.cs`)
```csharp
public interface IConfigService
{
    // Срабатывает после каждого SaveAsync — авто-триггер ModelManager при смене языка
    event System.Action? ConfigSaved;

    AppConfig  Current     { get; }   // config.json (мутируемый)
    AppSettings AppSettings { get; }  // appsettings.json (только чтение)
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task UpdateApiKeyAsync(string rawApiKey, CancellationToken cancellationToken = default);
    Task<string> GetApiKeyAsync(CancellationToken cancellationToken = default);
    Task UpdateObsPasswordAsync(string rawPassword, CancellationToken cancellationToken = default);
    Task<string> GetObsPasswordAsync(CancellationToken cancellationToken = default);
    Task UpdateTwitchOAuthAsync(string rawToken, CancellationToken cancellationToken = default);
    Task<string> GetTwitchOAuthAsync(CancellationToken cancellationToken = default);
}
```
Все `Update*` методы шифруют через `IVaultService` перед записью в JSON.

---

### IModelWrapper (`Core/Interfaces/IModelWrapper.cs`)
```csharp
public interface IModelWrapper : IAsyncDisposable
{
    ModelType Type    { get; }
    bool      IsReady { get; }
    Task         InitializeAsync(string modelPath, string language, CancellationToken ct = default);
    Task<string> RecognizeAsync(Stream audioWav, CancellationToken ct = default);
}
```
**Реализации:**
- `WhisperModelWrapper` — `WhisperFactory` + `WhisperProcessor`, GPU→CPU fallback
- `VoskModelWrapper` — `Vosk.VoskRecognizer`, только CPU, graceful degradation при отсутствии `libvosk.dll`

---

### IModelManager (`Core/Interfaces/IModelManager.cs`)
```csharp
public interface IModelManager : IAsyncDisposable
{
    ModelType ActiveModelType { get; }
    bool      IsReady         { get; }
    Task         InitializeAsync(CancellationToken ct = default);
    Task         SwitchModelAsync(ModelType type, string modelPath, string language, CancellationToken ct = default);
    Task<string> RecognizeAsync(Stream audioWav, CancellationToken ct = default);
    Task         WhenReadyAsync();
}
```
**Реализация:** `ModelManager` — singleton; принимает `IHardwareAccelerator` в конструкторе; в Auto-режиме `IsGpuAccelerationAvailable` выбирает между `ExternalWhisperService` и `ExternalVoskService`; в Manual+Whisper+GPU-requested+NoGPU — `throw InvalidOperationException` + overlay `"❌ GPU недоступен — проверьте ggml-cuda-whisper.dll"` (без тихого CPU fallback).
- `BuildAcceleratorDetails()` — форматирует `[CUDA={bool}, DirectML={bool}, ROCm={bool}, GPU={name}]` для диагностических сообщений.

**WhisperWorkerConfig передача:** `ExternalWhisperService.BuildArguments` → `WhisperWorkerConfig` → `JsonSerializer.Serialize` → `Convert.ToBase64String` → `--config-b64 {b64}`. Никаких отдельных аргументов для отдельных полей.

**GPU-Halt цепочка:** WhisperHost.exe `WhisperFactory(UseGpu=true)` выбрасывает → `WriteLog("critical")` + `WriteHalt("[CRITICAL]...")` → `BaseSpeechHostedService` ctrl-pipe case "halt" → `_logger.LogErrorAsync` + `Faulted?.Invoke()` → `ModelManager.OnWhisperFaulted` → `SwitchEngineAsync(Vosk)`.

**LogForwarder:** все `{"type":"log","level":"...","message":"..."}` из ctrl-pipe маппятся на `ILogService.Log*Async` методы → единый `logs/log_{date}.json`.

**AppSettings расширен:**
- `WhisperSettingsSection` (наследует `VoskSettingsSection`): `HostProcessName="WhisperHost.exe"`, `EngineType="Whisper"`, `StartupTimeoutMs=20000`, `MaxMemoryMb=4096`, `MaxSessionTimeMs=3600000`
- `AppSettings.WhisperSettings` — сконфигурированный экземпляр для DI-передачи в `ExternalWhisperService`
- `GpuSettings.ForceGpuInitialization` (bool, default=false) — при `true` обходит `IHardwareAccelerator` и запускает WhisperHost с `UseGpu=true` напрямую; WhisperHost обнаруживает GPU сам и при неудаче возвращает HALT-сигнал
- `GpuSettings.GpuStartupDelayMs` (int, default=800) — задержка мс перед Process.Start WhisperHost с GPU; даёт VRAM освободиться после Vosk/предыдущего процесса

---

### IObsService (`Core/Interfaces/IObsService.cs`)
```csharp
public interface IObsService
{
    bool IsConnected { get; }
    event EventHandler<bool> ConnectionStatusChanged;
    Task ConnectAsync(string url, string password, CancellationToken ct = default);
    Task DisconnectAsync();
    Task SetCurrentProgramSceneAsync(string sceneName, CancellationToken ct = default);
    Task SetInputMuteAsync(string inputName, bool mute, CancellationToken ct = default);
    Task StartRecordingAsync(CancellationToken ct = default);
    Task StopRecordingAsync(CancellationToken ct = default);
    Task ToggleRecordingAsync(CancellationToken ct = default);
    Task<List<string>> GetScenesAsync(CancellationToken ct = default);
    Task<string> GetCurrentSceneAsync(CancellationToken ct = default);
    Task<bool> IsRecordingAsync(CancellationToken ct = default);
    Task<bool> IsInputMutedAsync(string inputName, CancellationToken ct = default);
    // Каскадные запросы (Сцена → Источник → Фильтр):
    Task<List<string>> GetSceneInputsAsync(string sceneName, CancellationToken ct = default);
    Task<List<string>> GetSourceFiltersAsync(string sourceName, CancellationToken ct = default);
    Task<List<string>> GetAudioSourcesAsync(CancellationToken ct = default);
    // Видимость источников:
    Task<int>  GetSceneItemIdAsync(string sceneName, string sourceName, CancellationToken ct = default);
    Task<bool> GetSceneItemEnabledAsync(string sceneName, int sceneItemId, CancellationToken ct = default);
    Task       SetSceneItemEnabledAsync(string sceneName, int sceneItemId, bool enabled, CancellationToken ct = default);
    Task<bool> GetSourceFilterEnabledAsync(string sourceName, string filterName, CancellationToken ct = default);
    Task       SetSourceFilterEnabledAsync(string sourceName, string filterName, bool enabled, CancellationToken ct = default);
    // Аудио:
    Task<float> GetInputVolumeDbAsync(string inputName, CancellationToken ct = default);
    Task        SetInputVolumeDbAsync(string inputName, float volumeDb, CancellationToken ct = default);
    Task        ToggleInputMuteAsync(string inputName, CancellationToken ct = default);
    // Вещание:
    Task<bool> IsStreamingAsync(CancellationToken ct = default);
    Task StartStreamingAsync(CancellationToken ct = default);
    Task StopStreamingAsync(CancellationToken ct = default);
    Task ToggleStreamingAsync(CancellationToken ct = default);
    // Буфер повторов:
    Task<bool> IsReplayBufferActiveAsync(CancellationToken ct = default);
    Task StartReplayBufferAsync(CancellationToken ct = default);
    Task StopReplayBufferAsync(CancellationToken ct = default);
    Task ToggleReplayBufferAsync(CancellationToken ct = default);
    Task SaveReplayBufferAsync(CancellationToken ct = default);
    // Динамический контент:
    Task<string> GetInputTextAsync(string inputName, CancellationToken ct = default);
    Task         SetInputTextAsync(string inputName, string text, CancellationToken ct = default);
    Task<string> GetInputFilePathAsync(string inputName, CancellationToken ct = default);
    Task         SetInputFilePathAsync(string inputName, string filePath, CancellationToken ct = default);
    Task         TriggerMediaActionAsync(string inputName, string mediaAction, CancellationToken ct = default);
    Task<string> GetMediaStateAsync(string inputName, CancellationToken ct = default);
}
```

---

### ITwitchService (`Core/Interfaces/ITwitchService.cs`)
```csharp
public interface ITwitchService
{
    bool IsConnected { get; }
    event EventHandler<TwitchMessageEventArgs>? OnMessageReceived;
    event EventHandler<bool>? ConnectionStatusChanged;
    Task ConnectAsync(string channel, string username, string oauthToken, CancellationToken ct = default);
    Task DisconnectAsync();
}
```

---

### ISpeechSynthesisService (`Core/Interfaces/ISpeechSynthesisService.cs`)
```csharp
public interface ISpeechSynthesisService : IDisposable
{
    bool IsSpeaking { get; }
    Task SpeakAsync(string text, string modelPath,
        double speed = 1.0, double volume = 1.0, CancellationToken ct = default);
    void Stop();  // Barge-In: немедленная отмена TTS
}
```

---

### ISpeechTriggerService (`Core/Interfaces/ISpeechTriggerService.cs`)
```csharp
public interface ISpeechTriggerService
{
    event Func<string, Task>? SpeechRecognized;
    event Action<double>? LevelUpdated;         // RMS [0..1], ~33 мс интервал
    bool IsRunning    { get; }
    bool IsMonitoring { get; }
    // Предзагружает модель Whisper в VRAM/RAM без запуска захвата аудио.
    // Вызывается StartupManager при старте — делает последующий StartAsync() мгновенным.
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    Task SwitchModelAsync(string modelPath, CancellationToken cancellationToken = default);
    Task StartMonitoringAsync(CancellationToken cancellationToken = default);  // VU-метр без Whisper
    Task StopMonitoringAsync();
    // Завершается после первого StartAsync / StartMonitoringAsync (Race Condition guard)
    Task WhenReadyAsync();
}
```

---

### IInputService (`Core/Interfaces/IInputService.cs`)
```csharp
public interface IInputService
{
    event EventHandler<System.Windows.Point>? MouseMoved;
    event EventHandler<MouseButtonHookEventArgs>? MouseLeftButtonPressed;
    event EventHandler<MouseButtonHookEventArgs>? MouseRightButtonPressed;
    event EventHandler<KeyHookEventArgs>? KeyDown;
    event EventHandler<KeyHookEventArgs>? KeyUp;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task StartGlobalHooksAsync(CancellationToken cancellationToken = default);
    Task StopGlobalHooksAsync(CancellationToken cancellationToken = default);
}
```

---

### IActionService (`Core/Interfaces/IActionService.cs`)
```csharp
public interface IActionService
{
    Task ClickAsync(double x, double y, CancellationToken ct = default);
    Task RightClickAsync(double x, double y, CancellationToken ct = default);
    Task DoubleClickAsync(double x, double y, CancellationToken ct = default);
    Task MoveAsync(double x, double y, CancellationToken ct = default);
    Task ScrollAsync(double x, double y, int amount, CancellationToken ct = default);
    Task MouseButtonDownAsync(double x, double y, CancellationToken ct = default);
    Task MouseButtonUpAsync(double x, double y, CancellationToken ct = default);
    Task PressKeyAsync(Key key, CancellationToken ct = default);
    Task PressKeyWithModifiersAsync(Key key, ModifierKeys modifiers, CancellationToken ct = default);
    Task TypeTextAsync(string text, CancellationToken ct = default);
}
```

---

### IUiAutomationService (`Core/Interfaces/IUiAutomationService.cs`)
```csharp
public interface IUiAutomationService
{
    nint GetActiveWindowHandle();
    Task<List<UiElementInfo>> GetClickableElementsAsync(CancellationToken cancellationToken = default);
}
```

---

### IOverlayService (`Core/Interfaces/IOverlayService.cs`)
```csharp
public interface IOverlayService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ShowOverlayAsync(CancellationToken cancellationToken = default);
    Task HideOverlayAsync(CancellationToken cancellationToken = default);
    Task ShowTextAsync(string text, int durationMilliseconds, CancellationToken ct = default);
    Task ShowHighlightAsync(System.Windows.Point center, double width, double height,
        int durationMilliseconds, CancellationToken ct = default);
    Task ShowStreamingTextAsync(IAsyncEnumerable<string> textStream, CancellationToken ct = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}
```

---

### IQueueService (`Core/Interfaces/IQueueService.cs`)
```csharp
public interface IQueueService
{
    QueueStore Store { get; }
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
    QueueRegion? GetRegionById(Guid id);
    bool TryAddRegion(string name, out QueueRegion? region, out string? error);
    bool TryRenameRegion(QueueRegion region, string newName, out string? error);
    void DeleteRegion(QueueRegion region);
    bool TryAddFolder(QueueRegion region, QueueFolder? parent, string name,
                      out QueueFolder? folder, out string? error);
    bool TryRenameFolder(QueueFolder folder, QueueFolder? parent,
                         QueueRegion region, string newName, out string? error);
    void DeleteFolder(QueueRegion region, QueueFolder? parent, QueueFolder folder);
}
```
**Реализация:** `QueueService` — файл `queues.json` в `AppContext.BaseDirectory`, `SemaphoreSlim(1,1)`, `JsonObjectCreationHandling.Populate`.
Валидация: дубли имён → error string; глубина папок = 1 (SubFolders не могут содержать регионы).
Статический хелпер: `public static (QueueFolder? Parent, bool Found) FindFolder(QueueRegion, QueueFolder target)`.

---

### IStartupOrchestrator (`Core/Interfaces/IStartupOrchestrator.cs`)
```csharp
public interface IStartupOrchestrator
{
    bool IsReady { get; }
    event EventHandler? ReadyStateChanged;
    event EventHandler<StartupPhaseEventArgs>? PhaseCompleted;
    Task RunAsync(CancellationToken ct = default);
}
public sealed record StartupPhaseEventArgs(string PhaseName, bool Success, string? ErrorMessage = null);
```
**Реализация:** `StartupOrchestrator` — Singleton, запускается из `App.xaml.cs` через `Task.Run`.

**Фазы прогрева (warm-up sequence):**
```
Phase 0 — GPU:        CudaDiagnostics.CheckCompatibilityAsync → аудит nvidia-smi, CUDA версии
                      → IHardwareAccelerator.WaitForCudaAsync(4, 1000) — адаптивный проб: fast path при IsCudaAvailable=true, иначе до 4 попыток × 1 сек
Phase 1 — Speech:     +500 мс → SpeechTriggerService.InitializeAsync → StartAsync/StartMonitoringAsync
Phase 2 — MacroIndex: +1000 мс → лог профилей/макросов в памяти
Phase 3 — Processes:  ProcessWatcher.Start → кэш RunningProcessNames → ProcessStarted/Exited события
Финал:    IsReady = true → ReadyStateChanged.Invoke → DashboardViewModel.IsReady → скрытие спиннера
```
**Свойства:** ошибка в фазе не роняет очередь (catch + лог + PhaseCompleted(false)). Отмена через OperationCanceledException пропагируется.

---

### IProcessWatcher (`Core/Interfaces/IProcessWatcher.cs`)
```csharp
public interface IProcessWatcher
{
    IReadOnlySet<string> RunningProcessNames { get; }
    event EventHandler<ProcessWatcherEventArgs>? ProcessStarted;
    event EventHandler<ProcessWatcherEventArgs>? ProcessExited;
    void Start(CancellationToken ct = default);
    void Stop();
}
public sealed record ProcessWatcherEventArgs(string ProcessName, int ProcessId);
```
**Реализация:** `ProcessWatcher` — poll каждые 2 сек через `Process.GetProcesses()`, diff между снимками, events через `ProcessStarted`/`ProcessExited`.
**MacroScheduler интеграция:** при `ProcessStarted` → `HandleProcessStartedAsync` → автоактивация профиля с совпадающим `TargetProcessName` если `_activeProfile == null`.

---

### INodeEngine (`Core/Interfaces/INodeEngine.cs`)
```csharp
public interface INodeEngine
{
    bool IsRunning { get; }
    IEnumerable<BaseNode> Nodes { get; }
    Action<string>? DebugSink { get; set; }
    void RegisterNodes(IEnumerable<BaseNode> nodes);
    void RegisterConnections(IEnumerable<VisualConnection> connections);
    Task StartAsync(Guid startNodeId, CancellationToken cancellationToken = default);
    Task StartAsync(Guid startNodeId, MacroExecutionContext context, CancellationToken cancellationToken = default);
    void Stop();
}
```

---

## 4. АРХИТЕКТУРА ДВУХКАНАЛЬНОГО ДВИЖКА НОД

### MacroExecutionContext (`Core/Models/ExecutionContext.cs`)
```csharp
public sealed class MacroExecutionContext
{
    // Шина данных: thread-safe, OrdinalIgnoreCase ключи
    public ConcurrentDictionary<string, object> Variables { get; }

    // Атомарные счётчики (Interlocked) — используются NodeEngine и частичными шлюзами
    public int ActiveNodeCount    { get; }   // в процессе выполнения
    public int CompletedNodeCount { get; }   // завершено (успех + провал)
    public int ExecutedNodesCount { get; }   // только успешные (для Logic_QueueBlockNode)

    public void BeginNode();            // +1 _activeNodes
    public void EndNode();              // -1 _activeNodes, +1 _completedNodes
    public void IncrementExecutedCount(); // +1 _executedNodesCount
}
```
**Форматы ключей Variables:**
- `Var_{sourceNodeId}` — сырое значение источника (последнее записанное)
- `In:{targetNodeId}:{propName}` — адресная доставка в конкретное свойство ноды-приёмника

---

### DataPacket (`Core/Models/DataPacket.cs`)
```csharp
public enum DataType { Text, Image, File, Audio }

public sealed class DataPacket
{
    public required object Payload  { get; init; }
    public DataType        Type     { get; init; }
    public string          MetaData { get; init; } = string.Empty;
    public override string ToString() => Payload?.ToString() ?? string.Empty;
}
```

---

### BaseNode (`Core/Nodes/BaseNode.cs`) — ключевые члены
```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
// ...37 [JsonDerivedType] атрибутов...
public abstract class BaseNode : INotifyPropertyChanged
{
    // Identity
    public Guid   Id   { get; init; } = Guid.NewGuid();
    public string Name { get; set; }

    // Граф
    public Guid? OnSuccessNodeId { get; set; }
    public Guid? OnErrorNodeId   { get; set; }
    public int   QueuePriority   { get; set; } = 0;  // 0=параллельно, >0=очередь

    // Канал данных
    public Guid?  DataOutputNodeId       { get; set; }
    public string DataOutputPropertyName { get; set; }
    public bool   IsDataOutputEnabled    { get; set; } = true;
    [JsonIgnore] public object? LastOutputValue { get; protected set; }

    // Безопасность
    [JsonIgnore] public virtual bool   IsDangerous       => false;
    [JsonIgnore] public virtual string DangerWarningText => string.Empty;

    // Канал данных
    [JsonIgnore] public virtual string DefaultDataInputPropertyName => string.Empty;

    // Отладка
    [JsonIgnore] public Action<string>? DebugSink { get; set; }

    // TryApplyContextInput<T>: читает In:{Id}:{prop}, распаковывает DataPacket,
    // 5-уровневый каскад кастинга (exact → string → int → double → IConvertible),
    // вызывает RaisePropertyChanged после успешного применения.
    protected bool TryApplyContextInput<T>(string propertyName, Action<T> setter);

    // Шаблонный метод выполнения
    protected abstract Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider, ILogService logger, CancellationToken ct);

    // Публичная обёртка: устанавливает State, ловит исключения
    public async Task<bool> ExecuteAsync(
        IServiceProvider serviceProvider, ILogService logger,
        MacroExecutionContext context, CancellationToken ct);
}
```

---

### NodeEngine (`Core/Services/NodeEngine.cs`) — алгоритм выполнения

```
StartAsync(startNodeId)
  └─ ExecuteBranchAsync(nodeId, context, ct)
       ├─ node.ResetToPending() [все ноды в начале]
       ├─ node.DebugSink = DebugSink  ← прокидывание делегата
       ├─ node.ExecuteAsync(serviceProvider, logger, context, ct)
       │    └─ ExecuteCoreAsync(...) [переопределяется в каждой ноде]
       │
       ├─ [если success && IsDataOutputEnabled && LastOutputValue != null]
       │    dataWires = _visualConnections.Where(IsDataRoute && SourceId==node.Id)
       │    если dataWires.Count > 0:
       │      context.Variables["Var_{node.Id}"] = LastOutputValue  ← один раз
       │      foreach wire:
       │        propName = target.DefaultDataInputPropertyName
       │        context.Variables["In:{target.Id}:{propName}"] = LastOutputValue
       │        рефлексия: prop.SetValue(target, value) если не DataPacket
       │        target.RaisePropertyChanged(propName)
       │
       ├─ [если Logic_SequenceNode] ExecuteSequencerStepsAsync(...)
       │
       └─ GetConnectedNodeIds(nodeId, isSuccessRoute):
            1. Жёлтые провода (!IsDataRoute, IsErrorRoute==!success)
            2. Серебряный как неявный success: если жёлтых нет && success → берём DataRoute цели
            3. Fallback: OnSuccessNodeId / OnErrorNodeId из логики ноды
            └─ Разделение: fullGateIds / partialGateIds / regularIds
                 QueuePriority > 0 → строго последовательно
                 regularIds → Task.Run() параллельный Fork
                 fullGateIds → Task.WhenAll(parallel) → execute gate
                 partialGateIds → polling(50мс) пока ExecutedNodesCount - start < WaitNodesCount
```

---

## 5. РЕЕСТР ВСЕХ 38 НОД (JsonDerivedType)

| № | Дискриминатор `$type` | Класс | Категория | DefaultDataInputPropertyName |
|---|---|---|---|---|
| 1 | `delay` | `DelayNode` | Logic | `nameof(DelayMilliseconds)` |
| 2 | `overlay` | `OverlayTextNode` | Output | `nameof(Text)` |
| 3 | `hotkey` | `HotkeyTriggerNode` | Input | `"HotKeyText"` |
| 4 | `mouse_click` | `MouseClickNode` | Input | — |
| 5 | `key_press` | `KeyPressNode` | Input | — |
| 6 | `text_write` | `TextWriteNode` | Output | `nameof(Text)` |
| 7 | `color_search` | `ColorSearchNode` | Vision | — |
| 8 | `template_match` | `TemplateMatchNode` | Vision | — |
| 9 | `network_status` | `NetworkStatusNode` | Network | — |
| 10 | `send_input` | `SendInputNode` | Input | `"TargetKey"` |
| 11 | `mouse_action` | `MouseActionNode` | Input | `"X"` |
| 12 | `run_process` | `RunProcessNode` | Win | `nameof(FilePathOrUrl)` |
| 13 | `clipboard` | `ClipboardNode` | Win | `nameof(TextToWrite)` |
| 14 | `text_condition` | `TextConditionNode` | Logic | `nameof(InputValue)` |
| 15 | `obs_set_scene` | `ObsSetSceneNode` | OBS | — |
| 16 | `obs_toggle_mute` | `ObsToggleMuteNode` | OBS | — |
| 17 | `obs_record_control` | `ObsRecordControlNode` | OBS | — |
| 18 | `obs_scene_manager` | `OBS_SceneManagerNode` | OBS | — |
| 19 | `obs_source_visibility_manager` | `OBS_SourceVisibilityManagerNode` | OBS | — |
| 20 | `obs_audio_manager` | `OBS_AudioManagerNode` | OBS | — |
| 21 | `obs_stream_record_manager` | `OBS_StreamAndRecordManagerNode` | OBS | — |
| 22 | `obs_dynamic_content_manager` | `OBS_DynamicContentManagerNode` | OBS | `nameof(TextContent)` |
| 23 | `win_speak_text` | `Win_SpeakTextNode` | Win | `nameof(TextToSpeak)` |
| 24 | `win_process_manager` | `Win_ProcessManagerNode` | Win | `nameof(InputValue)` |
| 25 | `win_window_manager` | `Win_WindowManagerNode` | Win | `nameof(InputValue)` |
| 26 | `win_system_power` | `Win_SystemPowerNode` | Win | — |
| 27 | `win_powershell` | `Win_PowerShellNode` | Win | `nameof(ScriptText)` |
| 28 | `win_audio_device` | `Win_AudioDeviceNode` | Win | `nameof(DeviceName)` |
| 29 | `wait_smart_delay` | `Wait_SmartDelayNode` | Logic | — |
| 30 | `logic_counter` | `Logic_CounterNode` | Logic | — |
| 31 | `logic_branch` | `Logic_BranchNode` | Logic | `nameof(InputValue)` |
| 32 | `speech_trigger` | `SpeechTriggerNode` | Input | `"Text"` |
| 33 | `web_request` | `Web_RequestNode` | Web | `nameof(Url)` |
| 34 | `vision_ocr` | `Vision_OcrNode` | Vision | `nameof(ImagePath)` |
| 35 | `win_translate` | `Win_TranslateNode` | Win | `nameof(SourceText)` |
| 36 | `logic_sequence` | `Logic_SequenceNode` | Logic | — |
| 37 | `logic_queue_block` | `Logic_QueueBlockNode` | Logic | — |
| 38 | `exclusive_gate` | `Win_ExclusiveGateNode` | УМНАЯ ЛОГИКА | — |
| 39 | `bypass_queue` | `Win_BypassQueueNode` | УМНАЯ ЛОГИКА | — |
| 40 | `trigger_root` | `TriggerRootNode` | АРХИТЕКТУРНАЯ | — |

### Win_ExclusiveGateNode — монопольный режим (MacroScheduler)

Нода-маркер: при наличии в начале графа `MacroScheduler` переключает макрос в **эксклюзивный режим**.

```
Поля MacroScheduler:
  _activeMacroCount : int       — Interlocked (кол-во активных макросов)
  _exclusiveRunning : bool      — volatile (флаг монопольного выполнения)
  _exclusiveLock    : object    — синхронизация входа/выхода
  _systemPendingQueue : Queue<SystemPending>  — ожидающие запуска

Логика входа (lock _exclusiveLock):
  canStart = !_exclusiveRunning && (!isExclusive || _activeMacroCount == 0)
  если canStart → если exclusive: _exclusiveRunning=true
                  Interlocked.Increment(_activeMacroCount)
  иначе → _systemPendingQueue.Enqueue(pending); return (отложен)

ReleaseExecutionSlot(wasExclusive):
  lock(_exclusiveLock):
    если wasExclusive → _exclusiveRunning = false
    Interlocked.Decrement(_activeMacroCount)
    цикл: peek → canStart? → dequeue → increment → список toStart
  запустить toStart через EnqueueMacroImmediate (вне замка)
```

Двойной поиск макросов (обратная совместимость):
- Legacy путь: `profile.Regions[].Macros[]` (старые профили)
- Новый путь: `profile.Macros[]` + рекурсивно `folder.Macros[]` через `GetAllMacros(AppProfile)`

---

### Паттерн PhraseItem (авторасширяющийся список)
Используется в: `Win_ProcessManagerNode`, `Win_WindowManagerNode`, `SpeechTriggerNode`.
```csharp
public sealed class PhraseItem : INotifyPropertyChanged
{
    internal PhraseItem(string text);
    public string Text { get; set; }  // INPC
}
// В ноде:
[JsonIgnore] public ObservableCollection<PhraseItem> ProcessesList { get; } = [];
[JsonPropertyName("processes")]
public List<string> ProcessesData { get; set; }  // JSON-прокси (getter: LINQ, setter: Clear+repopulate)
// EnsureTrailingEmpty() — последний элемент всегда пустой
```

### Win_PowerShellNode — ключевые особенности
- `DefaultDataInputPropertyName = nameof(ScriptText)` — провод доставляет сценарий напрямую
- `ConsoleOutput` (`[JsonIgnore]`, INPC) — отображает последний stdout/stderr в UI после выполнения
- PowerShell args: `-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {base64}`
- Преамбула скрипта: `$ProgressPreference = 'SilentlyContinue'; $OutputEncoding = [Console]::OutputEncoding = [System.Text.Encoding]::UTF8;`
- `ProcessStartInfo`: `StandardOutputEncoding = Encoding.UTF8`, `StandardErrorEncoding = Encoding.UTF8`
- Порядок: `ReadToEndAsync → WhenAll → output/error → WaitForExitAsync → ExitCode`
- `FilterCliXml(string)`: regex `#<\s*CLIXML[\s\S]*?</Objs>` с fast-path через `Contains`

### ClipboardNode — retry механизм
- `SetClipboardTextWithRetryAsync`: 5 попыток × 50 мс при `COMException(0x800401D0 CLIPBRD_E_CANT_OPEN)`
- `GetClipboardTextWithRetryAsync`: аналогично для чтения
- Empty Write Guard: `IsNullOrWhiteSpace(TextToWrite)` → пропуск записи (return `true`)

### Logic_QueueBlockNode — синхронизация
- `WaitFullChain = true`: `Task.WhenAll(parallelTasks)` затем execute gate
- `WaitFullChain = false`: polling 50 мс, пока `ExecutedNodesCount - startCount < WaitNodesCount`
- Снапшот счётчика до запуска параллельных задач

---

## 6. ГОЛОСОВОЙ ПАЙПЛАЙН (VAD / Whisper / Kokoro / RNNoise)

### Аудит: RAM-ONLY подход (нулевой дисковый I/O)

**ПОДТВЕРЖДЕНО:** В голосовом конвейере `SpeechTriggerService` временные файлы на диске **НЕ используются ни в одной точке**. Весь путь — исключительно оперативная память:

| Компонент | Тип хранилища | Комментарий |
|---|---|---|
| `_audioBuffer` | `MemoryStream` | Накопительный WAV-буфер (RAM) |
| `WaveFileWriter(_audioBuffer, ...)` | пишет в `MemoryStream` | WAV-заголовок + PCM16 — строго в RAM |
| `snapshot = new MemoryStream(buffer.ToArray(), writable: false)` | `MemoryStream` (copy) | Изолированная копия для инференса |
| `TranscribeAsync(MemoryStream audioBuffer)` | получает `MemoryStream` | Никакого `File.Create` / `Path.GetTempFileName` |
| `RnNoiseDenoiser.ProcessInPlace(byte[], int)` | `byte[]` in-place | Изменяет исходный чанк без аллокаций |

---

### SpeechTriggerService — полный конвейер обработки аудио

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                         [Поток NAudio (Thread Pool)]                         │
│                                                                              │
│  NAudio WaveInEvent                                                          │
│  16 кГц / 16-bit / Моно / 33 мс чанки (~1056 байт)                          │
│    │                                                                         │
│    ▼ OnDataAvailable(sender, WaveInEventArgs e)                             │
│                                                                              │
│  1. ТТС-подавление эха                                                       │
│     IsSpeaking → Array.Clear(buffer)  ← нейтрализует петлю AEC              │
│    │                                                                         │
│  2. RNNoise: ProcessInPlace(buffer, bytesRecorded)                           │
│     16 кГц PCM16 → ×3 апсемплинг → rnnoise_process_frame → ×3 децимация    │
│     Zero-Allocation FIFO (short[8192] inFifo / outFifo)                     │
│     [IsAvailable=false → no-op (graceful degradation без падения)]           │
│    │                                                                         │
│  3. RMS Calculation → LevelUpdated?.Invoke(rms) ← VU-метр в UI              │
│     rms = sqrt( Σ(sample²) / N )                                            │
│    │                                                                         │
│  ── if (!_isRunning) return;  ← режим мониторинга: только VU-метр ──        │
│    │                                                                         │
│  4. VAD Gate (lock _captureLock)                                             │
│     rms >= SpeechRmsThreshold (default 0.02)?                               │
│     ├─ YES → захват активен                                                  │
│     │   _audioBuffer = new MemoryStream()          ← RAM                    │
│     │   _waveWriter  = new WaveFileWriter(_audioBuffer, _waveFormat)         │
│     │   _waveWriter.Write(e.Buffer, 0, e.BytesRecorded) ← WAV в RAM         │
│     │                                                                        │
│     ├─ NO + _isCapturing → дописываем тишину (плавное завершение)           │
│     │   SiriTimer: буфер < 3.0с → 1200 мс тишины                           │
│     │              буфер ≥ 3.0с → 2500 мс тишины                           │
│     │                                                                        │
│     └─ Timeout! → FlushAndScheduleTranscription()                           │
│                    writer.Dispose()  ← финализирует RIFF-заголовок в RAM    │
│                    snapshot = new MemoryStream(buffer.ToArray(), false)      │
│                    _ = Task.Run(() => TranscribeAsync(snapshot))             │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
                                     │
                   [Отдельный Task на Thread Pool — не блокирует UI/NAudio]
                                     │
                                     ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         TranscribeAsync(MemoryStream)                        │
│                                                                              │
│  await _processingLock.WaitAsync()  ← одновременно только один инференс     │
│                                                                              │
│  if (audioBuffer.Length < 16_044) return;  ← фильтр микрофрагментов         │
│                                                                              │
│  var sw = Stopwatch.StartNew();                                              │
│  await foreach (var seg in _processor.ProcessAsync(audioBuffer))            │
│      sb.Append(seg.Text);                                                    │
│  sw.Stop();                                                                  │
│                                                                              │
│  LogInfoAsync($"[ГОЛОС] Распознано: '{text}' (инференс: N мс)")             │
│                                                                              │
│  SpeechRecognized?.Invoke(text) → MacroScheduler → NodeEngine               │
│                                                                              │
│  audioBuffer.Dispose()  ← освобождаем MemoryStream                          │
└──────────────────────────────────────────────────────────────────────────────┘
```

**Константы:**

| Константа | Значение | Назначение |
|---|---|---|
| `SampleRate` | `16_000` Гц | Частота дискретизации |
| `BitsPerSample` | `16` | PCM16 Little-Endian |
| `MinAudioBytes` | `16_044` | Минимум ~0.5 с аудио (16000 байт PCM + 44 байта WAV-заголовок) |
| `ShortSilenceMs` | `1200` мс | Таймер тишины при длине буфера < 3.0 с |
| `LongSilenceMs` | `2500` мс | Таймер тишины при длине буфера ≥ 3.0 с |
| `LongUtteranceThresholdBytes` | `96_000` | 3.0 с PCM16 при 16 кГц (граница Siri-таймера) |

**Модели Whisper:**
- `Models/Whisper/base/ggml-base.bin` — базовая, быстрая (CPU ~300 мс, GPU ~50 мс)
- `Models/Whisper/turbo/ggml-large-v3-turbo.bin` — точная (CPU ~1500 мс, GPU ~200 мс)

**Жизненный цикл сервиса (Startup):**
```
StartupManager (t+500 мс)
  ├─ SpeechTriggerService.InitializeAsync()
  │   └─ ModelManager.InitializeAsync()
  │       ├─ UseGpuAcceleration=false? → WhisperModelWrapper (CPU)
  │       ├─ CUDA доступна? → WhisperModelWrapper (GPU)
  │       └─ GPU запрошен, но CUDA нет? → VoskModelWrapper (CPU) + StartGpuWatchdog()
  │
  └─ StartAsync() (SpeechEnabled=true) или StartMonitoringAsync() (false)
      ├─ WaveInEvent.StartRecording()  ← начало потока NAudio
      └─ _isRunning = true  / _isMonitoring = true
```

**Инференс (после рефакторинга):**
```
TranscribeAsync(MemoryStream)
  └─ _modelManager.RecognizeAsync(audioWav)
      ├─ WhisperModelWrapper: _processor.ProcessAsync(stream) → IAsyncEnumerable<SegmentData>
      └─ VoskModelWrapper: WaveFileReader → short[] → recognizer.AcceptWaveform → FinalResult()
```

---

### NetworkService — экспоненциальный бэкофф реконнекта

**Управление через `AppConfig.NetworkEnabled` (по умолчанию `false`):**
- `false` → `ConnectionLoopAsync` немедленно возвращается; нет попыток подключения
- `true` → запускается цикл с экспоненциальной задержкой

**Таблица задержек (`static readonly int[] ReconnectDelays`):**

| Попытка | Задержка |
|---|---|
| 0 (первая ошибка) | 5 с |
| 1 | 10 с |
| 2 | 30 с |
| 3+ | 60 с (максимум) |
| После успешного соединения (затем разрыв) | 5 с (сброс к 0) |

---

### RnNoiseDenoiser (`Core/Audio/RnNoiseDenoiser.cs`)

**P/Invoke (`rnnoise.dll` x64):**
```csharp
[LibraryImport("rnnoise", EntryPoint = "rnnoise_create")]
private static partial nint RnnoiseCreate(nint model);

[LibraryImport("rnnoise", EntryPoint = "rnnoise_destroy")]
private static partial void RnnoiseDestroy(nint state);

[LibraryImport("rnnoise", EntryPoint = "rnnoise_process_frame")]
private static unsafe partial float RnnoiseProcessFrame(nint st, float* output, float* input);
```

**Алгоритм (Zero-Allocation FIFO):**
```
Входной чанк (16 кГц, short[])
  ├─ Линейный апсемплинг ×3 → 48 кГц, float[] (FrameSize48k=480 сэмплов = 10 мс)
  ├─ rnnoise_process_frame(state, outFrame, inFrame) → float[480]
  ├─ Децимация ×3 (усреднение 3 сэмплов) → 16 кГц, short[]
  └─ Вывод через outFifo[8192] по размеру входного чанка
```

---

### ModelManager — динамическое переключение моделей STT

**Схема выбора движка при старте:**
```
ModelManager.InitializeAsync()
│
├─ UseGpuAcceleration = false
│   └─ WhisperModelWrapper(CPU) ← Whisper быстрее Vosk на CPU при длинном аудио
│
├─ UseGpuAcceleration = true && HardwareMonitor.IsCudaAvailable()
│   └─ WhisperModelWrapper(CUDA GPU) ← высший приоритет
│
└─ UseGpuAcceleration = true && CUDA НЕТ
    ├─ VoskModelWrapper(CPU) ← 5-10× быстрее Whisper-CPU для коротких команд
    └─ StartGpuWatchdog() ← таймер 30 с / проверяет HardwareMonitor.RefreshCuda()
         └─ GPU появился? → SwitchModelAsync(Whisper) → StopWatchdog
```

**SwitchModelAsync — порядок операций (без утечек памяти):**
```
1. DisposeActiveWrapperLockedAsync()
   ├─ old.DisposeAsync()             ← Whisper: _processor.DisposeAsync() + _factory.Dispose()
   │                                   Vosk:    _recognizer.Dispose() + _model.Dispose()
   ├─ GC.Collect(MaxGeneration, Forced, blocking: true)
   └─ GC.WaitForPendingFinalizers()  ← гарантирует освобождение VRAM нативными финализаторами

2. LoadWhisperLockedAsync() / LoadVoskLockedAsync()
   └─ новый wrapper.InitializeAsync(modelPath, language, ct)
```

**Авто-триггер смены языка:**
```
ConfigService.SaveAsync()
  └─ ConfigSaved?.Invoke()
       └─ ModelManager.OnConfigSaved()
            └─ SpeechLanguage изменился? → SwitchModelAsync(ActiveModelType, path, newLang)
```

**HardwareMonitor.cs — CUDA-детектор:**
```csharp
// Кэш: volatile bool _cudaAvailable + volatile bool _cudaCached
static bool IsCudaAvailable()    // кэшированная проверка
static bool RefreshCuda()        // сброс кэша + переобнаружение (для Watchdog)

// Алгоритм DetectCuda():
//   1. Exists("ggml-cuda.dll") — быстрый IO-фильтр
//   2. NativeLibrary.TryLoad(path) — реальная загрузка DLL
//   3. NativeLibrary.Free(handle)  — немедленное освобождение
```

**AppConfig — новые поля:**
```csharp
public string VoskModelPath  { get; set; } = @"Models\Vosk\vosk-model-small-ru-0.22";
public string SpeechLanguage { get; set; } = "ru";
```

**Unit-тесты (ARK.UI.Tests / ModelManagerTests.cs):**

| Тест | Проверяет |
|---|---|
| `InitializeAsync_WhenGpuDisabledAndNoModel_CompletesWithoutException` | Нет исключения при отсутствии файла модели |
| `InitializeAsync_IsDemultiplexed_SecondCallIsNoop` | Конкурентные вызовы не дублируют загрузку |
| `RecognizeAsync_WhenNotReady_ReturnsEmptyString` | Пустая строка до инициализации |
| `SwitchModelAsync_DisposesOldWrapperAndLogsSwitch` | Тип переключился, лог зафиксирован |
| `DisposeAsync_IsIdempotent` | Двойной Dispose не падает |
| `ConfigSaved_WithSameLanguage_DoesNotTriggerSwitch` | Одинаковый язык не вызывает перезагрузку |

---

### SpeechSynthesisService (`Core/Services/SpeechSynthesisService.cs`)

**Тёплый WaveOut (500 мс демпфирование):**
- `_sharedWaveOut` + `_sharedProvider` — переиспользуются между предложениями
- `IdleGraceMs = 500` мс после последней фразы перед `_sharedWaveOut.Stop()`
- `_idleShutdownCts` — перезапускаемый таймер завершения устройства

**Barge-In:** `Stop()` → `_speakCts.Cancel()` + `_kokoroEngine.StopPlayback()` + `_currentProcess?.Kill()`

**Пути:**
- KokoroSharp ONNX: `Models/TTS/Kokoro/kokoro-v1.0.onnx`
- Piper: `Tools\Piper\piper.exe`

---

## 7. ВИЗУАЛЬНЫЙ СЛОЙ И СТИЛИ (XAML / Themes)

### Obsidian Gold палитра (`Themes/Brushes.xaml`)

| Ключ ресурса | Тип | Значение |
|---|---|---|
| `ObsidianBackgroundBrush` | `LinearGradientBrush` (45°) | `#121212` → `#1A1A1A` |
| `GoldBrush` | `LinearGradientBrush` (90°) | `#AA7C11` → `#D4AF37` → `#F5D77F` |
| `GoldGlowEffect` | `DropShadowEffect` (`x:Shared=False`) | Color=`#D4AF37`, BlurRadius=15, Opacity=0.6 |
| `RubyBrush` | `LinearGradientBrush` (90°) | `#800000` → `#FF3B30` |
| `RubyGlowEffect` | `DropShadowEffect` (`x:Shared=False`) | Color=`#FF3B30`, BlurRadius=12, Opacity=0.85 |
| `SilverDataPortBrush` | `SolidColorBrush` | `#D3D3D3` |
| `SilverGlowEffect` | `DropShadowEffect` (`x:Shared=False`) | Color=`#D3D3D3`, BlurRadius=12, Opacity=0.8 |
| `DarkBorderBrush` | `SolidColorBrush` | `#2A2A2A` |

**Цветовой код проводов:**
- **Жёлтый** `#F5D77F` — провод успеха (on_success)
- **Рубиновый** `#FF3B30` — провод ошибки (on_error)
- **Серебряный** `#D3D3D3` — провод данных (data wire)

---

### MarqueeBehavior (`Core/Services/MarqueeBehavior.cs`)
Attached behavior на `TextBlock`. Активируется через `behaviors:MarqueeBehavior.IsEnabled="True"`.

```csharp
// При MouseEnter:
var ft = new FormattedText(tb.Text, ...);
double overflow = ft.Width - tb.ActualWidth;
if (overflow > 0)
{
    // DoubleAnimation: TranslateTransform.X: 0 → -overflow
    // Duration: overflow / 40 секунд (скорость 40 px/с)
}

// При MouseLeave:
// DoubleAnimation: TranslateTransform.X → 0, Duration 0.3с, EaseOut
```

**Правило проекта:** обязателен во ВСЕХ `ComboBox.ItemTemplate` / `ListBox.ItemTemplate` с динамическими именами (OBS-сцены, TTS-голоса, профили, устройства).

---

### UrlCursorBehavior (`Core/Services/UrlCursorBehavior.cs`)
```csharp
// Attached property: behaviors:UrlCursorBehavior.Enabled="True" на TextBox
// ConditionalWeakTable<TextBox, UrlCache> — weak reference, не держит TextBox живым
// На каждый MouseMove:
//   1. GetCharacterIndexFromPoint(mousePos)
//   2. Если UrlRegex.Matches кэш устарел (Text изменился) → пересчитать ranges
//   3. Если индекс попадает в URL-диапазон → Cursor = Cursors.Hand
//   4. Иначе → Cursor = Cursors.IBeam
```

---

### Двухслойный лог-терминал (`Views/LogsTerminalControl.xaml`)

```
ScrollViewer (общий)
├─ TextBox (IsReadOnly, нижний слой)
│    Text = {Binding LogOutputText}
│    behaviors:UrlCursorBehavior.Enabled="True"
│    behaviors:ScrollSyncBehavior (синхронизация вертикального скролла с TextBlock)
└─ TextBlock (верхний слой, IsHitTestVisible=False)
     behaviors:LogInlinesBehavior (рендер Inline с гиперссылками Run/Hyperlink)
     behaviors:LogsDocumentBehavior (авто-скролл вниз при новых записях)
```
TextBlock закрывает TextBox по Z-order, но `IsHitTestVisible=False` пропускает клики в TextBox для выделения/копирования. URL-подстроки рендерятся как синие `Hyperlink` в TextBlock; `HyperlinkClickBehavior` открывает их в браузере.

---

### Reveal PasswordBox (`Views/PasswordDialog.xaml`)
- Два слоя: `PasswordBox` (обычный ввод) + `TextBox` (`IsReadOnly` при показе, `Visibility=Collapsed`)
- Иконка «глазик»: `Path` с WPF Geometry (открытый/закрытый глаз)
- `PasswordBehavior` attached behavior: синхронизирует `PasswordBox.SecurePassword` ↔ ViewModel
- `PasswordEyeIconConverter`: `bool IsPasswordVisible → PathGeometry`
- Переключение: `IsKeyboardFocusWithin` триггер (без Storyboard, без TargetName — Anti-Crash Rule)

---

### Node Parameters Focus Safety Rule
**ЗАПРЕЩЕНО:**
```xml
<EventTrigger RoutedEvent="LostFocus">
    <BeginStoryboard>
        <Storyboard TargetName="Border_Highlight" .../>  <!-- CRASH при удалении ноды -->
    </BeginStoryboard>
</EventTrigger>
```
**ПРАВИЛЬНО:**
```xml
<Trigger Property="IsKeyboardFocused" Value="True">
    <Setter TargetName="Border_Highlight" Property="Opacity" Value="1"/>
</Trigger>
```

---

## 8. НАТИВНЫЕ P/INVOKE (`Core/Services/Win32Api.cs`)

```csharp
// user32.dll
[LibraryImport] int  GetWindowLong(IntPtr hWnd, int nIndex);
[LibraryImport] int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
[LibraryImport] bool GetCursorPos(out POINT lpPoint);
[LibraryImport] bool SetForegroundWindow(IntPtr hWnd);
[LibraryImport] bool BringWindowToTop(IntPtr hWnd);
[LibraryImport] IntPtr SetActiveWindow(IntPtr hWnd);
[LibraryImport] bool ReleaseCapture();
[LibraryImport] IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
[LibraryImport] IntPtr GetForegroundWindow();
[LibraryImport] bool ShowWindow(IntPtr hWnd, int nCmdShow);
[LibraryImport] bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
[LibraryImport] IntPtr FindWindowW(string? lpClassName, string? lpWindowName);
[LibraryImport] bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
[LibraryImport] uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[LibraryImport] IntPtr GetDC(IntPtr hWnd);
[LibraryImport] int ReleaseDC(IntPtr hWnd, IntPtr hDC);

// gdi32.dll
[LibraryImport] uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

// user32.dll (DllImport — CharSet.Unicode обязателен)
[DllImport] int  GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);
[DllImport] bool DestroyIcon(IntPtr hIcon);

// shell32.dll (SHGetFileInfoW — иконки процессов)
[DllImport] IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes,
    ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
// SHGFI_ICON=0x100, SHGFI_SMALLICON=0x001

// shlwapi.dll (AssocQueryStringW — ассоциации файлов)
[DllImport] int AssocQueryStringW(uint flags, uint str, string pszAssoc,
    string? pszExtra, char[]? pszOut, ref uint pcchOut);
// ASSOCF_NONE=0, ASSOCSTR_EXECUTABLE=2
```

**Win32 константы:**
```csharp
GWL_EXSTYLE       = -20
WS_EX_TRANSPARENT = 0x00000020  // overlay: клики проходят сквозь
WS_EX_TOOLWINDOW  = 0x00000080  // не отображается в Alt+Tab
WS_EX_NOACTIVATE  = 0x08000000  // не перехватывает фокус
WM_SYSCOMMAND     = 0x0112
SC_SIZE_SE        = 0xF008      // ресайз нижний правый угол
```

---

## 9. ГЛОБАЛЬНЫЕ ПЕРЕХВАТЧИКИ ИСКЛЮЧЕНИЙ (`App.xaml.cs`)

```csharp
// В OnStartup():
DispatcherUnhandledException               += OnDispatcherUnhandledException;
AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
TaskScheduler.UnobservedTaskException      += OnUnobservedTaskException;
```
Три уровня: UI-поток (Dispatcher), CLR домен приложения, необслуженные Task-исключения.

---

## 10. ЛОКАЛИЗАЦИЯ (RU/EN)

### Strings.resx + Strings.en.resx
- `Resources/Strings.resx` — основной RU словарь (генерирует `Strings.Designer.cs` через `PublicResXFileCodeGenerator`)
- `Resources/Strings.en.resx` — EN локаль (Culture=`en`)
- Доступ: `ARK.UI.Resources.Strings.ResourceManager.GetString(key, Strings.Culture)`

### EnumToLocalizedDescriptionConverter (`Converters/EnumToLocalizedDescriptionConverter.cs`)
```csharp
// Паттерн ключа: "Enum_{EnumTypeName}_{EnumValue}"
// Пример: PowerAction.Shutdown → "Enum_PowerAction_Shutdown"
var key = $"Enum_{value.GetType().Name}_{value}";
return Strings.ResourceManager.GetString(key, Strings.Culture) ?? value.ToString();
```

### LocalizationService (`Core/Services/LocalizationService.cs`)
```csharp
public static class LocalizationService
{
    public static event EventHandler? CultureChanged;
    public static void NotifyCultureChanged() => CultureChanged?.Invoke(null, EventArgs.Empty);
}
```
При смене языка: устанавливается `Thread.CurrentThread.CurrentUICulture`, затем `NotifyCultureChanged()`. Строки в XAML через `{x:Static res:Strings.SomeKey}` пересчитываются через `StringResourceConverter`.

---

## 11. SECURITY — VAULT

| Параметр | Значение |
|---|---|
| Алгоритм | Windows DPAPI (`ProtectedData.Protect/Unprotect`) |
| Область | `DataProtectionScope.CurrentUser` |
| Энтропия | `byte[] { 0x41,0x52,0x4B,0x2D,0x56,0x61,0x75,0x6C,0x74 }` = `"ARK-Vault"` |
| Хранение | Base64-строка в JSON (`EncryptedApiKey`, `EncryptedObsPassword`, `EncryptedTwitchOAuth`) |
| Правило | ХРАНЕНИЕ СЕКРЕТОВ В ОТКРЫТОМ ВИДЕ В JSON ЗАПРЕЩЕНО |

---

## 12. MSBuild АВТОМАТИЗАЦИЯ (ARK.UI.csproj)

### KillRunningProcess (BeforeTargets="BeforeBuild")
Убивает запущенный `ARK.UI.exe` перед сборкой — снимает lock на exe-файл.
```powershell
$p = Get-Process ARK.UI -EA Ignore
if ($p) { $p | Stop-Process -Force -EA Ignore; $p | ForEach-Object { $_.WaitForExit(3000) } }
exit 0
```

### CopyWhisperNatives (AfterTargets="Build")
Копирует нативные DLL Whisper из `runtimes/*/native/` в корень output:
1. CPU: `runtimes/win-x64/native/*.dll`
2. CUDA: `runtimes/cuda12/win-x64/*.dll` (перезаписывает CPU-версии → GPU-приоритет)
