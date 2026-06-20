using System.Text;

namespace ARK.UI.Core.Services;

/// <summary>
/// Потоковый перехватчик XML-команд агента из токенов Ollama (ловит теги «на лету»).
/// Текст вне командных тегов прозрачно пропускается дальше (TTS/оверлей); завершённые
/// теги &lt;click/&gt; &lt;type/&gt; &lt;run/&gt; &lt;write_clipboard/&gt; отдаются на исполнение
/// и НЕ попадают в озвучку и субтитры. Не команда (например, «&lt;b&gt;») — отдаётся как текст.
/// Состояние на один стрим — создавать новый экземпляр на каждый ответ ИИ.
/// </summary>
public sealed class AgentCommandFilter
{
    private static readonly string[] TagNames = ["write_clipboard", "click", "type", "run"];

    // Незакрытый «тег» длиннее лимита — модель сгенерировала мусор, возвращаем как текст
    private const int MaxTagLength = 600;

    private enum State { Text, Deciding, InTag }

    private readonly StringBuilder _visible = new();
    private readonly StringBuilder _pending = new();
    private List<string>? _commands;
    private State _state = State.Text;
    private bool  _inQuotes;

    /// <summary>Обрабатывает токен: видимый текст + полностью закрывшиеся командные теги.</summary>
    public (string Visible, IReadOnlyList<string> Commands) Process(string token)
    {
        _visible.Clear();
        _commands = null;
        foreach (var ch in token)
            ProcessChar(ch);
        return (_visible.ToString(), (IReadOnlyList<string>?)_commands ?? []);
    }

    /// <summary>Хвост стрима: всё удержанное (незавершённый тег) возвращается как обычный текст.</summary>
    public string Flush()
    {
        var tail = _pending.ToString();
        _pending.Clear();
        _state    = State.Text;
        _inQuotes = false;
        return tail;
    }

    private void ProcessChar(char ch)
    {
        switch (_state)
        {
            case State.Text:
                if (ch == '<')
                {
                    _state = State.Deciding;
                    _pending.Append(ch);
                }
                else
                {
                    _visible.Append(ch);
                }
                return;

            case State.Deciding:
                _pending.Append(ch);
                EvaluateDeciding();
                return;

            case State.InTag:
                _pending.Append(ch);
                if (ch == '"')
                {
                    _inQuotes = !_inQuotes;       // '>' внутри значения атрибута — не конец тега
                }
                else if (ch == '>' && !_inQuotes)
                {
                    (_commands ??= []).Add(_pending.ToString());
                    _pending.Clear();
                    _state = State.Text;
                }
                else if (_pending.Length > MaxTagLength)
                {
                    AbortPending();
                }
                return;
        }
    }

    // Решает по накопленному префиксу «<...», является ли он началом командного тега
    private void EvaluateDeciding()
    {
        var candidateLength = _pending.Length - 1;   // без ведущего '<'

        foreach (var tag in TagNames)
        {
            if (candidateLength <= tag.Length)
            {
                // Кандидат короче или равен имени — ждём следующие символы, если префикс совпадает
                if (PendingMatchesPrefix(tag, candidateLength)) return;
            }
            else if (candidateLength == tag.Length + 1 && PendingMatchesPrefix(tag, tag.Length))
            {
                // Имя совпало полностью — следующий символ должен быть разделителем
                var delimiter = _pending[1 + tag.Length];
                if (delimiter is ' ' or '\t' or '\r' or '\n' or '/' or '>')
                {
                    _state = State.InTag;
                    if (delimiter == '>')
                    {
                        (_commands ??= []).Add(_pending.ToString());
                        _pending.Clear();
                        _state = State.Text;
                    }
                    return;
                }
            }
        }

        AbortPending();   // ни один командный тег не подходит — это обычный текст
    }

    private bool PendingMatchesPrefix(string tag, int length)
    {
        for (var i = 0; i < length; i++)
            if (_pending[i + 1] != tag[i]) return false;
        return true;
    }

    // Ложное срабатывание: '<' эмитится как текст, хвост прогоняется через автомат заново
    // (внутри хвоста может начинаться настоящий командный тег)
    private void AbortPending()
    {
        var held = _pending.ToString();
        _pending.Clear();
        _state    = State.Text;
        _inQuotes = false;

        _visible.Append(held[0]);
        for (var i = 1; i < held.Length; i++)
            ProcessChar(held[i]);
    }
}
