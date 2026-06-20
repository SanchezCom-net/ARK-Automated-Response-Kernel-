using System.Windows.Input;

namespace ARK.UI.Core.Input;

public sealed class KeyHookEventArgs : EventArgs
{
    public Key          Key       { get; }
    public ModifierKeys Modifiers { get; }

    public KeyHookEventArgs(Key key, ModifierKeys modifiers)
    {
        Key       = key;
        Modifiers = modifiers;
    }
}
