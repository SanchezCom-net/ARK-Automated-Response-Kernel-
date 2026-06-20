namespace ARK.UI.Core.Interfaces;

public enum TriggerState { Idle, Active }

public interface ITriggerService : IDisposable
{
    TriggerState State    { get; }
    bool         IsActive { get; }

    /// <summary>
    /// Анализирует распознанный текст на наличие слов активации (поиск целого слова).
    /// При совпадении: переводит систему в Active, сбрасывает таймер истечения, логирует.
    /// Возвращает true если система находится или переходит в состояние Active.
    /// Возвращает false если система в Idle и ключевого слова не найдено — текст игнорируется.
    /// </summary>
    bool Evaluate(string text);

    /// <summary>Ключевое слово, по которому произошла активация (первый найденный match).</summary>
    event System.Action<string>? Activated;

    /// <summary>Активный период истёк — система вернулась в Idle.</summary>
    event System.Action? Deactivated;
}
