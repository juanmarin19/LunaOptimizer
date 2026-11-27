using System.IO;
using System.Runtime.InteropServices;
using FluentCleaner.Models;

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

/// Un elemento concreto de la limpieza: "Caché de Chrome", "Caché de miniaturas",
/// "Archivos temporales"... Es lo que el usuario ve y puede destildar.
public sealed class DeepItem
{
    public string Id { get; init; } = "";
    public string Nombre { get; init; } = "";
    public string Icono { get; init; } = "I.File";
    public bool Incluida { get; set; } = true;
    public List<string> Files { get; } = new();
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long Bytes { get; set; }
    public int Registro { get; set; }
    public bool EsPapelera { get; init; }
    public bool RequiereSinMultimedia { get; set; }
    /// Aviso bajo el nombre (p. ej. "no se borra con multimedia en curso").
    public string? Nota { get; set; }
    public ScanResult? Scan { get; init; }

    public int Archivos => Files.Count;
    public bool TieneAlgo => Bytes > 0 || Registro > 0 || Files.Count > 0;
}

/// Nivel intermedio del arbol: "Elementos de la aplicación" / "Elementos del sistema".
public sealed class DeepSubCat
{
    public string Nombre { get; init; } = "";
    public List<DeepItem> Items { get; } = new();
    public bool Incluida { get; set; } = true;
    public long Bytes => Items.Sum(i => i.Bytes);
    public long BytesSel => Items.Where(i => i.Incluida).Sum(i => i.Bytes);
    public int RegistroSel => Items.Where(i => i.Incluida).Sum(i => i.Registro);
    public bool HayAlgo => Items.Any(i => i.TieneAlgo);
}

/// Nivel superior: las cinco categorias de siempre.
public sealed class DeepCatResult
{
    public string Nombre { get; init; } = "";
    public List<DeepSubCat> Subs { get; } = new();
    public List<DeepItem> Items { get; } = new();
    public bool Incluida { get; set; } = true;

    public IEnumerable<DeepItem> Todos => Items.Concat(Subs.SelectMany(s => s.Items));
    public long Bytes => Todos.Sum(i => i.Bytes);
    public long BytesSel => Items.Where(i => i.Incluida).Sum(i => i.Bytes)
                         + Subs.Where(s => s.Incluida).SelectMany(s => s.Items)
                               .Where(i => i.Incluida).Sum(i => i.Bytes);
    public int Archivos => Todos.Sum(i => i.Archivos);
    public int ArchivosSel => Seleccionados().Sum(i => i.Archivos);
    public int Registro => Todos.Sum(i => i.Registro);
    public int RegistroSel => Seleccionados().Sum(i => i.Registro);
    public bool TieneAlgo => Todos.Any(i => i.TieneAlgo);

    /// Items que se borrarian ahora mismo (categoria, subcategoria e item marcados).
    public IEnumerable<DeepItem> Seleccionados()
    {
        if (!Incluida) yield break;
        foreach (var i in Items) if (i.Incluida) yield return i;
        foreach (var s in Subs)
        {
            if (!s.Incluida) continue;
            foreach (var i in s.Items) if (i.Incluida) yield return i;
        }
    }
}

public static class DeepCleanService
{
    public static readonly string[] CatNames =
    {
        "Elementos recomendados",
        "Otros elementos del sistema",
        "Seguimientos de uso del equipo",
        "Papelera de reciclaje",
    };

    public static DateTime? LastClean { get; set; }

    /// Totales de lo que quedo marcado (es lo que muestran la cabecera y el boton).
    public static (long bytes, int files, int regs) TotalesSeleccionados(IEnumerable<DeepCatResult> cats)
    {
        long bytes = 0; int files = 0, regs = 0;
        foreach (var c in cats)
        {
            if (!c.Incluida) continue;
            foreach (var i in c.Seleccionados())
            {
                bytes += i.Bytes;
                files += i.Archivos;
                regs += i.Registro;
            }
        }
        return (bytes, files, regs);
    }

