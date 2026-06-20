using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Services;
using ARK.UI.ViewModels;

namespace ARK.UI.Views;

public partial class DashboardWindow : Window
{
    private readonly IConfigService _configService;

    public DashboardWindow(DashboardViewModel viewModel, IConfigService configService)
    {
        _configService = configService;
        InitializeComponent();
        DataContext = viewModel;

        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/app.ico", UriKind.RelativeOrAbsolute)); }
        catch { }

        // Восстанавливаем геометрию окна до первого показа (без мерцания)
        var cfg = _configService.Current;
        Width  = cfg.WindowWidth;
        Height = cfg.WindowHeight;
        if (Enum.TryParse<WindowState>(cfg.WindowState, out var savedState)
            && savedState != System.Windows.WindowState.Minimized)
            WindowState = savedState;

        // PreviewMouseLeftButtonDown: срабатывает до захвата мыши кнопкой — позволяет
        // передать управление Win32 до того, как WPF захватит ввод.
        ResizeButton.PreviewMouseLeftButtonDown += OnResizeButtonMouseDown;

        Closing += DashboardWindow_Closing;
        Closed  += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void OnResizeButtonMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Api.ReleaseCapture();
        Win32Api.SendMessageW(hwnd, Win32Api.WM_SYSCOMMAND, (IntPtr)Win32Api.SC_SIZE_SE, IntPtr.Zero);
    }

    private void DashboardWindow_Closing(object? sender, CancelEventArgs e)
    {
        var cfg = _configService.Current;
        cfg.WindowWidth  = ActualWidth;
        cfg.WindowHeight = ActualHeight;
        cfg.WindowState  = WindowState.ToString();

        // Сохраняем ширины боковых панелей Blueprint Editor
        var blueprint = FindVisualChild<BlueprintEditorControl>(this);
        blueprint?.SavePanelWidths(cfg);

        _ = _configService.SaveAsync();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var found = FindVisualChild<T>(child);
            if (found is not null) return found;
        }
        return null;
    }
}
