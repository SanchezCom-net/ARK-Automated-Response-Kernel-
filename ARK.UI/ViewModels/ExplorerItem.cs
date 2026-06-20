using System.ComponentModel;

namespace ARK.UI.ViewModels;

public enum ExplorerItemKind { Profile, Folder, Region, Macro }

public sealed class ExplorerItem : INotifyPropertyChanged
{
    private string _name      = string.Empty;
    private bool   _isEnabled = true;
    private int    _priority;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExplorerItemKind Kind          { get; init; }
    public object?          DataObject    { get; init; }
    public bool             CanSetPriority { get; init; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public int Priority
    {
        get => _priority;
        set
        {
            if (_priority == value) return;
            _priority = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPriority)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PriorityBadge)));
        }
    }

    public bool   HasPriority   => _priority > 0;
    public string PriorityBadge => _priority > 0 ? $"P{_priority}" : string.Empty;
}
