using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Diagnostics;
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
        TraceBindingsIfAsked();

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

    /// <summary>
    /// Sends WPF's binding failures to a file when SPYDATE_TRACE_BINDINGS names one. A binding that
    /// silently does nothing - a command that never arrives, a property that never updates - is invisible
    /// otherwise, and that is exactly the kind of bug the window cannot show you.
    /// </summary>
    private static void TraceBindingsIfAsked()
    {
        string? path = Environment.GetEnvironmentVariable("SPYDATE_TRACE_BINDINGS");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var listener = new TextWriterTraceListener(path) { TraceOutputOptions = TraceOptions.None };
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
        Trace.AutoFlush = true;

        // Written straight away, so an empty log means "no binding failures" rather than "not enabled".
        listener.WriteLine($"Spydate binding trace started {DateTime.Now:HH:mm:ss}");
        listener.Flush();
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
