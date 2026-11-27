using System.IO;
using System.Runtime.InteropServices;
namespace LunaOptimizer.Services;

/// Limpieza rapida sin Winapp2: temporales y caches de todos los usuarios.
/// Lo que no se puede borrar porque esta en uso se programa para el proximo
/// arranque (MoveFileEx + MOVEFILE_DELAY_UNTIL_REBOOT), asi que termina
/// borrandose al 100%. La limpieza profunda Winapp2 va aparte con FluentCleaner.Core.
public static class TempCleaner
{
    public record CleanResult(int FilesDeleted, long BytesFreed, int ScheduledReboot, long ScheduledBytes, int Errors, List<string> Log, bool Multimedia = false);

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
    /// Se cachea unos segundos: la tarjeta de Inicio lo pide al entrar y Smart
    /// Boost lo consulta cada minuto; recorrer 30.000 archivos cada vez para
    /// pintar una etiqueta hacia que la pantalla tardase en cargar.
    public static (int files, long bytes) MeasureTemp()
    {
        if (_medidaFecha != default && (DateTime.UtcNow - _medidaFecha) < TimeSpan.FromSeconds(20))
            return _medida;

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
        _medida = (files, bytes);
        _medidaFecha = DateTime.UtcNow;
        return _medida;
    }

    private static (int files, long bytes) _medida;
    private static DateTime _medidaFecha;

    /// Se llama al terminar de limpiar para que la proxima medida sea real.
    public static void InvalidarMedida() => _medidaFecha = default;

    /// Tamano de una carpeta concreta (se usa para medir y para pruebas).
    public static (int files, long bytes) MeasureDir(string dir)
    {
        int files = 0; long bytes = 0;
        if (!Directory.Exists(dir)) return (0, 0);
        foreach (var f in SafeEnumerateFiles(dir))
        {
            long len = SizeOf(f);
            if (len > 0) { files++; bytes += len; }
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
        }
        _safeDirs = dirs.ToList();
        return _safeDirs;
    }

    /// Caches de shaders de la GPU (D3D/NVIDIA/AMD). Van APARTE de BuildSafeDirs
    /// porque borrarlas mientras hay un video, un juego o cualquier app D3D en
    /// curso obliga a recompilar shaders -> tiron y, a veces, cuelgue visual.
    private static IEnumerable<string> GpuCacheDirs()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLocal(string local, string sub)
        {
            try { dirs.Add(Path.Combine(local, sub)); } catch { }
        }

        void AddUsuario(string local)
        {
            AddLocal(local, "D3DSCache");
            AddLocal(local, @"NVIDIA\GLCache");
            AddLocal(local, @"NVIDIA\DXCache");
            AddLocal(local, @"AMD\DxCache");
        }

        AddUsuario(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        foreach (var u in UserDirs())
            AddUsuario(Path.Combine(u, "AppData", "Local"));

        return dirs;
    }

    private static List<string>? _safeDirs;

    /// Solo para pruebas de rendimiento: 0 = automatico (4-8 segun el equipo).
    public static int HilosForzados { get; set; }