    /// Deja solo los elementos con algo que borrar (los de 0 bytes solo hacen ruido).
    private static void QuitarVacios(DeepSubCat sub)
    {
        for (int i = sub.Items.Count - 1; i >= 0; i--)
            if (!sub.Items[i].TieneAlgo) sub.Items.RemoveAt(i);
    }

    private static void QuitarVacios(DeepCatResult cat)
    {
        for (int i = cat.Items.Count - 1; i >= 0; i--)
            if (!cat.Items[i].TieneAlgo) cat.Items.RemoveAt(i);
    }

    public static Task<List<DeepCatResult>> ScanAsync(IProgress<DeepScanUpdate>? progress, CancellationToken token)
        => Task.Run(() => Scan(progress, token), token);

    // ===================== Analisis =====================

    private static List<DeepCatResult> Scan(IProgress<DeepScanUpdate>? progress, CancellationToken token)
    {
        var ctx = new Ctx(progress);
        var cats = new List<DeepCatResult>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Elementos recomendados = aplicaciones + sistema
        var rec = new DeepCatResult { Nombre = CatNames[0] };
        var subApps = new DeepSubCat { Nombre = "Elementos de la aplicación" };
        var subSys = new DeepSubCat { Nombre = "Elementos del sistema" };
        rec.Subs.Add(subApps);
        rec.Subs.Add(subSys);
        cats.Add(rec);

        EscanearApps(subApps, cats.Count - 1, ctx, vistos, token);
        Check(token);
        EscanearSistema(subSys, cats.Count - 1, ctx, vistos, token);
        Check(token);

        // 2) Otros elementos del sistema
        var otros = new DeepCatResult { Nombre = CatNames[1] };
        cats.Add(otros);
        EscanearOtros(otros, cats.Count - 1, ctx, vistos, token);
        Check(token);

        // 3) Seguimientos de uso
        var trazas = new DeepCatResult { Nombre = CatNames[2] };
        cats.Add(trazas);
        EscanearTrazas(trazas, cats.Count - 1, ctx, vistos, token);
        Check(token);

        // 4) Papelera
        var papelera = new DeepCatResult { Nombre = CatNames[3] };
        cats.Add(papelera);
        EscanearPapelera(papelera, cats.Count - 1, ctx, token);

        ctx.Report(DeepPhase.Scan, -1, CatState.Done, "", 0, 1);
        return cats;
    }

    private static void Check(CancellationToken token)
    {
        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
    }

    private static void EscanearApps(DeepSubCat sub, int idx, Ctx ctx, HashSet<string> vistos, CancellationToken token)
    {
        ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, "aplicaciones", 0, 0);
        var content = LoadWinapp2();
        if (content is null) return;

        List<CleanerEntry> entries;
        try
        {
            var parser = new FluentCleaner.Services.Winapp2Parser();
            var detect = new FluentCleaner.Services.DetectionService();
            entries = parser.Parse(content).Where(e => detect.IsInstalled(e)).ToList();
        }
        catch { return; }

        int i = 0;
        foreach (var e in entries)
        {
            if (token.IsCancellationRequested) return;
            i++;
            if (i % 5 == 0)
                ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, e.Name, 0,
                    Math.Min(0.9, (double)i / Math.Max(1, entries.Count)));

            // Las reglas de componentes de Windows (Update, logs, papelera, installer,
            // prefetch...) no son "aplicaciones": ya las cubren los elementos del
            // sistema, y ofrecerlas aqui llenaba la lista de filas irrelevantes.
            if (EsComponenteDelSistema(e.Name)) continue;

