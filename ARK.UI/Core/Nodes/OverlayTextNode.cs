using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class OverlayTextNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(Text);

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnPropertyChanged(); } }
    }

    private int _durationMs = 2000;
    public int DurationMilliseconds
    {
        get => _durationMs;
        set { if (_durationMs != value) { _durationMs = value; OnPropertyChanged(); } }
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        TryApplyContextInput<string>(nameof(Text), v => Text = v);

        var overlayService = serviceProvider.GetRequiredService<IOverlayService>();
        await overlayService.ShowTextAsync(Text, DurationMilliseconds, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
}
