using System.Windows.Input;

namespace ARK.UI.ViewModels;

public sealed class BreadcrumbItem
{
    public string   Label         { get; init; } = string.Empty;
    public ICommand NavigateToCmd { get; init; } = null!;
}
