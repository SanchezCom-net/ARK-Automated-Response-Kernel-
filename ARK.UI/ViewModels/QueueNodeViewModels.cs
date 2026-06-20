using System.Collections.ObjectModel;
using ARK.UI.Core.Models;

namespace ARK.UI.ViewModels;

// ── Базовый узел дерева очередей ─────────────────────────────────────────────

public abstract class QueueNodeVm : ViewModelBase
{
    private bool _isExpanded;
    private bool _childrenLoaded;
    private Func<IEnumerable<QueueNodeVm>>? _childFactory;

    public abstract string DisplayName { get; }
    public ObservableCollection<QueueNodeVm> Children { get; } = [];

    // Регистрирует фабрику дочерних узлов для ленивой загрузки.
    // Фабрика вызывается один раз — при первом раскрытии узла.
    protected void SetChildFactory(Func<IEnumerable<QueueNodeVm>> factory)
        => _childFactory = factory;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value) EnsureChildrenLoaded();
        }
    }

    // Загружает дочерние узлы, если ещё не были загружены.
    // Вызывается при раскрытии узла или явно при восстановлении сохранённого IsExpanded=true.
    public void EnsureChildrenLoaded()
    {
        if (_childrenLoaded || _childFactory is null) return;
        _childrenLoaded = true;
        foreach (var child in _childFactory())
            Children.Add(child);
    }
}

// ── Узел региона ─────────────────────────────────────────────────────────────

public sealed class QueueRegionNodeVm : QueueNodeVm
{
    public QueueRegion Region { get; }
    public override string DisplayName => Region.Name;

    // childFactory будет вызвана только при первом раскрытии узла.
    public QueueRegionNodeVm(QueueRegion region, Func<IEnumerable<QueueNodeVm>> childFactory)
    {
        Region = region;
        // Фабрика регистрируется ДО установки IsExpanded — если состояние = true,
        // EnsureChildrenLoaded() внутри сеттера найдёт готовую фабрику.
        SetChildFactory(childFactory);
        IsExpanded = region.IsExpanded;
    }
}

// ── Узел папки внутри региона ─────────────────────────────────────────────────

public sealed class QueueFolderNodeVm : QueueNodeVm
{
    public QueueFolder  Folder       { get; }
    public QueueRegion  ParentRegion { get; }
    public override string DisplayName => Folder.Name;

    public QueueFolderNodeVm(QueueFolder folder, QueueRegion parentRegion,
        Func<IEnumerable<QueueNodeVm>>? childFactory = null)
    {
        Folder       = folder;
        ParentRegion = parentRegion;
        if (childFactory is not null) SetChildFactory(childFactory);
        IsExpanded = folder.IsExpanded;
    }
}

// ── Узел-ссылка на макрос внутри региона ─────────────────────────────────────

public sealed class QueueMacroRefNodeVm : QueueNodeVm
{
    public MacroEntry  Macro        { get; }
    public AppProfile  Profile      { get; }
    public QueueRegion ParentRegion { get; }
    /// <summary>Отображаемый путь хранения: «Профиль / Папка / Подпапка»</summary>
    public string      DisplayPath  { get; }

    public override string DisplayName => Macro.Name;

    public int QueuePriority
    {
        get => Macro.QueuePriority;
        set
        {
            if (Macro.QueuePriority == value) return;
            Macro.QueuePriority = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PriorityBadge));
        }
    }

    public string PriorityBadge => Macro.QueuePriority > 0 ? $"[{Macro.QueuePriority}]" : string.Empty;

    public QueueMacroRefNodeVm(MacroEntry macro, AppProfile profile, QueueRegion region, string displayPath)
    {
        Macro        = macro;
        Profile      = profile;
        ParentRegion = region;
        DisplayPath  = displayPath;
    }
}
