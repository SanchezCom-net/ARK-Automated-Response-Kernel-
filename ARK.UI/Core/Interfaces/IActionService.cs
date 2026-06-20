using System.Windows.Input;

namespace ARK.UI.Core.Interfaces;

public interface IActionService
{
    Task ClickAsync(double x, double y, CancellationToken cancellationToken = default);
    Task RightClickAsync(double x, double y, CancellationToken cancellationToken = default);
    Task DoubleClickAsync(double x, double y, CancellationToken cancellationToken = default);
    Task MoveAsync(double x, double y, CancellationToken cancellationToken = default);
    Task ScrollAsync(double x, double y, int amount, CancellationToken cancellationToken = default);
    Task MouseButtonDownAsync(double x, double y, CancellationToken cancellationToken = default);
    Task MouseButtonUpAsync(double x, double y, CancellationToken cancellationToken = default);
    Task PressKeyAsync(Key key, CancellationToken cancellationToken = default);
    Task PressKeyWithModifiersAsync(Key key, ModifierKeys modifiers, CancellationToken cancellationToken = default);
    Task TypeTextAsync(string text, CancellationToken cancellationToken = default);
}
