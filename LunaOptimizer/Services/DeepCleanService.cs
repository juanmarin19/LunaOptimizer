using System.IO;
using System.Runtime.InteropServices;
using FluentCleaner.Models;
using FluentCleaner.Services;

namespace LunaOptimizer.Services;

public enum CatState { Waiting, Scanning, Cleaning, Done }
public enum DeepPhase { Scan, Clean }

public sealed class DeepScanUpdate
{
    public DeepPhase Phase { get; set; }
    public int CatIndex { get; set; }
    public CatState State { get; set; }
    public string Current { get; set; } = "";
    public long CatBytes { get; set; }
    public long TotalBytes { get; set; }
    public int TotalFiles { get; set; }
    public double Percent { get; set; }
}

/// Una app de Winapp2 dentro de la limpieza profunda, con su propia casilla:
/// el usuario puede excluirla sin descartar el resto de la categoria.
public sealed class DeepAppItem
{
    public FluentCleaner.Models.CleanerEntry Entry { get; init; } = null!;
    public FluentCleaner.Models.ScanResult Scan { get; init; } = null!;
    public bool Incluida { get; set; } = true;
    public string Nombre => Entry.Name;
    /// Solo los archivos que esta app aporta de verdad (sin repetir los que otra
    /// app ya habia contado): asi la suma de las apps coincide con el total.
    public long Bytes { get; set; }
    public int Archivos { get; set; }
    public int Registro => Scan.RegistryToDelete.Count;
}

public sealed class DeepCatResult
{
    public string Name { get; set; } = "";
    /// Si es false, la categoria entera se salta al limpiar.
    public bool Incluida { get; set; } = true;
    public List<string> Dirs { get; set; } = new();
    public List<string> Files { get; set; } = new();
    public Dictionary<string, long> Sizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DeepAppItem> Apps { get; set; } = new();
    public long Bytes { get; set; }
    public int RegistryCount { get; set; }   // claves/valores de registro a borrar (no cuentan bytes)
    public bool IsRecycle { get; set; }
    public bool IsApps { get; set; }
}

public static class DeepCleanService
{
    public static readonly string[] CatNames =
    {
        "Elementos recomendados",
        "Otros elementos del sistema",
        "Otros elementos de la aplicacion",
        "Seguimientos de uso del equipo",
        "Papelera de reciclaje",
    };

    public static DateTime? LastClean { get; set; }

