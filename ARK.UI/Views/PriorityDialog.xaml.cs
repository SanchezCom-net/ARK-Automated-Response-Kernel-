using System.Windows;
using System.Windows.Input;
using WpfKeyEventArgs         = System.Windows.Input.KeyEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ARK.UI.Views;

public partial class PriorityDialog : Window
{
    public int ResultPriority { get; private set; }

    public PriorityDialog(int currentPriority)
    {
        InitializeComponent();
        PriorityBox.Text = currentPriority > 0 ? currentPriority.ToString() : string.Empty;
        Loaded += (_, _) =>
        {
            PriorityBox.Focus();
            PriorityBox.SelectAll();
        };
    }

    // ── Только цифры 0-9 ─────────────────────────────────────────────────

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    // ── Клавиатура ────────────────────────────────────────────────────────

    private void OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if      (e.Key == Key.Return) Confirm();
        else if (e.Key == Key.Escape) Close();
    }

    private void OnTitleBarDrag(object sender, WpfMouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)    => Confirm();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Confirm()
    {
        if (!int.TryParse(PriorityBox.Text, out var value))
            value = 0;

        ResultPriority = Math.Clamp(value, 0, 99);
        DialogResult   = true;
    }
}
