using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Nodes;

public sealed class Logic_SequenceNode : BaseNode
{
    [JsonIgnore] public override int CardBodyWidth { get; } = 60;
    [JsonIgnore] public override int InPortYCenter { get; } = 17;

    public ObservableCollection<ConnectorStep> Steps { get; } = [];

    public Logic_SequenceNode()
    {
        AddStep("Шаг 1");
        AddStep("Шаг 2");
    }

    private void AddStep(string name)
    {
        var step = new ConnectorStep { Name = name };
        step.PropertyChanged += OnStepPropertyChanged;
        Steps.Add(step);
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectorStep.IsAnyConnected)) return;
        if (sender is not ConnectorStep changed || !changed.IsAnyConnected) return;
        if (!ReferenceEquals(changed, Steps[^1])) return;
        AddStep($"Шаг {Steps.Count + 1}");
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        await logger.LogInfoAsync(Name,
            $"[ОЧЕРЕДЬ] Запуск последовательной очереди: {Steps.Count} шагов.")
            .ConfigureAwait(false);
        return true;
    }
}