    /// Tamano del archivo, o -1 si no existe / es reparse / da error.
    public static long SizeOf(string path)
    {
        var m = MetaArchivo(path);
        if (!m.Ok) return -1;
        if ((m.Atributos & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return -1;
        return m.Tamano;
    }

    /// Borra la carpeta SOLO si existe y esta vacia (nunca junctions/reparse ni
    /// zonas protegidas: SafetyGuard lo vuelve a comprobar).
    public static void DeleteIfEmpty(string dir)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return;
            if (!SafetyGuard.CanDeleteEmptyDir(dir)) return;
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }

    public static async Task<CleanResult> QuickCleanAsync(IProgress<string>? progress = null, CancellationToken token = default,
        IEnumerable<string>? carpetas = null, IProgress<double>? avance = null, long totalBytes = 0)
    {
        return await Task.Run(() => Limpiar(progress, token, carpetas, avance, totalBytes), token);
    }

    /// Nucleo de la limpieza rapida. Antes se hacia un archivo a la vez con
    /// FileInfo (Exists + Attributes + LastWriteTime + Length = 3 o 4 stat por
    /// archivo) y encima una comprobacion de existencia despues de borrar. En un
    /// TEMP con decenas de miles de archivos eso son cientos de miles de llamadas
    /// al sistema: se veia como "tarda una eternidad".
    /// Ahora: 1 stat por archivo (GetFileAttributesEx), 0 asignaciones en la
    /// barrera de seguridad, borrado en paralelo (4-8 hilos) y sin verificar los
    /// archivos pequenos (File.Delete ya lanza si falla).
    internal static CleanResult Limpiar(IProgress<string>? progress, CancellationToken token, IEnumerable<string>? carpetas,
        IProgress<double>? avance = null, long totalBytes = 0)
    {
        bool overrideDirs = carpetas is not null;
        // Proteccion multimedia: si hay audio/video/juego en curso, no se tocan
        // las caches de GPU (borrarlas obliga a recompilar shaders -> tiron).
        bool multimedia = !overrideDirs && MediaGuard.EstaActivo();
        var dirs = overrideDirs ? carpetas!.ToList() : new List<string>(BuildSafeDirs());
        if (multimedia) progress?.Report("Multimedia en curso: no se tocan las caches de GPU");
        else if (!overrideDirs) dirs.AddRange(GpuCacheDirs());

        int files = 0, errors = 0, scheduled = 0;
        long bytes = 0, scheduledBytes = 0;
        var log = new List<string>();
        var fallidos = new List<string>();
        // Solo las carpetas donde de verdad se borro algo pueden quedar vacias.
        // Antes se recorria el arbol entero otra vez para buscarlas: en un TEMP con
        // miles de subcarpetas eso era la mayor parte del tiempo de la limpieza.
        var padres = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        int hilos = HilosForzados > 0 ? HilosForzados : Math.Clamp(Environment.ProcessorCount, 4, 8);
        var opciones = new ParallelOptions { MaxDegreeOfParallelism = hilos, CancellationToken = token };

        // Se borra en lotes: la enumeracion queda en un solo hilo (pila, sin
        // bloqueos) y el trabajo de red/antivirus se reparte entre varios.
        var lote = new List<string>(2048);
        int aviso = 0;

        void Borrar(string f)
        {
            var meta = MetaArchivo(f);
            if (!meta.Ok) return;
            if ((meta.Atributos & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly
                                   | FileAttributes.Directory)) != 0) return;
            if (!SafetyGuard.CanDeleteConMetadata(f, meta.Atributos, meta.EscrituraUtc,
                    protegerEjecutables: false, out _)) return;
            try
            {
                // DeleteFileW directo: File.Delete normaliza la ruta y consulta
                // atributos antes de borrar (2-3 syscalls extra por archivo).
                if (!DeleteFileW(f)) File.Delete(f);
                string? padre = Path.GetDirectoryName(f);
                if (padre is not null) padres.TryAdd(padre, 0);
                int n = Interlocked.Increment(ref files);
                Interlocked.Add(ref bytes, meta.Tamano);

                // Solo los grandes se confirman: en un archivo pequeno el stat extra
                // cuesta mas que el propio borrado.
                if (meta.Tamano > 1048576 && File.Exists(f))
                {
                    Interlocked.Decrement(ref files);
                    Interlocked.Add(ref bytes, -meta.Tamano);
                    lock (fallidos) fallidos.Add(f);
                    return;
                }
                if (n % 400 == 0 && Interlocked.Exchange(ref aviso, 1) == 0)
                {
                    long hechos = Interlocked.Read(ref bytes);
                    progress?.Report($"{n} archivos · {hechos / 1048576} MB");
                    if (avance is not null && totalBytes > 0)
                        avance.Report(Math.Min(100.0, hechos * 100.0 / totalBytes));
                    Interlocked.Exchange(ref aviso, 0);
                }
            }
            catch { lock (fallidos) fallidos.Add(f); }
        }

        foreach (var dir in dirs)
        {
            if (token.IsCancellationRequested) break;
            if (!Directory.Exists(dir)) continue;
            progress?.Report($"Limpiando {dir}");
            try
            {
                foreach (var f in EnumerarArchivos(dir))
                {
                    lote.Add(f);
                    if (lote.Count < 2048) continue;
                    Parallel.ForEach(lote, opciones, Borrar);
                    lote.Clear();
                    if (token.IsCancellationRequested) break;
                }
                if (lote.Count > 0 && !token.IsCancellationRequested)
                {
                    Parallel.ForEach(lote, opciones, Borrar);
                    lote.Clear();
                }
            }
            catch (OperationCanceledException) { break; }
            log.Add($"{dir}: OK");
        }

        // Pasada 2: solo los que fallaron. Se reintenta (puede que ya se hayan
        // liberado) y si siguen bloqueados se programan para el proximo arranque.
        if (fallidos.Count > 0)
            progress?.Report($"Reintentando {fallidos.Count} archivos...");
        var yaProgramados = LeerProgramadosParaReinicio();
        foreach (var f in fallidos)
        {
            if (token.IsCancellationRequested) break;
            long len = 0;
            try
            {
                var meta = MetaArchivo(f);
                if (!meta.Ok) continue;
                if (!SafetyGuard.CanDeleteConMetadata(f, meta.Atributos, meta.EscrituraUtc,
                        protegerEjecutables: false, out _)) continue;
                len = meta.Tamano;
                File.Delete(f);
                if (!File.Exists(f)) { files++; bytes += len; continue; }
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

        // Carpetas vacias: solo se sube desde las carpetas que perdieron archivos.
        if (padres.Count > 0)
        {
            progress?.Report("Borrando carpetas vacias...");
            var raices = new HashSet<string>(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (var d in padres.Keys)
                BorrarCarpetasVacias(d, raices);
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
        InvalidarMedida();
        avance?.Report(100);
        return new CleanResult(files, bytes, scheduled, scheduledBytes, errors, log, multimedia);
    }

    // ---- Lectura de metadatos en UNA llamada ----
    // FileInfo.Exists / Attributes / LastWriteTime / Length son 3-4 llamadas al
    // sistema por archivo. GetFileAttributesEx devuelve todo junto: con 60.000
    // temporales la diferencia es de cientos de miles de syscalls.

    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint Atributos;
        public uint CreacionLow, CreacionHigh;
        public uint AccesoLow, AccesoHigh;
        public uint EscrituraLow, EscrituraHigh;
        public uint TamanoHigh, TamanoLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileAttributesExW(string ruta, int nivel, out WIN32_FILE_ATTRIBUTE_DATA datos);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string ruta);

    private readonly record struct MetaDatos(bool Ok, long Tamano, FileAttributes Atributos, DateTime EscrituraUtc);

    private static MetaDatos MetaArchivo(string ruta)
    {
        if (!GetFileAttributesExW(ruta, 0, out var d)) return default;
        long tamano = ((long)d.TamanoHigh << 32) | d.TamanoLow;
        long ft = ((long)d.EscrituraHigh << 32) | d.EscrituraLow;
        DateTime escritura;
        try { escritura = DateTime.FromFileTimeUtc(ft); }
        catch { escritura = DateTime.UtcNow; }
        return new MetaDatos(true, tamano, (FileAttributes)d.Atributos, escritura);
    }

    /// Recorre la carpeta en un solo hilo. Los atributos salen de la propia
    /// enumeracion: no cuesta un stat extra saber si una entrada es carpeta,
    /// archivo o junction.
    private static IEnumerable<string> EnumerarArchivos(string raiz)
    {
        var pendientes = new Stack<string>();
        pendientes.Push(raiz);
        var opciones = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        while (pendientes.Count > 0)
        {
            string actual = pendientes.Pop();
            var archivos = new List<string>(64);
            try
            {
                foreach (var e in new DirectoryInfo(actual).EnumerateFileSystemInfos("*", opciones))
                {
                    FileAttributes attr;
                    try { attr = e.Attributes; } catch { continue; }
                    if ((attr & FileAttributes.Directory) != 0)
                    {
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue; // nunca seguir enlaces
                        pendientes.Push(e.FullName);
                    }
                    else archivos.Add(e.FullName);
                }
            }
            catch { }
            foreach (var f in archivos) yield return f;
        }
    }

    /// Sube desde <paramref name="dir"/> borrando las carpetas que quedaron vacias.
    /// Se detiene al primer directorio que aun tiene algo (o que no se puede borrar,
    /// o que se sale de las raices de limpieza): borrar una carpeta con contenido
    /// fallaria igual, pero asi no se paga el intento.
    private static void BorrarCarpetasVacias(string dir, HashSet<string> raices)
    {
        string? actual = dir;
        while (actual is not null)
        {
            bool dentro = false;
            foreach (var r in raices)
                if (actual.StartsWith(r, StringComparison.OrdinalIgnoreCase)) { dentro = true; break; }
            if (!dentro) return;

            string? padre;
            try
            {
                var di = new DirectoryInfo(actual);
                if (!di.Exists) { padre = Path.GetDirectoryName(actual); actual = padre; continue; }
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return;
                if (Directory.EnumerateFileSystemEntries(actual).Any()) return;
                if (!SafetyGuard.CanDeleteEmptyDir(actual)) return;
                di.Delete();
                padre = Path.GetDirectoryName(actual);
            }
            catch { return; }
            actual = padre;
        }
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
