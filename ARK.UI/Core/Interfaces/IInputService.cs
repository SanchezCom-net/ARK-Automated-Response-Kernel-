using ARK.UI.Core.Input;
using WpfPoint = System.Windows.Point;

namespace ARK.UI.Core.Interfaces;

public interface IInputService
{
    event EventHandler<WpfPoint>?                MouseMoved;
    event EventHandler<MouseButtonHookEventArgs>? MouseLeftButtonPressed;
    event EventHandler<MouseButtonHookEventArgs>? MouseRightButtonPressed;
    event EventHandler<KeyHookEventArgs>?         KeyDown;
    event EventHandler<KeyHookEventArgs>?         KeyUp;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task StartGlobalHooksAsync(CancellationToken cancellationToken = default);
    Task StopGlobalHooksAsync(CancellationToken cancellationToken = default);
}
