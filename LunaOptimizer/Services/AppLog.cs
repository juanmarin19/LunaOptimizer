using System.IO;

namespace LunaOptimizer.Services;

/// Log unico de la app (%LocalAppData%\LunaOptimizer\errores.log).
/// Centraliza el registro para que ningun 'catch' importante se trague un fallo
/// sin dejar rastro. App.xaml.cs lo usa para las excepciones no controladas y los
/// servicios lo usan en las operaciones delicadas (registro, desinstalacion...).
internal static class AppLog
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaOptimizer");
    private static readonly string LogPath = Path.Combine(Dir, "errores.log");
    private static readonly object Lock = new();

    public static void Error(Exception ex, string contexto = "") =>
        Write($"{contexto}{Environment.NewLine}{ex}");

    public static void Write(string texto)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{texto}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
