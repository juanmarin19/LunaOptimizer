using System.Windows;
using System.Windows.Threading;
using LunaOptimizer.Services;

namespace LunaOptimizer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// true una vez que la ventana principal se esta cerrando: durante el apagado
    /// no tiene sentido registrar/avisar de errores (p. ej. el DllNotFound que
    /// algunos modulos nativos lanzan al descargarse de la memoria).
    internal static volatile bool Cerrando;

    private Mutex? _instanceMutex;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    /// Instancia unica: si ya hay una LunaOptimizer abierta, la enfoca en vez
    /// de abrir otra copia que quedaria en segundo plano.
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Init();
        _instanceMutex = new Mutex(true, "LunaOptimizer_InstanciaUnica", out bool primera);
        if (!primera)
        {
            EnfocarExistente();
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    private static void EnfocarExistente()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                string n;
                try { n = p.ProcessName; } catch { continue; }
                if (!n.StartsWith("LunaOptimizer", StringComparison.OrdinalIgnoreCase)) { p.Dispose(); continue; }
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);      // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
                p.Dispose();
            }
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// Error en el hilo de UI: se registra y se continua (no cerrar la app).
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (Cerrando) return;
        WriteLog(e.Exception);
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
        if (Cerrando) return;
        if (e.ExceptionObject is Exception ex) WriteLog(ex);
    }

    /// Tareas en segundo plano con excepciones sin observar.
    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteLog(e.Exception);
        e.SetObserved();
    }

    private static void WriteLog(Exception ex) => AppLog.Error(ex);
}
