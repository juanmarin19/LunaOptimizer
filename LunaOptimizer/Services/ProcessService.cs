using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using LunaOptimizer.Models;

namespace LunaOptimizer.Services;

public static class IconHelper
{
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, System.Text.StringBuilder lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static BitmapSource? Extract(string exePath)
    {
        try
        {
            var sb = new System.Text.StringBuilder(exePath, 260);
            ushort idx = 0;
            IntPtr hIcon = ExtractAssociatedIcon(IntPtr.Zero, sb, ref idx);
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(20, 20));
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(hIcon); }
        }
        catch { return null; }
    }
}

public static class ProcessService
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "csrss", "wininit", "services", "lsass", "smss", "winlogon",
        "dwm", "system", "registry", "idle", "fontdrvhost", "wdf01000",
        "svchost", "memory compression", "sihost", "taskhostw", "audiodg",
        "securityhealthservice", "wudfhost",
    };

    private static readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    /// Hosts de Windows que aunque se puedan cerrar no son aplicaciones del
    /// usuario (los mismos que omite PC Manager), mas este propio programa.
    private static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "textinputhost", "searchhost", "searchui", "startmenuexperiencehost",
        "shellexperiencehost", "applicationframehost", "runtimebroker",
        "widgets", "peopleexperiencehost", "securityhealthsystray",
    };

    private static bool NotAnApp(string name)
        => NotApps.Contains(name) ||
           name.StartsWith("LunaOptimizer", StringComparison.OrdinalIgnoreCase);

    /// Procesos que tienen al menos una ventana visible: son las
    /// "aplicaciones" que el usuario puede ver y cerrar con seguridad.
    public static HashSet<int> VisibleWindowPids()    {
        var set = new HashSet<int>();
        try
        {
            EnumWindows((h, l) =>
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid > 0) set.Add((int)pid);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return set;
    }

    /// Apps empaquetadas (Microsoft Store): PC Manager las lista aunque ahora
    /// no tengan ventana visible (Photos, Xbox...). Su exe vive en WindowsApps.
    private static bool IsPackagedApp(string path)
        => !string.IsNullOrEmpty(path) &&
           path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);

    public static List<ProcessInfo> GetProcesses(string filter = "", bool onlySafe = true)
    {
        var list = new List<ProcessInfo>();
        var winPids = onlySafe ? VisibleWindowPids() : null;
        foreach (var p in Process.GetProcesses())
        {
            string name;
            double ram;
            string path;
            try
            {
                name = p.ProcessName;
                ram = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 1);
                try { path = p.MainModule?.FileName ?? ""; }
                catch { path = ""; } // MainModule suele fallar sin admin: no descartar el proceso
            }
            catch
            {
                try { p.Dispose(); } catch { }
                continue;
            }
            // Solo lo que se puede ver y cerrar: ni del sistema, ni de fondo.
            bool canKill = !ProtectedNames.Contains(name) && p.Id != Environment.ProcessId;
            if (onlySafe &&
                (!canKill || !(winPids!.Contains(p.Id) || IsPackagedApp(path)) || NotAnApp(name)))
            { try { p.Dispose(); } catch { } continue; }
            if (!string.IsNullOrWhiteSpace(filter) &&
                !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            { try { p.Dispose(); } catch { } continue; }
            string status = "OK";
            try { if (p.Responding == false) status = "No responde"; } catch { }
            BitmapSource? icon = null;
            if (!string.IsNullOrEmpty(path))
                icon = GetIconCached(path);
            list.Add(new ProcessInfo { Pid = p.Id, Name = name, RamMB = ram, Status = status, Path = path, Icon = icon, CanKill = canKill });
            try { p.Dispose(); } catch { }
        }
        return list.OrderByDescending(x => x.RamMB).ToList();
    }

    private static BitmapSource? GetIconCached(string exePath)
    {
        if (_iconCache.TryGetValue(exePath, out var cached)) return cached;
        var bmp = IconHelper.Extract(exePath);
        if (_iconCache.Count < 500) _iconCache[exePath] = bmp;
        return bmp;
    }

    public static bool IsProtected(string name, int pid)
        => ProtectedNames.Contains(name) || pid == Environment.ProcessId;

    public static List<ProcessGroup> GetGroups(string filter = "", bool onlySafe = true)
    {
        // En modo seguro: primero descubre QUE programas cuentan (al menos un
        // proceso con ventana o app empaquetada) y luego agrupa TODOS los
        // procesos de esos nombres, como hace PC Manager (Brave 2.3 GB = todos
        // los brave.exe, no solo el que tiene ventana).
        var all = GetProcesses(filter, onlySafe: false);
        if (onlySafe)
        {
            var winPids = VisibleWindowPids();
            var appNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in all)
            {
                if (ProtectedNames.Contains(p.Name) || NotAnApp(p.Name)) continue;
                if (p.Pid != Environment.ProcessId &&
                    (winPids.Contains(p.Pid) || IsPackagedApp(p.Path)))
                    appNames.Add(p.Name);
            }
            all = all.Where(p => appNames.Contains(p.Name)).ToList();
        }
        return all.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var pids = g.Select(p => p.Pid).OrderBy(i => i).ToList();
                return new ProcessGroup
                {
                    Name = first.Name,
                    Count = g.Count(),
                    TotalRamMB = Math.Round(g.Sum(p => p.RamMB), 1),
                    Pids = string.Join(", ", pids.Take(8)) + (pids.Count > 8 ? "..." : ""),
                    Status = g.Any(p => p.Status != "OK") ? "No responde" : "OK",
                    Path = first.Path,
                    Icon = first.Icon,
                    CanKill = g.All(p => p.CanKill),
                    PidList = pids,
                };
            })
            .OrderByDescending(g => g.TotalRamMB)
            .ToList();
    }

    /// Mata por NOMBRE re-resolviendo los PIDs en el momento.
    /// Los PIDs guardados al refrescar (hasta 2 s antes) pueden reciclarse y
    /// apuntar a otro programa: por eso no se usan los de la lista.
    public static (int killed, int failed) KillGroup(ProcessGroup g)
    {
        if (ProtectedNames.Contains(g.Name)) return (0, 0);
        Process[] targets;
        try { targets = Process.GetProcessesByName(g.Name); }
        catch { return (0, 1); }
        int killed = 0, failed = 0;
        foreach (var p in targets)
        {
            try
            {
                if (IsProtected(p.ProcessName, p.Id)) { failed++; continue; }
                if (KillCore(p)) killed++; else failed++;
            }
            catch { failed++; }
            finally { try { p.Dispose(); } catch { } }
        }
        return (killed, failed);
    }

    public static (bool ok, string msg) KillProcess(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            try
            {
                string name = p.ProcessName;
                if (IsProtected(name, p.Id))
                    return (false, $"Protegido: no se puede cerrar {name} del sistema.");
                if (!KillCore(p))
                    return (false, $"No se pudo terminar {name} ({pid}).");
                p.WaitForExit(3000);
                return (true, $"{name} ({pid}) terminado.");
            }
            finally { try { p.Dispose(); } catch { } }
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// Cierra el arbol completo (procesos hijo incluidos) y si no, al proceso suelto.
    private static bool KillCore(Process p)
    {
        try { p.Kill(entireProcessTree: true); return true; }
        catch
        {
            try { p.Kill(); return true; }
            catch { return false; }
        }
    }

    private const string MEM_DLL = "kernel32.dll";

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport(MEM_DLL, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// RAM total y disponible (MB). GlobalMemoryStatusEx: sin WMI, sin admin,
    /// sin coste por consulta — se llama cada pocos segundos desde el temporizador.
    public static (long totalMB, long freeMB) GetMemory()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
        if (GlobalMemoryStatusEx(ref ms))
            return ((long)(ms.ullTotalPhys / 1048576UL), (long)(ms.ullAvailPhys / 1048576UL));
        try { return (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576, 0); }
        catch { return (0, 0); }
    }
}
