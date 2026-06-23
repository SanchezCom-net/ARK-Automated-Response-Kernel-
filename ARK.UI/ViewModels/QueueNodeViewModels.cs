using ARK.UI.Core.Models;

namespace ARK.UI.ViewModels;

// ── Базовый элемент плоского списка очередей ──────────────────────────────────

public abstract class QueueListItemVm : ViewModelBase
{
    public abstract string DisplayName { get; }
}

// ── Элемент региона (корневой уровень) ───────────────────────────────────────

public sealed class RegionListItemVm : QueueListItemVm
{
    public RegionQueue Region { get; }

    public override string DisplayName => Region.Name;

    public string MacroCountLabel => Region.Entries.Count switch
    {
        0         => "пусто",
        1         => "1 макрос",
        <= 4      => $"{Region.Entries.Count} макроса",
        int n     => $"{n} макросов"
    };

    public RegionListItemVm(RegionQueue region) => Region = region;
}

// ── Элемент макроса внутри региона ───────────────────────────────────────────

public sealed class MacroQueueItemVm : QueueListItemVm
{
    public RegionQueueEntry Entry    { get; }
    public MacroManifest    Manifest { get; }
    public RegionQueue      Region   { get; }

    public override string DisplayName => Manifest.Name;

    public string Environment => Manifest.Environment;
    public bool   IsRelease   => Manifest.Environment.Equals("release", StringComparison.OrdinalIgnoreCase);

    /// <summary>Родительский путь без имени макроса: "Программа  ›  Папка" или пусто.</summary>
    public string LocationPath { get; }

    /// <summary>true, если LocationPath непустой — управляет видимостью второй строки.</summary>
    public bool HasLocationPath => !string.IsNullOrEmpty(LocationPath);

    private int _priority;
    public int Priority
    {
        get => _priority;
        set
        {
            if (!SetProperty(ref _priority, value)) return;
            OnPropertyChanged(nameof(PriorityBadge));
            OnPropertyChanged(nameof(HasPriority));
        }
    }

    public bool   HasPriority   => _priority > 0;
    public string PriorityBadge => _priority > 0 ? $"P: {_priority}" : string.Empty;

    public MacroQueueItemVm(
        RegionQueueEntry entry,
        MacroManifest    manifest,
        RegionQueue      region,
        string           locationPath)
    {
        Entry        = entry;
        Manifest     = manifest;
        Region       = region;
        LocationPath = locationPath;
        _priority    = entry.Priority;
    }
}
