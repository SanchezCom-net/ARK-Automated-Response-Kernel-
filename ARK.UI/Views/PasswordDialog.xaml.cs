using System.Windows;
using System.Windows.Input;
using WpfKeyEventArgs         = System.Windows.Input.KeyEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ARK.UI.Views;

public partial class PasswordDialog : Window
{
    public string Password => PassBox.Password;

    public PasswordDialog(string title, string description = "")
    {
        InitializeComponent();
        TitleText.Text = title;
        DescText.Text  = description;
        DescText.Visibility = string.IsNullOrEmpty(description)
            ? Visibility.Collapsed
            : Visibility.Visible;
        Loaded += (_, _) => PassBox.Focus();
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
        DialogResult = true;
    }
}
