using System.Windows;
using System.Windows.Threading;
using APTV.Services;

namespace APTV;

public partial class App : Application
{
    private readonly AppLogger _logger = AppLogger.Instance;

    public App()
    {
        Startup += App_Startup;
        Exit += App_Exit;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private void App_Startup(object sender, StartupEventArgs e)
    {
        _logger.Info($"Application startup. Log file: {_logger.CurrentLogFilePath}");
    }

    private void App_Exit(object sender, ExitEventArgs e)
    {
        _logger.Info($"Application exit. Code={e.ApplicationExitCode}");
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.Error("Unhandled UI exception.", e.Exception);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger.Error(
            $"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}",
            e.ExceptionObject as Exception);
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.Error("Unobserved task exception.", e.Exception);
    }
}
