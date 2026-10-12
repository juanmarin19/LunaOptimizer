using System.IO;
using System.Runtime.InteropServices;
namespace LunaOptimizer.Services;

/// Limpieza rapida sin Winapp2: temporales y caches de todos los usuarios.
/// Lo que no se puede borrar porque esta en uso se programa para el proximo
/// arranque (MoveFileEx + MOVEFILE_DELAY_UNTIL_REBOOT), asi que termina
/// borrandose al 100%. La limpieza profunda Winapp2 va aparte con FluentCleaner.Core.
public static class TempCleaner
{
    public record CleanResult(int FilesDeleted, long BytesFreed, int ScheduledReboot, long ScheduledBytes, int Errors, List<string> Log);

    /// Raiz de los perfiles de usuario, sin hardcodear "C:\Users": se deduce del
    /// perfil actual (su carpeta padre) y si no, de la unidad de Windows.
    public static string UsersRoot()
    {
        try
        {
            var me = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var parent = Directory.GetParent(me);
            if (parent is not null && parent.Exists) return parent.FullName;
        }
        catch { }
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        return Path.Combine(root, "Users");
    }

    /// Carpetas de perfil existentes (una por usuario).
    public static string[] UserDirs()
    {
        try { return Directory.GetDirectories(UsersRoot()); }
        catch { return Array.Empty<string>(); }
    }

    /// TEMP de todos los usuarios (mismo criterio que BuildSafeDirs).
    public static string[] TempDirs()
    {
        var dirs = new List<string> { Path.GetTempPath() };
        foreach (var u in UserDirs())
        {
            var t = Path.Combine(u, "AppData", "Local", "Temp");
            if (Directory.Exists(t)) dirs.Add(t);
        }
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// Tamano real de %TEMP% (recorrido completo, sin topes artificiales).
    public static (int files, long bytes) MeasureTemp()
    {
        int files = 0; long bytes = 0;
        foreach (var dir in TempDirs())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in SafeEnumerateFiles(dir))
            {
                long len = SizeOf(f);
                if (len > 0) { files++; bytes += len; }
            }
        }
        return (files, bytes);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    /// Lee PendingFileRenameOperations (REG_MULTI_SZ: pares origen/destino) para
    /// NO volver a programar el MISMO archivo en cada limpieza. Sin esto la lista
    /// crecia indefinidamente (mismo .tmp programado una y otra vez) y el arranque
    /// se volvia mas lento con el tiempo.
    private static HashSet<string> LeerProgramadosParaReinicio()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (key?.GetValue("PendingFileRenameOperations") is string[] entradas)
                for (int i = 0; i + 1 < entradas.Length; i += 2)
                    if (!string.IsNullOrEmpty(entradas[i])) set.Add(ToNtPath(entradas[i]));
        }
        catch { }
        return set;
    }

    /// MoveFileEx guarda las rutas como "\??\C:\...": normalizamos igual para
    /// poder comparar y evitar duplicados.
    private static string ToNtPath(string path)
        => path.StartsWith(@"\??\", StringComparison.Ordinal) ? path : @"\??\" + path;

    /// Carpetas temporales seguras de TODOS los usuarios (ademas de las ya
    /// existentes). Sin Prefetch, sin papelera, sin WinSxS.
    private static List<string> BuildSafeDirs()
    {
        if (_safeDirs is not null) return _safeDirs;
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetTempPath(),
            Path.Combine(win, "Temp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
            // Delivery Optimization, miniaturas, logs Windows Update (no tocar WinSxS)
            Path.Combine(win, "SoftwareDistribution", "Download"),
            Path.Combine(win, "SoftwareDistribution", "DeliveryOptimization", "Cache"),
            Path.Combine(common, @"Microsoft\Windows\WER"),
            Path.Combine(common, @"Microsoft\Windows\WER\Temp"),
        };
        foreach (var u in UserDirs())
        {
            string local = Path.Combine(u, "AppData", "Local");
            dirs.Add(Path.Combine(local, "Temp"));
            dirs.Add(Path.Combine(local, @"Microsoft\Windows\INetCache"));
            dirs.Add(Path.Combine(local, "CrashDumps"));
            dirs.Add(Path.Combine(local, "D3DSCache"));
            dirs.Add(Path.Combine(local, @"NVIDIA\GLCache"));
            dirs.Add(Path.Combine(local, @"NVIDIA\DXCache"));
            dirs.Add(Path.Combine(local, @"AMD\DxCache"));
        }
        _safeDirs = dirs.ToList();
        return _safeDirs;
    }

    private static List<string>? _safeDirs;

    /// Tamano del archivo, o -1 si no existe / es reparse / da error.
    public static long SizeOf(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return -1;
            if ((fi.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return -1;
            return fi.Length;
        }
        catch { return -1; }
    }

    /// Borra la carpeta SOLO si existe y esta vacia (nunca junctions/reparse).
    public static void DeleteIfEmpty(string dir)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return;
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }

    public static async Task<CleanResult> QuickCleanAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        return await Task.Run(() =>
        {
            int files = 0, errors = 0, scheduled = 0;
            long bytes = 0, scheduledBytes = 0;
            var log = new List<string>();
            var fallidos = new List<string>();       // pasada 1: no se pudieron borrar
            foreach (var dir in BuildSafeDirs())
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
                    }
                    catch { errors++; continue; }
                    try
                    {
                        long len = fi.Length;
                        fi.Delete();
                        // Verificar: File.Delete puede "no lanzar" y sin embargo
                        // no borrar (sharing violation, delete-on-close, ACL).
                        // Contar solo lo que REALMENTE se fue del disco.
                        if (!fi.Exists) { files++; bytes += len; }
                        else fallidos.Add(f);
                        if (files % 200 == 0) progress?.Report($"{files} archivos... {bytes / 1024 / 1024} MB");
                    }
                    catch { fallidos.Add(f); }
                }
                log.Add($"{dir}: OK");
            }

            // Pasada 2: reintenta los fallidos (puede que se hayan liberado) y
            // los que sigan bloqueados se marcan para borrarlos en el arranque.
            progress?.Report($"Reintentando {fallidos.Count} archivos...");
            var yaProgramados = LeerProgramadosParaReinicio();
            foreach (var f in fallidos)
            {
                if (token.IsCancellationRequested) break;
                long len = 0;
                try
                {
                    var fi = new FileInfo(f);
                    if (!fi.Exists) continue;
                    len = fi.Length;
                    fi.Delete();
                    if (!fi.Exists) { files++; bytes += len; continue; }
                    // sigue ahi: probar bloqueo y programar para el arranque
                }
                catch { }
                try
                {
                    if (IsFileLocked(f))
                    {
                        string nt = ToNtPath(f);
                        if (yaProgramados.Contains(nt)) continue; // ya se borrara en el arranque
                        if (MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                        {
                            yaProgramados.Add(nt);
                            scheduled++; scheduledBytes += len;
                            continue;
                        }
                    }
                }
                catch { }
                errors++;
            }

            // carpetas vacias (saltar junctions), de mas profundo a shallow
            foreach (var dir in BuildSafeDirs())
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var d in SafeEnumerateDirs(dir))
                    DeleteIfEmpty(d);
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
            return new CleanResult(files, bytes, scheduled, scheduledBytes, errors, log);
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
    public static IEnumerable<string> SafeEnumerateFiles(string dir)
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
    public static List<string> SafeEnumerateDirs(string dir)
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
