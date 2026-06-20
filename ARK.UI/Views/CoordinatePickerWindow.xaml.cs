using System.Windows;
using System.Windows.Controls;
using MouseEventArgs      = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs        = System.Windows.Input.KeyEventArgs;
using Key                 = System.Windows.Input.Key;

namespace ARK.UI.Views;

public partial class CoordinatePickerWindow : Window
{
    public int ResultX { get; private set; }
    public int ResultY { get; private set; }

    public CoordinatePickerWindow() => InitializeComponent();

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        var logical = e.GetPosition(this);
        var screen  = PointToScreen(logical);
        ResultX = (int)screen.X;
        ResultY = (int)screen.Y;
        CoordsText.Text = $"X: {ResultX}  Y: {ResultY}";

        double left = logical.X + 18;
        double top  = logical.Y + 18;
        if (CoordsPanel.ActualWidth > 0 && left + CoordsPanel.ActualWidth > ActualWidth)
            left = logical.X - CoordsPanel.ActualWidth - 8;
        if (CoordsPanel.ActualHeight > 0 && top + CoordsPanel.ActualHeight > ActualHeight)
            top  = logical.Y - CoordsPanel.ActualHeight - 8;
        Canvas.SetLeft(CoordsPanel, left);
        Canvas.SetTop(CoordsPanel, top);

        base.OnPreviewMouseMove(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        DialogResult = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
        base.OnPreviewKeyDown(e);
    }
}
