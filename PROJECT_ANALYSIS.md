# PROJECT_ANALYSIS.md — ARK (Automated Response Kernel)
> Отчёт о последней выполненной задаче. Обновляется после каждой задачи.
> Дата: 2026-06-17 | Задача: Multi-Name AI Activation + Collision Prevention (AiAssistantNames)

---

## Проблема

1. **Одно имя ИИ**: `AiAssistantName` принимал только одно имя (или делал вид, что поддерживает несколько — но поля не было в UI). Нужна поддержка нескольких форм: "Аркаша, Аркадий".
2. **Нет защиты от коллизий**: Если пользователь задавал одинаковые имена в гейткипере макросов (`ActivationNames`) и имени ИИ (`AiAssistantNames`), система одновременно пыталась запустить макрос И отправить запрос в ИИ.

---

## Изменения

### 1. `ARK.UI/Core/Models/AppConfig.cs`

Переименовано `AiAssistantName` → `AiAssistantNames`, default "Аркаша, Аркадий":

```csharp
// ДО:
public string AiAssistantName { get; set; } = "Арк";

// ПОСЛЕ:
/// <summary>
/// Имена ИИ-ассистента через запятую: "Аркаша, Аркадий".
/// Не должно пересекаться с ActivationNames (гейткипер макросов).
/// </summary>
public string AiAssistantNames { get; set; } = "Аркаша, Аркадий";
```

---

### 2. `ARK.UI/Core/Services/MacroScheduler.cs`

`GetAssistantNames()` обновлён на новое свойство:

```csharp
var raw = _configService.Current.AiAssistantNames;
```

---

### 3. `ARK.UI/ViewModels/SettingsViewModel.cs`

**Новые поле и свойство:**
```csharp
private string _aiAssistantNames = "Аркаша, Аркадий";

public string AiAssistantNames
{
    get => _aiAssistantNames;
    set => SetProperty(ref _aiAssistantNames, value ?? string.Empty);
}
```

**Инициализация в конструкторе:**
```csharp
_aiAssistantNames = string.IsNullOrWhiteSpace(config.AiAssistantNames)
    ? "Аркаша, Аркадий"
    : config.AiAssistantNames;
```

**Метод проверки коллизий `ValidateActivationNames`:**
```csharp
private bool ValidateActivationNames(out string? errorMessage)
{
    // Нечувствительное к регистру сравнение множеств имён
    var gatekeeperNames = ActivationNames.Split(',', ...)
        .Select(n => n.ToLowerInvariant()).ToHashSet();
    var aiNames = AiAssistantNames.Split(',', ...)
        .Select(n => n.ToLowerInvariant()).ToList();
    foreach (var name in aiNames)
        if (gatekeeperNames.Contains(name)) { errorMessage = $"Конфликт: «{name}»"; return false; }
    return true;
}
```

**Интеграция в `OnSaveSettingsAsync`:**
```csharp
// Первое действие в методе — блокирует сохранение при коллизии
if (!ValidateActivationNames(out var validationError))
{
    _ = _logService.LogErrorAsync("SettingsVM", $"[ВАЛИДАЦИЯ] {validationError}");
    MessageBox.Show(validationError, "Конфликт имён активации",
        MessageBoxButton.OK, MessageBoxImage.Warning);
    return;
}
// Далее — сохранение config.AiAssistantNames = AiAssistantNames;
```

**`OpenAiCharacterDialog` обновлён:** передаёт `config.AiAssistantNames`, при подтверждении синхронизирует свойство ViewModel:
```csharp
if (dialog.ResultName is not null)
{
    config.AiAssistantNames = dialog.ResultName;
    AiAssistantNames        = dialog.ResultName; // синхронизация TextBox в панели
}
```

---

### 4. `ARK.UI/Views/AiSettingsControl.xaml`

Добавлен блок в секцию "ИНТЕЛЛЕКТ ИИ" перед кнопкой "Характер ассистента":

```xml
<TextBlock Text="Имена активации ИИ-ассистента (через запятую)"
           Style="{StaticResource FieldLabel}"/>
<TextBox Text="{Binding AiAssistantNames, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
         Tag="Аркаша, Аркадий..."
         Margin="0,0,0,4"
         ToolTip="Укажите имена для ИИ-ассистента. Не должны совпадать с именами голосовых макросов."/>
<TextBlock Text="Не должны совпадать с именами активации макросов"
           FontStyle="Italic" FontSize="9" Foreground="#FF888888" TextWrapping="Wrap"
           Margin="0,0,0,12"/>
```

---

## Логика защиты от коллизий

| Сценарий | Результат |
|---|---|
| `ActivationNames = "Виктория"`, `AiAssistantNames = "Аркаша"` | ✅ Сохранение разрешено |
| `ActivationNames = "Виктория"`, `AiAssistantNames = "Виктория, Аркаша"` | ❌ MessageBox: «Конфликт: «виктория»» — сохранение заблокировано |
| `ActivationNames = "Виктория"`, `AiAssistantNames = "ВИКТОРИЯ"` | ❌ Заблокировано (нечувствительно к регистру) |

---

## Результат сборки

```
Ошибок: 0
Предупреждений: 0
Время: 00:00:14.51
```
