using System.Text.RegularExpressions;

namespace ARK.UI.Core;

/// <summary>
/// Единая точка нормализации текста для голосового матчинга.
/// Приводит к нижнему регистру, удаляет пунктуацию/спецсимволы, схлопывает пробелы.
/// </summary>
public static class TextSanitizer
{
    // Компилируется один раз: схлопывание последовательных пробелов
    private static readonly Regex SpaceCollapseRegex =
        new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Нормализует текст для голосового матчинга:
    /// нижний регистр → оставляет буквы/цифры/пробелы → схлопывает пробелы → trim.
    /// Использует stackalloc для строк до 1024 символов (голосовые команды — всегда короче).
    /// </summary>
    public static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var lower = input.ToLowerInvariant();

        // Pass 1 (zero-allocation): оставляем только буквы, цифры и пробелы
        Span<char> buf = lower.Length <= 1024
            ? stackalloc char[lower.Length]
            : new char[lower.Length];

        int len = 0;
        foreach (char c in lower)
        {
            if (char.IsLetterOrDigit(c))
                buf[len++] = c;
            else if (char.IsWhiteSpace(c))
                buf[len++] = ' '; // нормализуем любой whitespace в пробел
        }

        if (len == 0) return string.Empty;

        // Pass 2: схлопываем множественные пробелы (единственная неизбежная аллокация)
        return SpaceCollapseRegex.Replace(new string(buf[..len]), " ").Trim();
    }
}
