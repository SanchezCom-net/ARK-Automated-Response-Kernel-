using System.ComponentModel;

namespace ARK.UI.ViewModels;

public enum ExplorerItemKind { AppFolder, Folder, Region, Macro }

public sealed class ExplorerItem : INotifyPropertyChanged
{
    private string _name        = string.Empty;
    private bool   _isEnabled   = true;
    private int    _priority;
    private string _environment = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExplorerItemKind Kind           { get; init; }
    public object?          DataObject     { get; init; }
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

    /// <summary>"beta" или "release" — только для Kind=Macro</summary>
    public string Environment
    {
        get => _environment;
        set
        {
            if (_environment == value) return;
            _environment = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Environment)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRelease)));
        }
    }

    public bool   HasPriority   => _priority > 0;
    public string PriorityBadge => _priority > 0 ? $"P{_priority}" : string.Empty;

    /// <summary>true если макрос в Release (зелёная галочка). false — Beta (серая шестерёнка).</summary>
    public bool IsRelease => string.Equals(_environment, "release", StringComparison.OrdinalIgnoreCase);
}
