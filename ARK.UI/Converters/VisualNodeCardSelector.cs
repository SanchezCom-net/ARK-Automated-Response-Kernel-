using System.Windows;
using System.Windows.Controls;
using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;

namespace ARK.UI.Converters;

public sealed class VisualNodeCardSelector : DataTemplateSelector
{
    public DataTemplate? DefaultTemplate     { get; set; }
    public DataTemplate? SequencerTemplate   { get; set; }
    public DataTemplate? QueueBlockTemplate  { get; set; }
    public DataTemplate? TriggerRootTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item switch
        {
            VisualNode { LogicalNode: TriggerRootNode } when TriggerRootTemplate is not null
                => TriggerRootTemplate,
            VisualNode { LogicalNode: Logic_QueueBlockNode } when QueueBlockTemplate is not null
                => QueueBlockTemplate,
            VisualNode { LogicalNode: Logic_SequenceNode } when SequencerTemplate is not null
                => SequencerTemplate,
            _ => DefaultTemplate
        };
}
