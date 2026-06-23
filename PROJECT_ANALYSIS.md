# PROJECT_ANALYSIS.md — ARK (Automated Response Kernel)
> Дата: 2026-06-23 | Последняя задача: BASENODE_V3_STANDARD.md compliance — полное приведение к стандарту

---

## V3 Event-Data Bus Migration — Итоговый отчёт

### Цель
Устранить все критические несоответствия стандарту V3, выявленные в ходе архитектурного аудита. Перевести кодовую базу на чистую шину данных без V2-рефлексии.

---

## ШАГ 1: DataBusPacket.cs — Полная перепись (ВЫПОЛНЕНО)

### Изменения
- **Удалено:** поле `Payload` (данные больше НЕ переносятся внутри пакета)
- **Добавлено:** `Timestamp`, `SourceNodeId`, `Status` (enum `PacketStatus { Success, Error, Warning }`)
- **Сигнатуры фабрик обновлены:**
  - `Text(Guid sessionId, Guid sourceNodeId = default, Dictionary<string,string>? meta = null)` — без строки
  - `Object(Guid sessionId, PortDataType type, Guid sourceNodeId = default)` — без значения
  - Добавлен `Failure(Guid sessionId, Guid sourceNodeId = default)`

### Влияние на V3
Пакет теперь — **паспорт** (только SessionId+DataId+метаданные). Данные хранятся строго в `IDataBus`.

---

## ШАГ 2: NodeEngine.cs — Удаление V2 Reflection (ВЫПОЛНЕНО)

### Изменения
- **Удалено:** `using System.Reflection;`
- **Удалён блок** (~40 строк): V2-доставка данных через `prop.SetValue` + `context.Variables["In:{id}:{prop}"]`
- **Изменено:** Watchdog при таймауте теперь выставляет `NodeState.Zombie` (не `Error`) и пишет в BlackBox лог с тегом `[WATCHDOG ZOMBIE]`

### Влияние на V3
Нарушение § «Архитектура V3 (Unified Bus)» полностью устранено. Reflection из исполнения нод удалён.

---

## ШАГ 3: BaseNode.cs — Pre-flight, Pass-through, Smart Fields (ВЫПОЛНЕНО)

### Добавленные члены
```csharp
protected virtual bool AutoValidatesSession => true;
protected virtual bool SupportsDataType(PortDataType type) => true;
public Dictionary<string, string> FieldMetadataMapping { get; set; } = new();
protected bool TryGetMappedMetadata(string propertyName, DataBusPacket? packet, out string mappedValue);
public string LastBlackBoxMessage { get; private set; }
protected virtual NodeResult HandleError(Exception ex);
```

### Изменения в ExecuteAsync
1. **Pre-flight (Fail-Fast Sessioning):** до вызова `ExecuteCoreAsync` проверяется SessionId. При несовпадении — нода переходит в `Failed`, пишет в BlackBox, возвращает `NodeResult.Failure("ExpiredSession")`.
2. **Transparent Pass-through:** если нода не поддерживает тип входящего пакета (`!SupportsDataType`), пакет прозрачно уходит в выходной порт без выполнения логики. Состояние → `Success`.
3. **LastOutputValue:** после выполнения значение берётся из `dataBus.Get(...)` по ключу выходного пакета (не из несуществующего `Payload`).
4. **HandleError (§2.2):** catch-блок делегирует в `HandleError(ex)` — изолированный обработчик, логирует `[CRITICAL]` в BlackBox и возвращает `NodeResult.Failure`. Потомки могут переопределить поведение при ошибке.
5. **LastBlackBoxMessage:** каждый вызов `LogToBlackBox` сохраняет сообщение — NodeEngine читает его для маршрутизации по IsBlackBoxRoute-проводу.

### Удалено
- `catch (SessionMismatchException)` — обработка перенесена в inline Pre-flight.
- V2-метод `TryApplyContextInput<T>` — полностью удалён.

---

## ШАГ 4: Logic_SynchronizerNode.cs (ВЫПОЛНЕНО)

- `protected override bool AutoValidatesSession => !AllowCrossSession;` — нода оптирует из автоматической проверки при режиме cross-session.
- `BuildSuccess`: `DataBusPacket.Object(sessionId, PortDataType.Object)` + `DataBus?.Set(output.SessionId, output.DataId, snapshot)`.

---

## ШАГ 5: EventMonitor.cs — IDataBus инъекция (ВЫПОЛНЕНО)

