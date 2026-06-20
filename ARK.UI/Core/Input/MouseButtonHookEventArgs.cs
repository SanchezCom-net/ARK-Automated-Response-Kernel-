using WpfPoint = System.Windows.Point;

namespace ARK.UI.Core.Input;

public sealed class MouseButtonHookEventArgs : EventArgs
{
    public WpfPoint Position { get; }

    public MouseButtonHookEventArgs(WpfPoint position)
    {
        Position = position;
    }
}
