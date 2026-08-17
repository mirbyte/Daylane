using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Daylane.Services;
using Daylane.ViewModels;

namespace Daylane;

public partial class App : Application
{
    private static EventWaitHandle? _activationSignal;

    private TrackingService? _trackingService;
    private MainWindow? _mainWindow;
    private CancellationTokenSource? _activationCts;
    private bool _isExiting;

    internal static void RegisterActivationSignal(EventWaitHandle signal)
    {
        _activationSignal = signal;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                _trackingService = new TrackingService();
                _trackingService.Start();
            }
            catch (InvalidOperationException ex)
            {
                desktop.MainWindow = CreateErrorWindow(ex.Message);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            StartupRegistration.RefreshRegisteredPathIfEnabled();

            var viewModel = new MainWindowViewModel(_trackingService);
            _mainWindow = new MainWindow
            {
                DataContext = viewModel
            };

            desktop.MainWindow = _mainWindow;
            WireUiVisibility(_mainWindow, _trackingService);
            desktop.ShutdownRequested += (_, _) =>
            {
                StopActivationListener();
                if (!_isExiting)
                {
                    _trackingService.Dispose();
                }
            };
            SetupTrayIcon(desktop);
            StartActivationListener();

            if (StartupRegistration.HasTrayArgument(desktop.Args ?? Array.Empty<string>()))
            {
                _mainWindow.ShowInTaskbar = false;
                _mainWindow.Hide();
                // Lifetime may Show again after init; hide once more after load.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_mainWindow is null || _isExiting)
                    {
                        return;
                    }

                    _mainWindow.ShowInTaskbar = false;
                    _mainWindow.Hide();
                }, DispatcherPriority.Loaded);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void WireUiVisibility(MainWindow window, TrackingService tracking)
    {
        void Update()
        {
            bool visible = window.IsVisible && window.WindowState != WindowState.Minimized;
            tracking.SetUiVisible(visible);
        }

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
            {
                Update();
            }
        };

        Update();
    }

    private void StartActivationListener()
    {
        if (_activationSignal is null)
        {
            return;
        }

        _activationCts = new CancellationTokenSource();
        var signal = _activationSignal;
        var token = _activationCts.Token;

        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (signal.WaitOne(500))
                {
                    Dispatcher.UIThread.Post(ShowMainWindow);
                }
            }
        }, token);
    }

    private void StopActivationListener()
    {
        _activationCts?.Cancel();
        _activationCts?.Dispose();
        _activationCts = null;
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Daylane/Assets/app-icon.ico"))),
            ToolTipText = "Daylane (running)",
            IsVisible = true
        };

        var menu = new NativeMenu();
        var showItem = new NativeMenuItem { Header = "Show" };
        showItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(showItem);

        var startupItem = new NativeMenuItem
        {
            Header = "Start with Windows",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = StartupRegistration.IsEnabled()
        };
        startupItem.Click += (_, _) =>
        {
            bool enable = !StartupRegistration.IsEnabled();
            StartupRegistration.SetEnabled(enable);
            startupItem.IsChecked = enable;
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new NativeMenuItemSeparator());
        var exitItem = new NativeMenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApplication(desktop);
        menu.Items.Add(exitItem);
        trayIcon.Menu = menu;
        trayIcon.Clicked += (_, _) => ShowMainWindow();

        TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Maximized;
        _mainWindow.Activate();
        _mainWindow.ScrollTimelineToNow();
    }

    private void ExitApplication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _isExiting = true;
        StopActivationListener();
        _trackingService?.Dispose();
        desktop.Shutdown();
    }

    internal bool IsExiting => _isExiting;

    private static Window CreateErrorWindow(string message)
    {
        return new Window
        {
            Title = "Daylane",
            Width = 420,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(24)
            }
        };
    }
}
