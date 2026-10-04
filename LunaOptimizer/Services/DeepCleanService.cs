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

public sealed class DeepCatResult
{
    public string Name { get; set; } = "";
    public List<string> Dirs { get; set; } = new();
    public List<string> Files { get; set; } = new();
    public Dictionary<string, long> Sizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(CleanerEntry Entry, ScanResult Scan)> Apps { get; set; } = new();
    public long Bytes { get; set; }
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    public static Task<List<DeepCatResult>> ScanAsync(IProgress<DeepScanUpdate>? progress, CancellationToken token)
        => Task.Run(() => Scan(progress, token), token);

    public static (int files, long bytes) Clean(List<DeepCatResult> cats, IProgress<DeepScanUpdate>? progress, CancellationToken token)
    {
        var ctx = new Ctx(progress);
        int files = 0;
        long bytes = 0;
        for (int i = 0; i < cats.Count; i++)
        {
            if (token.IsCancellationRequested) break;
            var cat = cats[i];
            ctx.Report(DeepPhase.Clean, i, CatState.Cleaning, cat.Name, 0, 0);
            int catFiles = 0;
            long catBytes = 0;
            int target = Math.Max(1, cat.Files.Count);

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
                    long sz = cat.IsApps
                        ? (cat.Sizes.TryGetValue(f, out var s) ? s : 0)
                        : StatSize(f);
                    try { File.Delete(f); catFiles++; catBytes += sz; } catch { }
                    if (catFiles % 200 == 0)
                        ctx.Report(DeepPhase.Clean, i, CatState.Cleaning, Path.GetFileName(f), catBytes, Math.Min(0.99, (double)catFiles / target));
                }
                if (cat.IsApps) RemoveSelfCleanup(cat);
                else EmptyDirs(cat);
            }

            files += catFiles;
            bytes += catBytes;
            ctx.Done++;
            ctx.Report(DeepPhase.Clean, i, CatState.Done, "", catBytes, 1);
        }
        LastClean = DateTime.Now;
        return (files, bytes);
    }

    public static string? LoadWinapp2()
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
            catch { i++; continue; }

            foreach (var f in r.FilesToDelete)
            {
                if (!ctx.Seen.Add(f)) continue;
                long sz = r.FileSizes.TryGetValue(f, out var s) ? s : 0;
                cat.Files.Add(f);
                cat.Sizes[f] = sz;
                cat.Bytes += sz;
                ctx.Total += sz;
                ctx.Files++;
            }
            cat.Apps.Add((e, r));
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

    private static long StatSize(string path)
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

    private static List<string> RecommendedDirs()
    {
        var dirs = new List<string>();
        try { dirs.Add(Path.GetTempPath()); } catch { }
        try { dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")); } catch { }
        try
        {
            foreach (var u in Directory.GetDirectories(@"C:\Users"))
            {
                var t = Path.Combine(u, "AppData", "Local", "Temp");
                if (Directory.Exists(t)) dirs.Add(t);
            }
        }
        catch { }
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
        try
        {
            foreach (var u in Directory.GetDirectories(@"C:\Users"))
            {
                var rec = Path.Combine(u, "AppData", "Roaming", "Microsoft", "Windows", "Recent");
                if (Directory.Exists(rec)) dirs.Add(rec);
            }
        }
        catch { }
        return dirs;
    }

    private static void EmptyDirs(DeepCatResult cat)
    {
        foreach (var dir in cat.Dirs)
        {
            foreach (var d in TempCleaner.SafeEnumerateDirs(dir))
            {
                try
                {
                    if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch { }
            }
        }
    }

    private static void RemoveSelfCleanup(DeepCatResult cat)
    {
        try
        {
            var expander = new PathExpander();
            foreach (var (entry, _) in cat.Apps)
            {
                foreach (var fk in entry.FileKeys)
                {
                    if (fk.Flag != FileKeyFlag.RemoveSelf) continue;
                    foreach (var d in expander.ResolvePaths(fk.Path))
                    {
                        try
                        {
                            if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
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
