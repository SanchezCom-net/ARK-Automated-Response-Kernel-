using System.Windows;
using System.Windows.Input;
using WpfKeyEventArgs       = System.Windows.Input.KeyEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ARK.UI.Views;

public partial class RenameDialog : Window
{
    public string? ResultText { get; private set; }

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text            = currentName;
        NameBox.SelectionStart  = 0;
        NameBox.SelectionLength = currentName.Length;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnTitleBarDrag(object sender, WpfMouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)    => Confirm();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if      (e.Key == Key.Return) Confirm();
        else if (e.Key == Key.Escape) Close();
    }

    private void Confirm()
    {
        ResultText   = NameBox.Text;
        DialogResult = true;
    }
}