    /// Totales de lo que el usuario dejo marcado (la interfaz muestra esto, no el
    /// total del analisis: si destilda una app o una categoria, los numeros bajan).
    public static (long bytes, int files, int regs) TotalesSeleccionados(IEnumerable<DeepCatResult> cats)
    {
        long bytes = 0; int files = 0, regs = 0;
        foreach (var c in cats)
        {
            if (!c.Incluida) continue;
            if (c.IsApps)
            {
                foreach (var app in c.Apps.Where(a => a.Incluida))
                {
                    bytes += app.Bytes;
                    files += app.Archivos;
                    regs += app.Registro;
                }
            }
            else
            {
                bytes += c.Bytes;
                files += c.Files.Count;
            }
        }
        return (bytes, files, regs);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    public static Task<List<DeepCatResult>> ScanAsync(IProgress<DeepScanUpdate>? progress, CancellationToken token)
        => Task.Run(() => Scan(progress, token), token);

    public static (int files, long bytes, int regs) Clean(List<DeepCatResult> cats, IProgress<DeepScanUpdate>? progress, CancellationToken token)
    {
        var ctx = new Ctx(progress);
        int files = 0, regs = 0;
        long bytes = 0;
        for (int i = 0; i < cats.Count; i++)
        {
            if (token.IsCancellationRequested) break;
            var cat = cats[i];
            if (!cat.Incluida) { ctx.Done++; ctx.Report(DeepPhase.Clean, i, CatState.Done, "", 0, 1); continue; }

            ctx.Report(DeepPhase.Clean, i, CatState.Cleaning, cat.Name, 0, 0);
            int catFiles = 0;
            long catBytes = 0;

            // En la categoria de apps, el usuario puede haber desmarcado alguna:
            // solo se borran los archivos de las que siguen incluidas.
            HashSet<string>? permitidos = null;
            if (cat.IsApps)
            {
                permitidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var app in cat.Apps.Where(a => a.Incluida))
                    foreach (var f in app.Scan.FilesToDelete)
                        permitidos.Add(f);
            }
            int target = Math.Max(1, permitidos?.Count ?? cat.Files.Count);

            if (cat.IsRecycle)
            {
                int hr;
                try { hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); }
                catch { hr = -1; }
                if (hr == 0)
                {
                    catFiles = cat.Files.Count;
                    catBytes = cat.Bytes;
                }
                else
                {
                    foreach (var f in cat.Files)
                    {
                        if (token.IsCancellationRequested) break;
                        long sz = StatSize(f);
                        try { File.Delete(f); catFiles++; catBytes += sz; } catch { }
                    }
                }
            }
            else
            {
                foreach (var f in cat.Files)
                {
                    if (token.IsCancellationRequested) break;
                    if (permitidos is not null && !permitidos.Contains(f)) continue;
                    // Barrera de seguridad: las rutas de Winapp2 pueden apuntar a
                    // cualquier lado; nunca borramos zonas criticas, enlaces ni
                    // archivos recien modificados. En las apps ademas protegemos los
                    // binarios (.exe/.dll): si una regla los pide, es mas probable
                    // que la regla este mal que otra cosa.
                    if (!SafetyGuard.CanDelete(f, out _, protegerEjecutables: cat.IsApps)) continue;
                    long sz = cat.IsApps
                        ? (cat.Sizes.TryGetValue(f, out var s) ? s : 0)
                        : StatSize(f);
                    try { File.Delete(f); catFiles++; catBytes += sz; } catch { }
                    if (catFiles % 200 == 0)
                        ctx.Report(DeepPhase.Clean, i, CatState.Cleaning, Path.GetFileName(f), catBytes, Math.Min(0.99, (double)catFiles / target));
                }
                if (cat.IsApps)
                {
                    RemoveSelfCleanup(cat);
                    regs += DeleteRegistry(cat);
                }
                else EmptyDirs(cat);
            }

            files += catFiles;
            bytes += catBytes;
            ctx.Done++;
            ctx.Report(DeepPhase.Clean, i, CatState.Done, "", catBytes, 1);
        }
        if (!token.IsCancellationRequested) LastClean = DateTime.Now;
        return (files, bytes, regs);
    }

    // El ini (1,8 MB) se lee una vez y se reutiliza: antes se recargaba en cada
    // analisis (pestana Limpieza + limpieza profunda).
    private static readonly object _winapp2Lock = new();
    private static string? _winapp2Cache;
    private static bool _winapp2Loaded;

    public static string? LoadWinapp2()
    {
        if (_winapp2Loaded) return _winapp2Cache;
        lock (_winapp2Lock)
        {
            if (!_winapp2Loaded)
            {
                _winapp2Cache = LoadWinapp2Core();
                _winapp2Loaded = true;
            }
            return _winapp2Cache;
        }
    }

    private static string? LoadWinapp2Core()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("LunaOptimizer.Winapp2.ini");
            if (s is not null)
            {
                using var r = new StreamReader(s);
                return r.ReadToEnd();
            }
        }
        catch { }

        try
        {
            var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Winapp2.ini");
            if (File.Exists(local)) return File.ReadAllText(local);
        }
        catch { }

        // en desarrollo: sube desde bin\ hasta encontrarlo (sobrevive a mover la carpeta del proyecto)
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            try
            {
                var f = Path.Combine(dir.FullName, "Winapp2.ini");
                if (File.Exists(f)) return File.ReadAllText(f);
            }
            catch { }
        }
        return null;
    }

    private static List<DeepCatResult> Scan(IProgress<DeepScanUpdate>? progress, CancellationToken token)
    {
        var ctx = new Ctx(progress);
        var cats = new List<DeepCatResult>();

        var rec = new DeepCatResult { Name = CatNames[0], Dirs = RecommendedDirs() };
        cats.Add(rec);
        ScanDirCat(rec, 0, ctx, token);
        Check(token);

        var sys = new DeepCatResult { Name = CatNames[1], Dirs = SystemDirs() };
        cats.Add(sys);
        ScanDirCat(sys, 1, ctx, token);
        Check(token);

        var apps = new DeepCatResult { Name = CatNames[2], IsApps = true };
        cats.Add(apps);
        ScanAppsCat(apps, 2, ctx, token);
        Check(token);

        var trace = new DeepCatResult { Name = CatNames[3], Dirs = TraceDirs() };
        cats.Add(trace);
        ScanDirCat(trace, 3, ctx, token);
        Check(token);

        var bin = new DeepCatResult { Name = CatNames[4], IsRecycle = true };
        cats.Add(bin);
        ScanRecycleCat(bin, 4, ctx, token);

        ctx.Report(DeepPhase.Scan, -1, CatState.Done, "", 0, 1);
        return cats;
    }

    private static void Check(CancellationToken token)
    {
        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
    }

    private static void ScanDirCat(DeepCatResult cat, int idx, Ctx ctx, CancellationToken token)
    {
        ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, "", 0, 0);
        int scanned = 0;
        foreach (var dir in cat.Dirs)
        {
            if (token.IsCancellationRequested) return;
            foreach (var f in TempCleaner.SafeEnumerateFiles(dir))
            {
                if (token.IsCancellationRequested) return;
                long len = StatSize(f);
                if (len < 0) continue;
                if (!ctx.Seen.Add(f)) continue;
                cat.Files.Add(f);
                cat.Sizes[f] = len;
                cat.Bytes += len;
                ctx.Total += len;
                ctx.Files++;
                scanned++;
                if (scanned % 250 == 0)
                    ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, Path.GetFileName(f), cat.Bytes,
                        Math.Min(0.95, Math.Sqrt(scanned / 5000.0)));
            }
        }
        ctx.Done++;
        ctx.Report(DeepPhase.Scan, idx, CatState.Done, "", cat.Bytes, 1);
    }

    private static void ScanAppsCat(DeepCatResult cat, int idx, Ctx ctx, CancellationToken token)
    {
        ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, "", 0, 0);
        var content = LoadWinapp2();
        if (content is null)
        {
            ctx.Done++;
            ctx.Report(DeepPhase.Scan, idx, CatState.Done, "", 0, 1);
            return;
        }

        List<CleanerEntry> entries;
        try
        {
            var parser = new Winapp2Parser();
            var detect = new DetectionService();
            entries = parser.Parse(content).Where(e => detect.IsInstalled(e)).ToList();
        }
        catch
        {
            entries = new List<CleanerEntry>();
        }

        int i = 0;
        foreach (var e in entries)
        {
            if (token.IsCancellationRequested) return;
            ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, e.Name, cat.Bytes,
                entries.Count == 0 ? 1 : (double)i / entries.Count);
            ScanResult r;
            try { r = ScanEngine.AnalyzeAsync(e, null, token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppLog.Error(ex, "Analizando regla: " + e.Name); i++; continue; }

            var item = new DeepAppItem { Entry = e, Scan = r };
            foreach (var f in r.FilesToDelete)
            {
                long sz = r.FileSizes.TryGetValue(f, out var s) ? s : 0;
                if (!ctx.Seen.Add(f)) continue;   // ya lo conto otra app
                cat.Files.Add(f);
                cat.Sizes[f] = sz;
                cat.Bytes += sz;
                ctx.Total += sz;
                ctx.Files++;
                item.Bytes += sz;
                item.Archivos++;
            }
            cat.RegistryCount += r.RegistryToDelete.Count;
            cat.Apps.Add(item);
            i++;
        }
        ctx.Done++;
        ctx.Report(DeepPhase.Scan, idx, CatState.Done, "", cat.Bytes, 1);
    }

    private static void ScanRecycleCat(DeepCatResult cat, int idx, Ctx ctx, CancellationToken token)
    {
        ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, "", 0, 0);
        int scanned = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var rb = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            if (!Directory.Exists(rb)) continue;
            foreach (var f in TempCleaner.SafeEnumerateFiles(rb))
            {
                if (token.IsCancellationRequested) return;
                long len = StatSize(f);
                if (len < 0) continue;
                if (!ctx.Seen.Add(f)) continue;
                cat.Files.Add(f);
                cat.Sizes[f] = len;
                cat.Bytes += len;
                ctx.Total += len;
                ctx.Files++;
                scanned++;
                if (scanned % 250 == 0)
                    ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, Path.GetFileName(f), cat.Bytes,
                        Math.Min(0.95, Math.Sqrt(scanned / 2000.0)));
            }
        }
        ctx.Done++;
        ctx.Report(DeepPhase.Scan, idx, CatState.Done, "", cat.Bytes, 1);
    }

    private static long StatSize(string path) => TempCleaner.SizeOf(path);

    private static List<string> RecommendedDirs()
    {
        var dirs = new List<string>();
        try { dirs.Add(Path.GetTempPath()); } catch { }
        try { dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")); } catch { }
        foreach (var u in TempCleaner.UserDirs())
        {
            var t = Path.Combine(u, "AppData", "Local", "Temp");
            if (Directory.Exists(t)) dirs.Add(t);
        }
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> SystemDirs()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new List<string>
        {
            Path.Combine(win, "SoftwareDistribution", "Download"),
            Path.Combine(local, @"Microsoft\Windows\INetCache"),
            Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
            Path.Combine(local, @"Microsoft\Windows\WER"),
            Path.Combine(common, @"Microsoft\Windows\WER\ReportArchive"),
            Path.Combine(common, @"Microsoft\Windows\WER\ReportQueue"),
        };
    }

    private static List<string> TraceDirs()
    {
        var dirs = new List<string>();
        foreach (var u in TempCleaner.UserDirs())
        {
            var rec = Path.Combine(u, "AppData", "Roaming", "Microsoft", "Windows", "Recent");
            if (Directory.Exists(rec)) dirs.Add(rec);
        }
        return dirs;
    }

    private static void EmptyDirs(DeepCatResult cat)
    {
        foreach (var dir in cat.Dirs)
            foreach (var d in TempCleaner.SafeEnumerateDirs(dir))
                TempCleaner.DeleteIfEmpty(d);
    }

    private static void RemoveSelfCleanup(DeepCatResult cat)
    {
        try
        {
            var expander = new PathExpander();
            foreach (var app in cat.Apps.Where(a => a.Incluida))
            {
                foreach (var fk in app.Entry.FileKeys)
                {
                    if (fk.Flag != FileKeyFlag.RemoveSelf) continue;
                    foreach (var d in expander.ResolvePaths(fk.Path))
                        TempCleaner.DeleteIfEmpty(d);
                }
            }
        }
        catch { }
    }

    /// Borra las claves/valores de registro que Winapp2 marca (RegKeyN) para las
    /// apps analizadas. No cuentan como bytes, pero forman parte de la limpieza.
    /// Devuelve cuantas entradas se intentaron borrar (para el resumen).
    private static int DeleteRegistry(DeepCatResult cat)
    {
        int n = 0;
        foreach (var app in cat.Apps.Where(a => a.Incluida))
        {
            n += app.Scan.RegistryToDelete.Count;
            try { ScanEngine.DeleteRegistry(app.Scan); }
            catch (Exception ex) { AppLog.Error(ex, "Borrando registro (limpieza profunda)"); }
        }
        return n;
    }

    private sealed class Ctx
    {
        private readonly IProgress<DeepScanUpdate>? _progress;
        public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
        public long Total;
        public int Files;
        public int Done;

        public Ctx(IProgress<DeepScanUpdate>? progress) => _progress = progress;

        public void Report(DeepPhase phase, int idx, CatState state, string current, long catBytes, double frac)
            => _progress?.Report(new DeepScanUpdate
            {
                Phase = phase,
                CatIndex = idx,
                State = state,
                Current = current,
                CatBytes = catBytes,
                TotalBytes = Total,
                TotalFiles = Files,
                Percent = Math.Min(100, Done * 20 + frac * 20),
            });
    }
}
