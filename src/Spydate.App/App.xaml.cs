using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Spydate.App.Services;
using Spydate.App.ViewModels;
using Spydate.App.Views;
using Wpf.Ui.Appearance;

namespace Spydate.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Services not initialised.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var sc = new ServiceCollection();
        sc.AddSingleton<IFileDialogService, FileDialogService>();
        sc.AddSingleton<HighlightingService>();
        sc.AddSingleton<WorkspaceService>();
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<MainWindow>();
        _services = sc.BuildServiceProvider();

        // Warm up highlighting definitions before any editor is created.
        _services.GetRequiredService<HighlightingService>().EnsureRegistered();

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();

        // Open a file passed on the command line.
        string? path = e.Args.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
        if (path is not null)
        {
            _ = _services.GetRequiredService<MainViewModel>().OpenPathAsync(path);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Spydate – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
