using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LunaOptimizer.Services;

public sealed record CierreResultado(int Cerrados, int Fallidos, int Total, string Detalle);

/// <summary>
/// Cierre de aplicaciones igual que Microsoft PC Manager:
///
///  1. Se cierra la aplicacion COMPLETA: el proceso y todos sus hijos (incluidos
///     los que se lanzaron despues, tipo los subprocesos de Chrome o Brave).
///  2. Primero se pide un cierre ordenado (WM_CLOSE a sus ventanas) para que
///     guarden su estado; si no responden en 2,5 s se fuerza.
///  3. El cierre forzado usa un **Job Object**: se meten todos los PIDs en el job
///     y se termina el job. Asi no quedan procesos huerfanos ni "ventanas
///     fantasma" como cuando se mata uno por uno, y es lo mismo que hace PC
///     Manager (CreateJobObject + AssignProcessToJobObject + TerminateJobObject).
/// </summary>
public static class AppCloser
{
    private const uint WM_CLOSE = 0x0010;

    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint SYNCHRONIZE = 0x00100000;

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr security, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    /// Mapa pid -> pid del padre (una sola pasada de Toolhelp).
    private static Dictionary<int, int> MapaPadres()
    {
        var mapa = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return mapa;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
            if (!Process32First(snap, ref e)) return mapa;
            do
            {
                mapa[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
            } while (Process32Next(snap, ref e));
        }
        catch { }
        finally { CloseHandle(snap); }
        return mapa;
    }

    /// Arbol completo: los pids dados y todos sus descendientes.
    public static HashSet<int> ArbolCompleto(IEnumerable<int> pids)
    {
        var mapa = MapaPadres();
        var objetivo = new HashSet<int>(pids.Where(p => p > 0));
        bool crecio = true;
        while (crecio && mapa.Count > 0)
        {
            crecio = false;
            foreach (var (pid, padre) in mapa)
            {
                if (objetivo.Contains(padre) && objetivo.Add(pid)) crecio = true;
            }
        }
        return objetivo;
    }

    /// Aviso de cierre ordenado a las ventanas de esos procesos.
    private static void PedirCierre(HashSet<int> pids)
    {
        try
        {
            EnumWindows((h, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pids.Contains((int)pid)) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    /// true si la ventana principal del proceso no responde (mismo criterio que
    /// el Administrador de tareas, IsHungAppWindow).
    public static bool EstaColgado(int pid)
    {
        bool colgado = false;
        try
        {
            EnumWindows((h, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(h, out uint p);
                    if ((int)p != pid) return true;
                    if (IsHungAppWindow(h)) { colgado = true; return false; }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return colgado;
    }

    public static CierreResultado Cerrar(string nombre, IReadOnlyCollection<int> pids, bool ordenado = true)
    {
        // Si el proceso pedido es del sistema, se rechaza ENTERO: si solo se quitara
        // el de la lista, sus hijos (que pueden ser tus programas) se cerrarian igual.
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (ProcessService.IsProtected(p.ProcessName, pid))
                    return new CierreResultado(0, 0, 0, $"{p.ProcessName} es un proceso del sistema: no se cierra.");
            }
            catch { }
        }

        var objetivo = ArbolCompleto(pids);
        objetivo.Remove(Environment.ProcessId);

        // Ningun descendiente protegido: se quita el y todo su subarbol.
        bool quitado = true;
        while (quitado)
        {
            quitado = false;
            foreach (var pid in objetivo.ToList())
            {
                string n;
                try { using var p = Process.GetProcessById(pid); n = p.ProcessName; }
                catch { objetivo.Remove(pid); quitado = true; continue; }

                if (!ProcessService.IsProtected(n, pid)) continue;
                var subarbol = ArbolCompleto(new[] { pid });
                foreach (var d in subarbol) quitado |= objetivo.Remove(d);
            }
        }

        int total = objetivo.Count;
        if (total == 0) return new CierreResultado(0, 0, 0, $"{nombre} ya estaba cerrado.");

        if (ordenado)
        {
            PedirCierre(objetivo);
            int limite = Environment.TickCount + 2500;
            while (Environment.TickCount < limite)
            {
                if (objetivo.All(pid => !SigueVivo(pid))) break;
                Thread.Sleep(100);
            }
        }

        var vivos = objetivo.Where(SigueVivo).ToList();
        int cerrados = total - vivos.Count;

        if (vivos.Count > 0)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job != IntPtr.Zero)
            {
                try
                {
                    foreach (var pid in vivos)
                    {
                        IntPtr h = OpenProcess(PROCESS_TERMINATE | PROCESS_SET_QUOTA | SYNCHRONIZE, false, pid);
                        if (h == IntPtr.Zero) continue;
                        try { AssignProcessToJobObject(job, h); }
                        catch { }
                        finally { CloseHandle(h); }
                    }
                    TerminateJobObject(job, 1);
                }
                finally { CloseHandle(job); }
            }

            // Red de seguridad: los que no se pudieron meter en el job (permisos).
            foreach (var pid in vivos.Where(SigueVivo))
            {
                IntPtr h = OpenProcess(PROCESS_TERMINATE, false, pid);
                if (h == IntPtr.Zero) continue;
                try { TerminateProcess(h, 1); } catch { }
                finally { CloseHandle(h); }
            }

            int limite = Environment.TickCount + 1500;
            while (Environment.TickCount < limite && vivos.Any(SigueVivo)) Thread.Sleep(50);
        }

        int fallidos = objetivo.Count(SigueVivo);
        cerrados = objetivo.Count - fallidos;
        string detalle = fallidos == 0
            ? $"{nombre}: {cerrados} proceso(s) cerrados."
            : $"{nombre}: {cerrados} cerrados, {fallidos} no se pudieron cerrar (protegidos o en uso).";
        return new CierreResultado(cerrados, fallidos, total, detalle);
    }

    private static bool SigueVivo(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }
}
