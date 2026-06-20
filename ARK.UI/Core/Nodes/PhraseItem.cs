using System.ComponentModel;

namespace ARK.UI.Core.Nodes;

public sealed class PhraseItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _text;

    internal PhraseItem(string text) => _text = text;

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }
    }
}
