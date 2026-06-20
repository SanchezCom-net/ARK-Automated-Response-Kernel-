using System.Collections.ObjectModel;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface IProfileService
{
    ObservableCollection<AppProfile> Profiles { get; }

    Task LoadAllProfilesAsync(CancellationToken ct = default);
    Task SaveProfileAsync(AppProfile profile, CancellationToken ct = default);
    Task DeleteProfileAsync(AppProfile profile, CancellationToken ct = default);

    Task ExportProfileAsync(AppProfile profile, string targetFilePath, string? password, CancellationToken ct = default);
    Task<AppProfile> ImportProfileAsync(string sourceFilePath, string? password, CancellationToken ct = default);
}
