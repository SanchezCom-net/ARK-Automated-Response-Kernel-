using System.Windows;
using System.Windows.Input;
using WpfKeyEventArgs         = System.Windows.Input.KeyEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ARK.UI.Views;

public partial class AiCharacterDialog : Window
{
    public string? ResultName   { get; private set; }
    public string? ResultPrompt { get; private set; }

    public AiCharacterDialog(string currentName, string currentPrompt)
    {
        InitializeComponent();
        NameBox.Text   = currentName;
        PromptBox.Text = currentPrompt;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnTitleBarDrag(object sender, WpfMouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)  => Confirm();
    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Confirm()
    {
        ResultName   = NameBox.Text.Trim();
        ResultPrompt = PromptBox.Text;
        DialogResult = true;
    }
}
