using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes.OBS;

namespace ARK.UI.Core.Nodes;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(DelayNode),         "delay")]
[JsonDerivedType(typeof(OverlayTextNode),   "overlay")]
[JsonDerivedType(typeof(HotkeyTriggerNode), "hotkey")]
[JsonDerivedType(typeof(MouseClickNode),    "mouse_click")]
[JsonDerivedType(typeof(KeyPressNode),      "key_press")]
[JsonDerivedType(typeof(TextWriteNode),     "text_write")]
[JsonDerivedType(typeof(ColorSearchNode),   "color_search")]
[JsonDerivedType(typeof(TemplateMatchNode), "template_match")]
[JsonDerivedType(typeof(NetworkStatusNode), "network_status")]
[JsonDerivedType(typeof(SendInputNode),    "send_input")]
[JsonDerivedType(typeof(MouseActionNode),     "mouse_action")]
[JsonDerivedType(typeof(RunProcessNode),      "run_process")]
[JsonDerivedType(typeof(ClipboardNode),         "clipboard")]
[JsonDerivedType(typeof(TextConditionNode),   "text_condition")]
[JsonDerivedType(typeof(ObsSetSceneNode),      "obs_set_scene")]
[JsonDerivedType(typeof(ObsToggleMuteNode),    "obs_toggle_mute")]
[JsonDerivedType(typeof(ObsRecordControlNode), "obs_record_control")]
[JsonDerivedType(typeof(OBS_SceneManagerNode),              "obs_scene_manager")]
[JsonDerivedType(typeof(OBS_SourceVisibilityManagerNode),   "obs_source_visibility_manager")]
[JsonDerivedType(typeof(OBS_AudioManagerNode),              "obs_audio_manager")]
[JsonDerivedType(typeof(OBS_StreamAndRecordManagerNode),    "obs_stream_record_manager")]
[JsonDerivedType(typeof(OBS_DynamicContentManagerNode),     "obs_dynamic_content_manager")]
[JsonDerivedType(typeof(Win_SpeakTextNode),                 "win_speak_text")]
[JsonDerivedType(typeof(Win_ProcessManagerNode),            "win_process_manager")]
[JsonDerivedType(typeof(Win_WindowManagerNode),             "win_window_manager")]
[JsonDerivedType(typeof(Win_SystemPowerNode),               "win_system_power")]
[JsonDerivedType(typeof(Win_PowerShellNode),                "win_powershell")]
[JsonDerivedType(typeof(Win_AudioDeviceNode),               "win_audio_device")]
[JsonDerivedType(typeof(Wait_SmartDelayNode),               "wait_smart_delay")]
[JsonDerivedType(typeof(Logic_CounterNode),                 "logic_counter")]
[JsonDerivedType(typeof(Logic_BranchNode),                 "logic_branch")]
[JsonDerivedType(typeof(SpeechTriggerNode),                "speech_trigger")]
[JsonDerivedType(typeof(Web_RequestNode),                  "web_request")]
[JsonDerivedType(typeof(Vision_OcrNode),                   "vision_ocr")]
[JsonDerivedType(typeof(Win_TranslateNode),                 "win_translate")]
[JsonDerivedType(typeof(Logic_SequenceNode),               "logic_sequence")]
[JsonDerivedType(typeof(Logic_QueueBlockNode),             "logic_queue_block")]
[JsonDerivedType(typeof(Win_BypassQueueNode),              "bypass_queue")]
[JsonDerivedType(typeof(TriggerRootNode),                  "trigger_root")]
[JsonDerivedType(typeof(MacroPolicyNode),                  "macro_policy")]
[JsonDerivedType(typeof(MacroResetNode),                   "macro_reset")]
[JsonDerivedType(typeof(Logic_SynchronizerNode),           "logic_synchronizer")]
public abstract class BaseNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal void RaisePropertyChanged(string propName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

    internal void ResetToPending()
    {
        State             = NodeState.Pending;
        _currentSessionId = Guid.Empty; // сброс сессии перед новым запуском
    }

    // Сброс ноды в исходное состояние по сигналу MacroResetNode (Macro Reset Protocol)
    public virtual void ResetToDefault()
    {
        State             = NodeState.Pending;
        IsListening       = false;
        _currentSessionId = Guid.Empty;
    }

    // ── Identity ──────────────────────────────────────────────────────────

    public Guid Id { get; init; } = Guid.NewGuid();

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    // ── V3 Настройки ноды ────────────────────────────────────────────────

    // 0 = использовать глобальный таймаут NodeEngine (30 сек)
    public int NodeTimeoutMs { get; set; } = 0;

    // Критическая секция: при отмене ноде выделяется Soft Timeout (5-10 сек) на безопасную остановку
    public bool IsCriticalSection { get; set; } = false;

    // Теги метаданных из DataBusPacket.Metadata, на которые подписана эта нода
    public List<string> MetadataSubscriptions { get; set; } = [];

    // ── Smart Fields V3.6: Drag-and-Drop Mapping ─────────────────────────────
    // Ключ: имя свойства ноды (например "DelayMilliseconds")
    // Значение: тег метаданных из шины (например "{Sys:CPU_Load}")
    public Dictionary<string, string> FieldMetadataMapping { get; set; } = new();

    // ── Состояние выполнения (только runtime, не сериализуется) ──────────

    private NodeState _state = NodeState.Pending;

    [JsonIgnore]
    public NodeState State
    {
        get => _state;
        protected internal set => SetState(value);
    }

    /// <summary>
    /// Потокобезопасная установка состояния выполнения.
    /// PropertyChanged маршализуется в UI-поток через Dispatcher.BeginInvoke,
    /// поскольку NodeEngine работает в фоновом Task (Task.Run).
    /// </summary>
    public void SetState(NodeState newState)
    {
        if (_state == newState) return;
        _state = newState;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            RaisePropertyChanged(nameof(State));
        else
            dispatcher.BeginInvoke(DispatcherPriority.Normal,
                (System.Action)(() => RaisePropertyChanged(nameof(State))));
    }

    // ── Граф ──────────────────────────────────────────────────────────────

    public Guid? OnSuccessNodeId { get; set; }
    public Guid? OnErrorNodeId   { get; set; }

    public int QueuePriority { get; set; } = 0;

    // ── Канал данных (Data Flow) ──────────────────────────────────────────

    public Guid?  DataOutputNodeId       { get; set; }
    public string DataOutputPropertyName { get; set; } = string.Empty;

    private bool _isDataOutputEnabled = true;
    public bool IsDataOutputEnabled
    {
        get => _isDataOutputEnabled;
        set { if (_isDataOutputEnabled != value) { _isDataOutputEnabled = value; OnPropertyChanged(); } }
    }

    [JsonIgnore]
    public object? LastOutputValue { get; protected set; }

    // ── Метаданные безопасности ───────────────────────────────────────────

    [JsonIgnore] public virtual bool   IsDangerous        => false;
    [JsonIgnore] public virtual string DangerWarningText  => string.Empty;
    [JsonIgnore] public virtual bool   IsRemovable        => true;
    [JsonIgnore] public virtual string DefaultDataInputPropertyName => string.Empty;

    // Пассивная нода: не имеет портов ввода/вывода, служит только конфигуратором
    [JsonIgnore] public virtual bool IsPassive => false;

    // false — кнопки «Тест ноды» / «Тест цепочки» в редакторе скрыты (нода не исполняема интерактивно)
    [JsonIgnore] public virtual bool IsTestable => true;

    // Переопределите, чтобы указать какие типы данных нода умеет обрабатывать.
    // Если входящий тип не поддерживается, BaseNode.ExecuteAsync автоматически
    // пробросит пакет дальше без изменений (Transparent Pass-through).
    protected virtual bool SupportsDataType(PortDataType type) => true;

    // Переопределите на false, если нода намеренно накапливает пакеты из разных сессий
    // (например, Logic_SynchronizerNode с AllowCrossSession = true).
    protected virtual bool AutoValidatesSession => true;

    // ── V3 Event-Driven: Регистрация триггера ─────────────────────────────

    // Выставляется движком через RegisterTriggersAsync() — нода реагирует на внешние события
    [JsonIgnore]
    public bool IsListening { get; set; } = false;

    /// <summary>
    /// Вызывается NodeEngine.RegisterTriggersAsync при активации макроса.
    /// Переопределяется триггерными нодами для подписки на системные хуки.
    /// </summary>
    public virtual Task OnStartListeningAsync(CancellationToken ct) => Task.CompletedTask;

    // ── Метрики карточки ──────────────────────────────────────────────────
    [JsonIgnore] public virtual int CardBodyWidth      { get; } = 160;
    [JsonIgnore] public virtual int InPortYCenter      { get; } = 27;   // Trigger In  (Y+27, 14px port, Margin top=20)
    [JsonIgnore] public virtual int DataInPortYCenter  { get; } = 43;   // Data In     (Y+43, 14px port, Margin top=36)
    [JsonIgnore] public virtual int SuccessPortYCenter { get; } = 12;   // Success Out (Y+12, 14px port, StackPanel top 5)

    // ── Системные зависимости (инжектируются из NodeEngine перед вызовом ExecuteAsync) ──

    [JsonIgnore] protected ILogService?       NodeLogger   { get; private set; }
    [JsonIgnore] protected IServiceProvider?  NodeServices { get; private set; }
    [JsonIgnore] protected IBlackBoxLogger?   BlackBox     { get; private set; }
    [JsonIgnore] protected IDataBus?          DataBus      { get; private set; }

    // Делегат отладочного вывода
    [JsonIgnore] public Action<string>? DebugSink { get; set; }

    // ── Watchdog (Heartbeat) ──────────────────────────────────────────────

    private readonly Stopwatch _watchdog = new();

    // Абсолютная метка времени последнего сброса — используется NodeEngine для анализа ZOMBIE-состояния
    [JsonIgnore]
    public DateTime LastWatchdogReset { get; private set; }

    // Нода вызывает этот метод внутри долгих циклов, чтобы сообщить «я живая».
    public void ResetWatchdogTimer()
    {
        _watchdog.Restart();
        LastWatchdogReset = DateTime.UtcNow;
    }

    internal TimeSpan WatchdogElapsed => _watchdog.Elapsed;

    // ── BlackBox телеметрия ───────────────────────────────────────────────

    // Последнее сообщение в BlackBox — NodeEngine читает это для маршрутизации по IsBlackBoxRoute
    [JsonIgnore]
    public string LastBlackBoxMessage { get; private set; } = string.Empty;

    protected void LogToBlackBox(string message, Exception? ex = null)
    {
        LastBlackBoxMessage = message;
        BlackBox?.Log(Id, message, ex);
    }

    /// <summary>
    /// V3 Dual-Mode: изолированный обработчик ошибок ноды.
    /// Логирует в BlackBox с тегом [CRITICAL] и возвращает Failure-результат.
    /// NodeEngine дополнительно маршрутизирует LastBlackBoxMessage через IsBlackBoxRoute-провод.
    /// </summary>
    protected virtual NodeResult HandleError(Exception ex)
    {
        LogToBlackBox($"[CRITICAL] {ex.Message}", ex);
        return NodeResult.Failure(ex.Message);
    }

    // ── Session validation (Fail-Fast) ───────────────────────────────────

    private Guid _currentSessionId;

    protected void ValidateSession(DataBusPacket packet)
    {
        if (_currentSessionId == Guid.Empty)
            _currentSessionId = packet.SessionId;

        if (packet.SessionId != _currentSessionId)
            throw new SessionMismatchException(_currentSessionId, packet.SessionId);
    }

    // ── Lifecycle States ─────────────────────────────────────────────────

    // IDLE → PROCESSING → (WAITING | ERROR) → IDLE
    // ZOMBIE детектируется NodeEngine через Heartbeat Ping/Pong (Watchdog)

    // ── V3 Execution Interface ────────────────────────────────────────────

    protected abstract Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct);

    public async Task<NodeResult> ExecuteAsync(
        IServiceProvider   serviceProvider,
        ILogService        logger,
        IBlackBoxLogger?   blackBox,
        IDataBus?          dataBus,
        DataBusPacket?     inputPacket,
        CancellationToken  ct)
    {
        NodeLogger   = logger;
        NodeServices = serviceProvider;
        BlackBox     = blackBox;
        DataBus      = dataBus;

        _watchdog.Restart();
        LastWatchdogReset = DateTime.UtcNow;
        State = NodeState.Executing;

        // ── Pre-flight: автоматическая проверка сессии (Fail-Fast Sessioning) ──
        if (AutoValidatesSession && inputPacket is not null)
        {
            if (_currentSessionId != Guid.Empty && inputPacket.SessionId != _currentSessionId)
            {
                State = NodeState.Failed;
                LogToBlackBox(
                    $"[PRE-FLIGHT] ExpiredSession: ожидался={_currentSessionId}, получен={inputPacket.SessionId}");
                return NodeResult.Failure("ExpiredSession");
            }
            _currentSessionId = inputPacket.SessionId;
        }

        // ── Transparent Pass-through ────────────────────────────────────────────
        if (inputPacket is { Type: not PortDataType.Signal and not PortDataType.ResetRequest }
            && !SupportsDataType(inputPacket.Type))
        {
            LogToBlackBox(
                $"[PASS-THROUGH] Тип {inputPacket.Type} не поддерживается нодой '{Name}'. " +
                "Пакет прозрачно передаётся дальше без изменений.");
            State = NodeState.Success;
            return NodeResult.Success(inputPacket);
        }

        try
        {
            var result = await ExecuteCoreAsync(inputPacket, ct).ConfigureAwait(false);

            State = result.IsSuccess ? NodeState.Success : NodeState.Failed;

            // V3: LastOutputValue берём из DataBus по ключу выходного пакета (не из Payload)
            if (result.OutputPacket is not null && dataBus is not null)
                LastOutputValue = dataBus.Get(result.OutputPacket.SessionId, result.OutputPacket.DataId);

            return result;
        }
        catch (OperationCanceledException)
        {
            State = NodeState.Failed;
            throw;
        }
        catch (Exception ex)
        {
            State = NodeState.Failed;
            await logger.LogErrorAsync(Name, $"Нода '{Name}' завершилась с ошибкой.", ex)
                .ConfigureAwait(false);
            return HandleError(ex);
        }
        finally
        {
            _watchdog.Stop();
        }
    }

    // ── Smart Fields V3.6: хелпер для маппинга полей на теги метаданных ──────

    /// <summary>
    /// Проверяет, есть ли в <see cref="FieldMetadataMapping"/> привязка для указанного свойства,
    /// и если да — ищет значение тега в метаданных входящего пакета.
    /// Приоритет: тег метаданных ВЫШЕ статичного значения из UI.
    /// </summary>
    /// <param name="propertyName">Имя свойства ноды (например "DelayMilliseconds").</param>
    /// <param name="packet">Входящий DataBusPacket (может быть null).</param>
    /// <param name="mappedValue">Найденное строковое значение тега, или empty при неудаче.</param>
    /// <returns>true — значение найдено и нужно применить его вместо UI-значения.</returns>
    /// <summary>
    /// V3 Smart Fields: приоритет метаданных шины ВЫШЕ статичного UI-значения.
    /// Порядок проверки:
    ///   1. FieldMetadataMapping содержит привязку propertyName → тег
    ///   2. Если MetadataSubscriptions непуст — тег должен входить в список подписок
    ///   3. Тег найден в Metadata входящего пакета
    /// </summary>
    protected bool TryGetMappedMetadata(string propertyName, DataBusPacket? packet, out string mappedValue)
    {
        mappedValue = string.Empty;
        if (packet is null) return false;
        if (!FieldMetadataMapping.TryGetValue(propertyName, out var tag)) return false;
        // Если список подписок задан — тег обязан в него входить (фильтр безопасности)
        if (MetadataSubscriptions.Count > 0 && !MetadataSubscriptions.Contains(tag)) return false;
        if (!packet.Metadata.TryGetValue(tag, out var val)) return false;
        mappedValue = val;
        return true;
    }

    // ── V2 Legacy Context (только для MacroOrchestrator / SpeechTrigger пути) ──

    [JsonIgnore]
    protected Models.MacroExecutionContext? CurrentContext { get; private set; }

    internal void SetLegacyContext(Models.MacroExecutionContext ctx) => CurrentContext = ctx;
}
