using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Replica.App.Bootstrap;
using Replica.App.Services;

namespace Replica.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private IGlobalExceptionHandler? _exceptionHandler;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _services = AppBootstrapper.BuildServices();
            _exceptionHandler = _services.GetRequiredService<IGlobalExceptionHandler>();

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            MainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Replica를 시작할 수 없습니다.\n\n{exception.Message}",
                "Replica",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        _exceptionHandler?.Handle(e.Exception, "WPF dispatcher");
        e.Handled = true;
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        _exceptionHandler?.Handle(e.Exception, "Task scheduler");
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _exceptionHandler?.Handle(exception, "Application domain");
        }
    }
}
