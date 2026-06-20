using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
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
public abstract class BaseNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal void RaisePropertyChanged(string propName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

    internal void ResetToPending() => State = NodeState.Pending;

    // ── Identity ──────────────────────────────────────────────────────────

    public Guid Id { get; init; } = Guid.NewGuid();

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    // ── Состояние выполнения (только runtime, не сериализуется) ──────────

    private NodeState _state = NodeState.Pending;

    [JsonIgnore]
    public NodeState State
    {
        get => _state;
        protected set { if (_state != value) { _state = value; OnPropertyChanged(); } }
    }

    // ── Граф ──────────────────────────────────────────────────────────────

    public Guid? OnSuccessNodeId { get; set; }
    public Guid? OnErrorNodeId   { get; set; }

    // 0 = параллельное выполнение (по умолчанию); >0 = очередь по приоритету
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

    // ── Метаданные безопасности (переопределяются в опасных нодах) ────────

    [JsonIgnore] public virtual bool   IsDangerous       => false;
    [JsonIgnore] public virtual string DangerWarningText => string.Empty;

    // Нода не может быть удалена пользователем (например, TriggerRootNode).
    [JsonIgnore] public virtual bool   IsRemovable        => true;

    // Первичное имя свойства, принимающего данные по серебряному проводу.
    // ConnectNodes в ViewModel записывает это значение в DataOutputPropertyName источника.
    // Ноды-приёмники обязаны переопределить это свойство через nameof(TheirPrimaryProperty).
    [JsonIgnore] public virtual string DefaultDataInputPropertyName => string.Empty;

    // ── Метрики карточки (используются VisualNode для расчёта центров портов) ─
    [JsonIgnore] public virtual int CardBodyWidth    { get; } = 160;
    [JsonIgnore] public virtual int InPortYCenter    { get; } = 27;
    // Y-смещение центра выходного Success-порта от верхней границы VisualNode.
    // По умолчанию 11 (верхний порт стандартного стека из 3 портов в карточке 54px).
    // Переопределяется в нодах с нестандартной высотой карточки (например, TriggerRootNode).
    [JsonIgnore] public virtual int SuccessPortYCenter { get; } = 11;

    // ── Контекст выполнения (общая шина данных макроса) ──────────────────

    [JsonIgnore]
    protected MacroExecutionContext? CurrentContext { get; private set; }

    // Делегат отладочного вывода — прокидывается из NodeEngine перед ExecuteAsync.
    // AppendDebugLog во ViewModel уже обёрнут в Dispatcher.InvokeAsync → потокобезопасен.
    [JsonIgnore]
    public Action<string>? DebugSink { get; set; }

    protected bool TryApplyContextInput<T>(string propertyName, Action<T> setter)
    {
        var key = $"In:{Id}:{propertyName}";
        if (CurrentContext?.Variables.TryGetValue(key, out var val) != true || val is null)
            return false;

        // ── Прозрачная распаковка DataPacket ──────────────────────────────
        // Если T == DataPacket → отдаём пакет напрямую.
        // Иначе → извлекаем Payload и пускаем его через стандартный каскад кастинга.
        if (val is Models.DataPacket packet)
        {
            if (packet is T typedPacket)
            {
                setter(typedPacket);
                RaisePropertyChanged(propertyName);
                DebugSink?.Invoke(
                    $"[ВХОД] DataPacket получен в '{propertyName}': " +
                    $"тип={packet.Type}, payload={packet.Payload?.GetType().Name}");
                return true;
            }
            val = packet.Payload;
            if (val is null) return false;
        }

        // ── Каскад кастинга примитивов ─────────────────────────────────────
        T?   coerced = default;
        bool ok      = false;

        if (val is T exact)
            (coerced, ok) = (exact, true);
        else if (typeof(T) == typeof(string) && val.ToString() is T str)
            (coerced, ok) = (str, true);
        else if (typeof(T) == typeof(int) && val is string si
                 && int.TryParse(si, out int iv) && iv is T ti)
            (coerced, ok) = (ti, true);
        else if (typeof(T) == typeof(double) && val is string sd
                 && double.TryParse(sd,
                     System.Globalization.NumberStyles.Any,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out double dv) && dv is T td)
            (coerced, ok) = (td, true);
        else if (val is IConvertible conv)
        {
            try
            {
                coerced = (T)Convert.ChangeType(conv, typeof(T),
                    System.Globalization.CultureInfo.InvariantCulture);
                ok = true;
            }
            catch { /* несовместимые типы */ }
        }

        if (ok)
        {
            setter(coerced!);
            RaisePropertyChanged(propertyName);
            DebugSink?.Invoke(
                $"[ВХОД] Свойство '{propertyName}' успешно получило значение: \"{val}\" " +
                $"(тип: {val.GetType().Name} → {typeof(T).Name})");
            return true;
        }

        DebugSink?.Invoke(
            $"[ВХОД] ⚠ Не удалось привязать свойство '{propertyName}': " +
            $"значение \"{val}\" не может быть приведено к типу {typeof(T).Name}. " +
            $"Использован статический дефолт.");
        return false;
    }

    // ── Выполнение ────────────────────────────────────────────────────────

    protected abstract Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken);

    public async Task<bool> ExecuteAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        MacroExecutionContext context,
        CancellationToken cancellationToken)
    {
        CurrentContext = context;
        State = NodeState.Executing;
        try
        {
            bool result = await ExecuteCoreAsync(serviceProvider, logger, cancellationToken)
                .ConfigureAwait(false);
            State = result ? NodeState.Success : NodeState.Failed;
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
            return false;
        }
    }
}
