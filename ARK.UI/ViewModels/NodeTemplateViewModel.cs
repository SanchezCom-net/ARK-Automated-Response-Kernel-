using ARK.UI.Core.Models;
using ARK.UI.Core.Services;
using ARK.UI.Resources;

namespace ARK.UI.ViewModels;

public sealed class NodeTemplateViewModel : ViewModelBase
{
    private readonly string _nameKey;
    private readonly string _descKey;

    public Type         NodeType     { get; }
    public string       IconGeometry { get; }
    public NodeCategory Category     { get; }

    public string Name        => Strings.ResourceManager.GetString(_nameKey, Strings.Culture) ?? _nameKey;
    public string Description => Strings.ResourceManager.GetString(_descKey, Strings.Culture) ?? _descKey;

    public string CategoryName => Category switch
    {
        NodeCategory.SystemAndInput => Strings.Toolbox_Category_SystemAndInput,
        NodeCategory.UserInterface  => Strings.Toolbox_Category_UserInterface,
        NodeCategory.NetworkAndApi  => Strings.Toolbox_Category_NetworkAndApi,
        NodeCategory.VisionAndAi   => Strings.Toolbox_Category_VisionAndAi,
        NodeCategory.OBS           => Strings.Toolbox_Category_OBS,
        NodeCategory.WindowsSystem => Strings.Toolbox_Category_WindowsSystem,
        NodeCategory.ScriptsAndData=> Strings.Toolbox_Category_ScriptsAndData,
        NodeCategory.SmartLogic    => Strings.Toolbox_Category_SmartLogic,
        NodeCategory.GuardianLogic => Strings.Toolbox_Category_GuardianLogic,
        _                          => Category.ToString()
    };

    public NodeTemplateViewModel(NodeTemplate template)
    {
        NodeType      = template.NodeType;
        IconGeometry  = template.IconGeometry;
        Category      = template.Category;
        _nameKey      = template.NameKey;
        _descKey      = template.DescriptionKey;

        LocalizationService.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(CategoryName));
    }
}
