using System.IO;
namespace LunaOptimizer.Services;

/// Limpieza rapida sin Winapp2: temporales, caches comunes, papelera (opcional).
/// La limpieza profunda Winapp2 viene despues con FluentCleaner.Core.
public static class TempCleaner
{
    public record CleanResult(int FilesDeleted, long BytesFreed, int Errors, List<string> Log);

    private static readonly string[] SafeDirs =
    {
        Path.GetTempPath(),
        @"C:\Windows\Temp",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
    };

    private static readonly string[] SafeExtraDirs =
    {
        // Delivery Optimization, miniaturas, logs Windows Update (no tocar WinSxS)
        @"C:\Windows\SoftwareDistribution\Download",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\INetCache"),
    };

    public static async Task<CleanResult> QuickCleanAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        return await Task.Run(() =>
        {
            int files = 0, errors = 0;
            long bytes = 0;
            var log = new List<string>();
            foreach (var dir in SafeDirs.Concat(SafeExtraDirs))
            {
                if (token.IsCancellationRequested) break;
                if (!Directory.Exists(dir)) continue;
                progress?.Report($"Escaneando {dir}");
                foreach (var f in SafeEnumerateFiles(dir))
                {
                    if (token.IsCancellationRequested) break;
                    FileInfo fi;
                    try
                    {
                        fi = new FileInfo(f);
                        if (!fi.Exists) continue;
                        if ((fi.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0) continue;
                        if (IsFileLocked(f)) continue; // en uso -> saltar
                    }
                    catch { errors++; continue; }
                    try
                    {
                        long len = fi.Length;
                        fi.Delete();
                        files++; bytes += len;
                        if (files % 200 == 0) progress?.Report($"{files} archivos... {bytes / 1024 / 1024} MB");
                    }
                    catch { errors++; }
                }
                // carpetas vacias (saltar junctions)
                foreach (var d in SafeEnumerateDirs(dir))
                {
                    try
                    {
                        var di = new DirectoryInfo(d);
                        if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                    }
                    catch { }
                }
                log.Add($"{dir}: OK");
            }
            // Flush DNS (rapido, seguro)
            try
            {
                using var p = new System.Diagnostics.Process();
                p.StartInfo = new System.Diagnostics.ProcessStartInfo("ipconfig", "/flushdns")
                {
                    CreateNoWindow = true, UseShellExecute = false
                };
                p.Start(); p.WaitForExit(5000);
                log.Add("DNS flush: OK");
            }
            catch (Exception ex) { log.Add("DNS flush: " + ex.Message); }
            return new CleanResult(files, bytes, errors, log);
        }, token);
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            return false;
        }
        catch { return true; }
    }

    /// Recorrido manual en profundidad: si un subdirectorio lanza (bloqueado,
    /// permisos, junction roto) se salta ESE directorio y sigue. Nunca vuelve a
    /// llamar a MoveNext sobre un enumerador ya fallado (bucle infinito).
    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(current); }
            catch { files = Array.Empty<string>(); }
            foreach (var f in files) yield return f;

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(current); }
            catch { subDirs = Array.Empty<string>(); }
            foreach (var s in subDirs)
            {
                if (IsReparsePoint(s)) continue; // no seguir vinculos: bucles y accesos no deseados
                pending.Push(s);
            }
        }
    }

    /// Todos los subdirectorios (sin reparse) ordenados del mas profundo al mas
    /// shallow, para poder borrar vacios de dentro hacia fuera.
    private static List<string> SafeEnumerateDirs(string dir)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(current); }
            catch { continue; }
            foreach (var s in subDirs)
            {
                if (IsReparsePoint(s)) continue;
                result.Add(s);
                pending.Push(s);
            }
        }
        result.Sort((a, b) => b.Length.CompareTo(a.Length));
        return result;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    public static string FormatBytes(long b)
        => b < 1024 ? $"{b} B" : b < 1024*1024 ? $"{b/1024.0:F1} KB" : b < 1024L*1024*1024 ? $"{b/1024.0/1024:F1} MB" : $"{b/1024.0/1024/1024:F2} GB";
}
