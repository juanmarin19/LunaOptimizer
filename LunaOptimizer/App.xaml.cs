using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LunaOptimizer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaOptimizer");

    private static readonly string LogFile = Path.Combine(LogDir, "errores.log");

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    /// Error en el hilo de UI: se registra y se continua (no cerrar la app).
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog(e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show(
                $"Se produjo un error y se ha ignorado, la app sigue funcionando.\n\n{e.Exception.Message}",
                "LunaOptimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }

    /// Excepcion no controlada fuera del hilo de UI: solo registrar.
    private void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) WriteLog(ex);
    }

    /// Tareas en segundo plano con excepciones sin observar.
    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteLog(e.Exception);
        e.SetObserved();
    }

    private static void WriteLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}");
        }
        catch { }
    }
}
