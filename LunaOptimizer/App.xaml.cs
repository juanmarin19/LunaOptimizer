using System.Diagnostics;
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

    private const string NombreMutex = "LunaOptimizer_InstanciaUnica";
    private Mutex? _instanceMutex;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    /// Instancia unica CON red de seguridad. Si ya hay otra LunaOptimizer con
    /// ventana, se la enfoca y esta sale. Si la que existe NO tiene ninguna ventana
    /// quedo "zombi" porque su cierre anterior fallo; ver OnClosing de MainWindow),
    /// se la termina y arranca esta. Antes ese caso dejaba la app inservible para
    /// siempre: el mutex seguia tomado y cada intento salia en silencio.
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Init();

        if (!TomarMutex())
        {
            if (EnfocarExistente(1500)) { Shutdown(); return; }

            CerrarInstanciasSinVentana();
            bool logrado = false;
            for (int i = 0; i < 20 && !logrado; i++)
            {
                Thread.Sleep(200);
                logrado = TomarMutex();
            }
            if (!logrado) { Shutdown(); return; }
        }

        base.OnStartup(e);
    }

    /// Toma el mutex de instancia unica. Devuelve false si otra instancia lo tiene.
    private bool TomarMutex()
    {
        try
        {
            var m = new Mutex(true, NombreMutex, out bool primera);
            if (!primera) { m.Dispose(); return false; }
            _instanceMutex = m;
            return true;
        }
        catch
        {
            // Si el sistema no deja crear el mutex, no bloqueamos el arranque.
            _instanceMutex = null;
            return true;
        }
    }

    /// Busca una ventana de LunaOptimizer y la trae al frente. Reintenta hasta
    /// esperaMs por si la otra instancia todavia esta arrancando.
    private static bool EnfocarExistente(int esperaMs)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(esperaMs);
        while (true)
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    string n;
                    try { n = p.ProcessName; } catch { continue; }
                    if (!n.StartsWith("LunaOptimizer", StringComparison.OrdinalIgnoreCase)) continue;
                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    ShowWindow(h, 9);              // SW_RESTORE
                    SetForegroundWindow(h);
                    return true;
                }
            }
            if (DateTime.UtcNow >= limite) return false;
            Thread.Sleep(250);
        }
    }

    /// Termina las instancias de LunaOptimizer que llevan mas de 10 s sin ventana
    /// (zombis). El umbral evita matar otra copia que todavia esta arrancando.
    private static void CerrarInstanciasSinVentana()
    {
        int yo = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == yo) continue;
                    string n;
                    try { n = p.ProcessName; } catch { continue; }
                    if (!n.StartsWith("LunaOptimizer", StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.MainWindowHandle != IntPtr.Zero) continue;
                    if ((DateTime.Now - p.StartTime).TotalSeconds < 10) continue;
                    p.Kill(true);
                    p.WaitForExit(3000);
                }
                catch { }
            }
        }
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

        // Red de seguridad: si el error nos dejo sin ninguna ventana, salir. Un
        // proceso vivo pero sin interfaz solo sirve para retener el mutex y que la
        // app no vuelva a abrirse nunca mas.
        try
        {
            if (Windows.Count == 0)
            {
                Cerrando = true;
                Environment.Exit(1);
            }
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
