using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using KhurramAudioRoute.Core;

namespace KhurramAudioRoute;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogger.LogException("UI thread unhandled", e.Exception);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            CrashLogger.LogException($"AppDomain unhandled (terminating={e.IsTerminating})", ex);
        else
            CrashLogger.Log($"AppDomain unhandled (non-Exception object): {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLogger.LogException("Unobserved task", e.Exception);
        e.SetObserved();
    }
}