            ScanResult r;
            try { r = ScanEngine.AnalyzeAsync(e, null, token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppLog.Error(ex, "Analizando regla: " + e.Name); continue; }

            var item = new DeepItem
            {
                Id = "app:" + e.Name,
                Nombre = NombreApp(e.Name),
                Icono = "I.Grid",
                Scan = r,
                Registro = r.RegistryToDelete.Count,
            };
            foreach (var f in r.FilesToDelete)
            {
                long sz = r.FileSizes.TryGetValue(f, out var s) ? s : 0;
                if (!vistos.Add(f)) continue;    // ya lo conto otra regla
                // La vista previa solo ofrece lo que de verdad se puede borrar.
                if (!SafetyGuard.CanDelete(f, out _, protegerEjecutables: true)) continue;
                item.Files.Add(f);
                item.Sizes[f] = sz;
                item.Bytes += sz;
                ctx.Total += sz;
                ctx.Files++;
            }

            // Solo se ofrecen apps con algo que borrar: sin esto aparecen decenas
            // de reglas en 0KB y la lista se vuelve inservible.
            if (item.Bytes > 0) sub.Items.Add(item);
        }

        QuitarVacios(sub);

        // Las mas pesadas primero: es lo que el usuario espera ver arriba.
        var ordenados = sub.Items.OrderByDescending(i => i.Bytes).ToList();
        sub.Items.Clear();
        sub.Items.AddRange(ordenados);
    }

    private static bool EsComponenteDelSistema(string nombre)
    {
        string n = nombre.ToLowerInvariant();
        if (n.StartsWith("windows ")) return true;
        if (n.StartsWith("microsoft windows")) return true;
        if (n.StartsWith(".net")) return true;
        foreach (var palabra in new[]
                 {
                     "event log", "recycle bin", "temporary files", "registry", "installer",
                     "update", " logs", "error reporting", "prefetch", "delivery optimization",
                 })
            if (n.Contains(palabra, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string NombreApp(string nombre)
    {
        // PC Manager lo llama "Caché de <app>". Las reglas de Winapp2 ya vienen como
        // "Google Chrome Caches" o "Caché de .NET Framework": se normaliza el nombre.
        string n = nombre.Trim();
        foreach (var suf in new[] { " Caches", " Cache", " Caché", " caches", " cache" })
        {
            if (n.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
            {
                n = n[..^suf.Length].TrimEnd(' ', '-', ':');
                break;
            }
        }
        if (n.Length == 0) return nombre;
        if (n.StartsWith("Caché de ", StringComparison.OrdinalIgnoreCase)) return n;
        return "Caché de " + n;
    }

    private static void EscanearSistema(DeepSubCat sub, int idx, Ctx ctx, HashSet<string> vistos, CancellationToken token)
    {
        ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, "sistema", 0, 0);

        // Archivos temporales: TEMP del usuario, de cada perfil y el de Windows.
        var temps = new List<string>();
        try { temps.Add(Path.GetTempPath()); } catch { }
        try { temps.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")); } catch { }
        foreach (var u in TempCleaner.UserDirs())
        {
            var t = Path.Combine(u, "AppData", "Local", "Temp");
            if (Directory.Exists(t)) temps.Add(t);
        }
        sub.Items.Add(ItemDeCarpetas("temp", "Archivos temporales", "I.Clock", temps, vistos, ctx, idx, token));
        Check(token);

        // Archivos temporales de Internet (INetCache de cada perfil).
        var inet = new List<string>();
        foreach (var u in TempCleaner.UserDirs())
        {
            var p = Path.Combine(u, @"AppData\Local\Microsoft\Windows\INetCache");
            if (Directory.Exists(p)) inet.Add(p);
        }
        sub.Items.Add(ItemDeCarpetas("inet", "Archivos temporales de Internet", "I.Monitor", inet, vistos, ctx, idx, token));
        Check(token);

        // Cache de miniaturas del Explorador.
        var mini = new List<string>();
        foreach (var u in TempCleaner.UserDirs())
        {
            var p = Path.Combine(u, @"AppData\Local\Microsoft\Windows\Explorer");
            if (Directory.Exists(p)) mini.Add(p);
        }
        var itemMini = ItemDeCarpetas("thumbs", "Caché de miniaturas", "I.Grid", mini, vistos, ctx, idx, token,
            filtro: f => Path.GetFileName(f).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase)
                      || Path.GetFileName(f).StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase));
        sub.Items.Add(itemMini);
        Check(token);

        // Cache de sombreadores (GPU): no se borra si hay video/juego en curso.
        var shaders = new List<string>();
        foreach (var u in TempCleaner.UserDirs())
        {
            var local = Path.Combine(u, "AppData", "Local");
            shaders.Add(Path.Combine(local, "D3DSCache"));
            shaders.Add(Path.Combine(local, @"NVIDIA\GLCache"));
            shaders.Add(Path.Combine(local, @"NVIDIA\DXCache"));
            shaders.Add(Path.Combine(local, @"AMD\DxCache"));
        }
        var itemShader = ItemDeCarpetas("shaders", "Caché de sombreadores", "I.Flame", shaders, vistos, ctx, idx, token);
        itemShader.RequiereSinMultimedia = true;
        if (MediaGuard.EstaActivo())
        {
            itemShader.Incluida = false;
            itemShader.Nota = "En uso: se omite mientras haya vídeo o juego";
        }
        sub.Items.Add(itemShader);
        QuitarVacios(sub);
    }

    private static void EscanearOtros(DeepCatResult cat, int idx, Ctx ctx, HashSet<string> vistos, CancellationToken token)
    {
        ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, "sistema", 0, 0);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        cat.Items.Add(ItemDeCarpetas("wu", "Archivos de Windows Update", "I.Download",
            new[] { Path.Combine(win, @"SoftwareDistribution\Download") }, vistos, ctx, idx, token));
        Check(token);

        cat.Items.Add(ItemDeCarpetas("do", "Optimización de entrega", "I.Download",
            new[] { Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") },
            vistos, ctx, idx, token));
        Check(token);

        cat.Items.Add(ItemDeCarpetas("wer", "Informes de errores de Windows", "I.Alert",
            new[]
            {
                Path.Combine(local, @"Microsoft\Windows\WER"),
                Path.Combine(common, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Combine(common, @"Microsoft\Windows\WER\ReportQueue"),
            }, vistos, ctx, idx, token));
        QuitarVacios(cat);
    }

    private static void EscanearTrazas(DeepCatResult cat, int idx, Ctx ctx, HashSet<string> vistos, CancellationToken token)
    {
        ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, "historial", 0, 0);
        var dirs = new List<string>();
        foreach (var u in TempCleaner.UserDirs())
        {
            var rec = Path.Combine(u, @"AppData\Roaming\Microsoft\Windows\Recent");
            if (Directory.Exists(rec)) dirs.Add(rec);
        }
        cat.Items.Add(ItemDeCarpetas("recent", "Accesos recientes", "I.Clock", dirs, vistos, ctx, idx, token));
        QuitarVacios(cat);
    }

    private static void EscanearPapelera(DeepCatResult cat, int idx, Ctx ctx, CancellationToken token)
    {
        ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, "papelera", 0, 0);
        var item = new DeepItem { Id = "recycle", Nombre = "Archivos en la papelera", Icono = "I.Trash", EsPapelera = true };
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (token.IsCancellationRequested) return;
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var rb = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                if (!Directory.Exists(rb)) continue;
                foreach (var f in TempCleaner.SafeEnumerateFiles(rb))
                {
                    long len = TempCleaner.SizeOf(f);
                    if (len < 0) continue;
                    item.Files.Add(f);
                    item.Sizes[f] = len;
                    item.Bytes += len;
                    ctx.Total += len;
                    ctx.Files++;
                }
            }
            catch (Exception ex) { AppLog.Error(ex, "papelera " + drive.Name); }
        }
        cat.Items.Add(item);
    }

    /// Junta los archivos de varias carpetas en un item, sin repetir los ya vistos.
    private static DeepItem ItemDeCarpetas(string id, string nombre, string icono, IEnumerable<string> dirs,
        HashSet<string> vistos, Ctx ctx, int idx, CancellationToken token, Func<string, bool>? filtro = null)
    {
        var item = new DeepItem { Id = id, Nombre = nombre, Icono = icono };
        foreach (var dir in dirs)
        {
            if (token.IsCancellationRequested) return item;
            if (!Directory.Exists(dir)) continue;
            foreach (var f in TempCleaner.SafeEnumerateFiles(dir))
            {
                if (token.IsCancellationRequested) return item;
                if (filtro is not null && !filtro(f)) continue;
                if (!vistos.Add(f)) continue;
                long len = TempCleaner.SizeOf(f);
                if (len < 0) continue;
                item.Files.Add(f);
                item.Sizes[f] = len;
                item.Bytes += len;
                ctx.Total += len;
                ctx.Files++;
            }
            ctx.Report(DeepPhase.Scan, idx, CatState.Scanning, Path.GetFileName(dir), 0,
                Math.Min(0.9, (double)item.Files.Count / Math.Max(1, item.Files.Count + 200)));
        }
        return item;
    }

    // ===================== Winapp2 embebido =====================

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

    // ===================== Limpieza =====================

    public static (int files, long bytes, int regs) Clean(List<DeepCatResult> cats,
        IProgress<DeepScanUpdate>? progress, CancellationToken token)
    {
        var ctx = new Ctx(progress);
        int files = 0, regs = 0;
        long bytes = 0;
        bool multimedia = MediaGuard.EstaActivo();

        for (int c = 0; c < cats.Count; c++)
        {
            if (token.IsCancellationRequested) break;
            var cat = cats[c];
            ctx.Report(DeepPhase.Clean, c, CatState.Cleaning, cat.Nombre, 0, 0);
            if (!cat.Incluida) { ctx.Done++; ctx.Report(DeepPhase.Clean, c, CatState.Done, "", 0, 1); continue; }

            long catBytes = 0;
            int catFiles = 0;
            foreach (var item in cat.Seleccionados())
            {
                if (token.IsCancellationRequested) break;
                // Las caches de GPU no se tocan si hay video/juego/musica en curso.
                if (item.RequiereSinMultimedia && multimedia) continue;

                if (item.EsPapelera)
                {
                    int hr;
                    try { hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); }
                    catch { hr = -1; }
                    if (hr == 0)
                    {
                        catFiles += item.Files.Count;
                        catBytes += item.Bytes;
                    }
                    else
                    {
                        foreach (var f in item.Files)
                        {
                            if (token.IsCancellationRequested) break;
                            if (!SafetyGuard.CanDelete(f, out _)) continue;
                            try { File.Delete(f); catFiles++; catBytes += item.Sizes.TryGetValue(f, out var s) ? s : 0; } catch { }
                        }
                    }
                    continue;
                }

                foreach (var f in item.Files)
                {
                    if (token.IsCancellationRequested) break;
                    if (!SafetyGuard.CanDelete(f, out _, protegerEjecutables: item.Scan is not null)) continue;
                    long sz = item.Sizes.TryGetValue(f, out var s) ? s : TempCleaner.SizeOf(f);
                    try { File.Delete(f); catFiles++; catBytes += sz; } catch { }
                }

                // Registro: solo lo que marcan las reglas de Winapp2 (ScanEngine protege ramas criticas).
                if (item.Scan is not null && item.Registro > 0)
                {
                    try { ScanEngine.DeleteRegistry(item.Scan); regs += item.Registro; }
                    catch (Exception ex) { AppLog.Error(ex, "Borrando registro (limpieza profunda)"); }
                }

                VaciarCarpetas(item);
            }

            files += catFiles;
            bytes += catBytes;
            ctx.Done++;
            ctx.Report(DeepPhase.Clean, c, CatState.Done, "", catBytes, 1);
        }

        if (!token.IsCancellationRequested) LastClean = DateTime.Now;
        return (files, bytes, regs);
    }

    private static void VaciarCarpetas(DeepItem item)
    {
        try
        {
            var padres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in item.Files)
            {
                var p = Path.GetDirectoryName(f);
                if (p is not null) padres.Add(p);
            }
            foreach (var d in padres.OrderByDescending(x => x.Length))
                TempCleaner.DeleteIfEmpty(d);
        }
        catch { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    // ===================== Estado de progreso =====================

    private sealed class Ctx
    {
        private readonly IProgress<DeepScanUpdate>? _progress;
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
                Percent = Math.Min(100, Done * 25 + frac * 25),
            });
    }
}