- Добавлена `IDataBus? _dataBus` (optional DI через конструктор).
- При срабатывании речевого триггера: `DataBusPacket.Text(Guid.NewGuid(), meta: ...)` + `_dataBus?.Set(...)` для доставки `rawText` в шину.

---

## Массовая правка нод (~22 файла) (ВЫПОЛНЕНО)

### Паттерн изменений
1. Удалён `else if (inputPacket?.Payload is string _pl) Property = _pl;` — V2-fallback через Payload.
2. Обновлены вызовы `DataBusPacket.Text(_sid, value)` → `DataBusPacket.Text(_sid)` (строка больше не передаётся в фабрику).

| Файл | Payload fallback | Text factory |
|------|-----------------|--------------|
| ClipboardNode.cs | удалено | `Text(_sid)` |
| HotkeyTriggerNode.cs | удалён | ×2 `Text(_sid)` |
| DelayNode.cs | удалён | `Text(_sid)` |
| Logic_BranchNode.cs | удалён | — |
| Logic_SynchronizerNode.cs | — | `Object(sid, type)` |
| MouseActionNode.cs | удалён | `Text(_sid)` |
| OverlayTextNode.cs | удалён | — |
| RunProcessNode.cs | удалён | `Text(_sid)` |
| SendInputNode.cs | удалён | `Text(_sid)` |
| SpeechTriggerNode.cs | удалён | ×3 `Text(_sid)` |
| TextWriteNode.cs | удалён | — |
| TextConditionNode.cs | удалён | ×2 `Text(_sid)` |
| Vision_OcrNode.cs | удалён | `Text(_sid)` |
| OBS_DynamicContentManagerNode.cs | удалён | — |
| Web_RequestNode.cs | удалён | `Text(_sid)` |
| Win_AudioDeviceNode.cs | удалён | — |
| Win_PowerShellNode.cs | удалён | `Text(_sid)` |
| Win_ProcessManagerNode.cs | удалён | `Text(_sid)` |
| Win_SpeakTextNode.cs | удалён | — |
| Win_WindowManagerNode.cs | удалён | `Text(_sid)` |
| Win_TranslateNode.cs | удалён | `Text(_sid)` |
| EventMonitor.cs | — | `Text(sid, meta:...)` |

---

## BASENODE_V3_STANDARD.md Compliance (ВЫПОЛНЕНО 2026-06-23)

### Исправленные дефекты

#### Дефект 1: NodeTimeoutMs = 0 (§2.5.1.1)
**Было:** `int timeoutMs = node.NodeTimeoutMs > 0 ? node.NodeTimeoutMs : 30_000;`  
**Стало:** `int timeoutMs = node.NodeTimeoutMs;` + проверка `timeoutMs == 0 || elapsed <= timeoutMs`  
**Стандарт:** «NodeTimeoutMs (0 = бесконечность)» — при 0 Watchdog по таймауту не срабатывает.

#### Дефект 2: CSP Soft Timeout → NodeState.Error (§CSP 1.2.3.2)
**Было:** `node.SetState(NodeState.Zombie)` при превышении Soft Timeout Critical Section  
**Стало:** `node.SetState(NodeState.Error)` с сообщением `"Critical Section Soft Timeout exceeded: Context cascade canceled"`  
**Стандарт:** «переводя её в состояние ERROR с отписью в Black Box Log: Critical Section Soft Timeout exceeded: Context cascade canceled»

Применено к двум путям:
- Пользователь нажал Stop (catch OperationCanceledException)
- Watchdog-таймаут для Critical Section

#### Дефект 3: Разделение путей Watchdog (§HEARTBEAT 1.3 vs §CSP 1.2.3.2)
**Было:** единый путь `NodeState.Zombie` для всех нод после таймаута  
**Стало:**
- Critical Section timeout → `NodeState.Error` + `"Critical Section Soft Timeout exceeded: Context cascade canceled"`
- Обычная нода timeout → `NodeState.Zombie` + `[WATCHDOG ZOMBIE]`

#### Дефект 4: BlackBox Dual-Mode маршрутизация (§1.2.5.2)
**Было:** BlackBox порт — только визуальный, IsBlackBoxRoute не обрабатывался движком  
**Стало:**
- `BaseNode.LastBlackBoxMessage` — захватывает каждое сообщение BlackBox
- `BaseNode.HandleError()` — изолированный virtual-метод обработки ошибок
- `NodeEngine.ExecuteBranchAsync` — после выполнения ноды: если `LastBlackBoxMessage != ""` и есть IsBlackBoxRoute-провод → создаётся `DataBusPacket.Text`, текст лога кладётся в DataBus, нода-приёмник запускается
- `GetConnectedNodeIds` — исключает IsBlackBoxRoute из стандартных маршрутов

