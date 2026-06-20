using System.Windows;
using System.Windows.Controls;

namespace ARK.UI.Core.Services;

/// <summary>
/// Attached behavior для двустороннего биндинга PasswordBox.Password ↔ ViewModel-свойство.
/// PasswordBox не поддерживает стандартный DependencyProperty для Password (намеренно, по соображениям безопасности).
/// Флаг Updating хранится per-instance через отдельный DP — безопасен для множества PasswordBox одновременно.
/// </summary>
public static class PasswordBehavior
{
    private static readonly DependencyProperty UpdatingProperty =
        DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBehavior),
            new PropertyMetadata(false));

    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBehavior),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    public static string GetBoundPassword(DependencyObject d) =>
        (string)d.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject d, string v) =>
        d.SetValue(BoundPasswordProperty, v);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox pb) return;
        pb.PasswordChanged -= OnPasswordBoxChanged;
        if (!(bool)pb.GetValue(UpdatingProperty))
            pb.Password = (string)(e.NewValue ?? string.Empty);
        pb.PasswordChanged += OnPasswordBoxChanged;
    }

    private static void OnPasswordBoxChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox pb) return;
        pb.SetValue(UpdatingProperty, true);
        SetBoundPassword(pb, pb.Password);
        pb.SetValue(UpdatingProperty, false);
    }
}