#### Дефект 5: Smart Fields V3.6 (§3 Drag-and-Drop Mapping)
**Было:** `TryGetMappedMetadata` объявлен, нигде не применялся  
**Стало:**
- `TryGetMappedMetadata` расширен проверкой `MetadataSubscriptions` (§1.4.1.5: фильтр по реестру подписок)
- Применён в `DelayNode` (DelayMilliseconds, MinDelayMs, MaxDelayMs)
- Применён в `OverlayTextNode` (Text, DurationMilliseconds)
- Применён в `KeyPressNode` (Key через Enum.TryParse, Modifiers через Enum.TryParse)

---

## Результат сборки

```
Сборка успешно завершена.
    Предупреждений: 0
    Ошибок: 0
Прошло времени: 00:00:10.88
```

---

## Полная таблица соответствия стандарту (BASENODE_V3_STANDARD.md)

| Раздел стандарта | Пункт | Статус |
|---|---|---|
| §1.1.1 | Trigger In | ✅ РЕАЛИЗОВАНО |
| §1.1.2 | Data In (DataBusPacket без контента) | ✅ РЕАЛИЗОВАНО |
| §1.2.1 | Success Trigger | ✅ РЕАЛИЗОВАНО |
| §1.2.2 | Data Out | ✅ РЕАЛИЗОВАНО |
| §1.2.3 | Custom Data Out | ✅ РЕАЛИЗОВАНО |
| §1.2.4 | Error/Rejected Trigger | ✅ РЕАЛИЗОВАНО |
| §1.2.5.1 | BlackBox фоновый режим (Channel\<T\>.TryWrite) | ✅ РЕАЛИЗОВАНО |
| §1.2.5.2 | BlackBox визуальный режим (IsBlackBoxRoute маршрутизация) | ✅ РЕАЛИЗОВАНО |
| §1.4.1 | DataBusPacket: SessionID, DataID, Timestamp, SourceNodeID, Status | ✅ РЕАЛИЗОВАНО |
| §1.4.1.3 | Metadata объект (FrozenDictionary) | ✅ РЕАЛИЗОВАНО |
| §1.4.1.4 | Типизированные теги {Type:Value} (соглашение) | ✅ РЕАЛИЗОВАНО |
| §1.4.1.5 | MetadataSubscriptions — реестр подписок | ✅ РЕАЛИЗОВАНО |
| §2.1 | Transparent Pass-through | ✅ РЕАЛИЗОВАНО |
| §2.2 | HandleError() → BlackBox + Error Trigger | ✅ РЕАЛИЗОВАНО |
| §2.5.1.1 | NodeTimeoutMs = 0 → бесконечность | ✅ РЕАЛИЗОВАНО |
| §2.5.1.2 | ResetWatchdogTimer() в долгих нодах | ✅ РЕАЛИЗОВАНО |
| §V3.4 §2 | Pre-flight Session Validation (Fail-Fast) | ✅ РЕАЛИЗОВАНО |
| §V3.4 §3 | Изоляция по SessionID в DataBus | ✅ РЕАЛИЗОВАНО |
| §V3.6 §3 | Smart Fields: приоритет метаданных над UI | ✅ РЕАЛИЗОВАНО |
| §V3.7 §1 | DataOut / Custom DataOut разделение магистралей | ✅ РЕАЛИЗОВАНО |
| §Lifecycle §1.1-1.5 | NodeState: IDLE/PROCESSING/WAITING/ERROR/ZOMBIE | ✅ РЕАЛИЗОВАНО |
| §CSP §1.1 | IsCriticalSection тумблер | ✅ РЕАЛИЗОВАНО |
| §CSP §1.2.3.1 | Soft Timeout 5-10 сек | ✅ РЕАЛИЗОВАНО |
| §CSP §1.2.3.2 | Exceeded → NodeState.Error + точное сообщение | ✅ РЕАЛИЗОВАНО |
| §HEARTBEAT §1.3 | Обычный Watchdog timeout → NodeState.Zombie | ✅ РЕАЛИЗОВАНО |
| §Port Rules §1.1 | Типизация портов (SIGNAL/TEXT/IMAGE/OBJECT/ANY) | ✅ РЕАЛИЗОВАНО |
