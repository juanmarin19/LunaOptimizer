using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LunaOptimizer.Services;

/// Memoria en 3 etapas, misma idea que RAMMap/CleanMem (codigo propio MIT,
/// no copiado de GPL):
///   1) EmptyWorkingSet por proceso (working set -> standby)
///   2) lista modificada al pagefile (modified -> standby)
///   3) standby -> libre, lo que Task Manager cuenta como RAM libre
/// Medicion real de la RAM "en uso" (total - disponible) antes y despues.
public static class MemoryBoost
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// Capea el working set en sus limites por defecto: sin esto, las paginas
    /// recortadas vuelven a cargarse al instante cuando la app toca esa
    /// memoria y el boost "no se nota". -1/-1 = limites por defecto del kernel.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    /// Cache de archivos del sistema: -1/-1 vacia la cache (API documentada).
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemFileCacheSize(IntPtr dwMinimumFileCacheSize, IntPtr dwMaximumFileCacheSize, int Flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int command, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const int SE_PRIVILEGE_ENABLED = 0x00000002;
    private const string SeProfileSingleProcess = "SeProfileSingleProcessPrivilege";

    private const int SystemMemoryListInformation = 80;
    private const int MemoryEmptyWorkingSets = 2;
    private const int MemoryFlushModifiedList = 3;
    private const int MemoryPurgeStandbyList = 4;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
    }

    public static (int trimmed, long gainedMB, long usoAntesMB, long usoDespuesMB, string log, bool multimedia) Boost(bool forzar = false)
    {
        var (totalBefore, freeBefore) = ProcessService.GetMemory();
        long usoAntes = Math.Max(0, totalBefore - freeBefore);
        int trimmed = 0;
        var log = new System.Text.StringBuilder();

        // No interrumpir multimedia. Purgar la lista standby, vaciar los working sets
        // de TODO el sistema o tirar la cache de archivos mientras se ve un video (o
        // se juega) deja al reproductor sin memoria ni cache: tiene que releer todo de
        // disco -> tirones y, con decodificacion por hardware, cuelgues. Igual que PC
        // Manager: si hay algo reproduciendose, no se toca la memoria. `forzar` lo
        // salta en acciones explicitas (modo juego).
        if (!forzar && MediaGuard.EstaActivo())
        {
            log.AppendLine("Multimedia en curso (video/juego/musica): se omite la liberacion de RAM.");
            GuardarLog(log.ToString());
            return (0, 0, usoAntes, usoAntes, log.ToString(), true);
        }

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                // lista compartida con la pestana de procesos (dwm, csrss, svchost, ...)
                if (ProcessService.IsProtected(p.ProcessName, p.Id)) continue;
                if (EmptyWorkingSet(p.Handle))
                {
                    // Capping: sin esto el kernel recarga las paginas en cuanto
                    // la app toca esa memoria y el boost no se nota.
                    SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1));
                    trimmed++;
                }
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        log.AppendLine($"Etapa 1 working set: {trimmed} procesos recortados");

        // Las etapas 2..4 necesitan SeProfileSingleProcessPrivilege; sin ella
        // (app no elevada) se continuan igual y solo se informa el fallo.
        bool privilegio = HabilitarPrivilegio(SeProfileSingleProcess);
        string tag = privilegio ? "" : " (sin privilegio)";
        Thread.Sleep(100);

        // Etapa 2: lista modificada (paginas sucias -> escritas -> standby).
        var (ok2, st2) = EjecutarComando(MemoryFlushModifiedList);
        log.AppendLine($"Etapa 2 lista modificada: {(ok2 ? "OK" : $"fallo NTSTATUS=0x{st2:X8}")}{tag}");

        Thread.Sleep(100);

        // Etapa 3: working sets DEL SISTEMA (dwm, csrss, svchost...): es la
        // lista que esta app no puede tocar por nombre porque son criticos.
        // En 16 GB son varios cientos de MB que un EmptyWorkingSet por proceso
        // no alcanza porque el kernel solo vacia lo que puede tocar.
        var (ok3, st3) = EjecutarComando(MemoryEmptyWorkingSets);
        log.AppendLine($"Etapa 3 working sets del sistema: {(ok3 ? "OK" : $"fallo NTSTATUS=0x{st3:X8}")}{tag}");

        Thread.Sleep(100);

        // Etapa 4: standby -> libre (lo que Task Manager cuenta como RAM libre).
        var (ok4, st4) = EjecutarComando(MemoryPurgeStandbyList);
        log.AppendLine($"Etapa 4 standby -> libre: {(ok4 ? "OK" : $"fallo NTSTATUS=0x{st4:X8}")}{tag}");

        Thread.Sleep(100);

        // Etapa 5: cache de archivos del sistema. En equipos con mucho I/O es
        // 1-2 GB. API documentada; -1/-1 = sin limites (vacia la cache).
        bool ok5 = SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0);
        log.AppendLine($"Etapa 5 cache de archivos: {(ok5 ? "OK" : "no soportada/fallo")}{tag}");

        // Re-leer despues de TODAS las etapas.
        var (totalAfter, freeAfter) = ProcessService.GetMemory();
        long gained = Math.Max(0, freeAfter - freeBefore);
        long usoDespues = Math.Max(0, totalAfter - freeAfter);

        log.AppendLine($"RAM en uso: {usoAntes} MB -> {usoDespues} MB");
        log.AppendLine($"RAM disponible: {freeBefore} MB -> {freeAfter} MB");
        GuardarLog(log.ToString());
        return (trimmed, gained, usoAntes, usoDespues, log.ToString(), false);
    }

    /// Deja constancia de las 3 etapas en %LocalAppData%\LunaOptimizer\boost.log
    /// (igual que tema.log: sirve para comprobar si el privilegio estuvo activo).
    private static void GuardarLog(string texto)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaOptimizer");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "boost.log"),
                $"[{DateTime.Now:HH:mm:ss}] {texto.Replace("\r\n", " | ")}{Environment.NewLine}");
        }
        catch { }
    }

    /// Habilita un privilegio del token actual (la app ya corre elevada).
    private static bool HabilitarPrivilegio(string nombre)
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES, out var token))
                return false;
            try
            {
                if (!LookupPrivilegeValue(null, nombre, out var luid)) return false;
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    /// NtSetSystemInformation(SystemMemoryListInformation, comando) -> (ok, NTSTATUS).
    private static (bool ok, int status) EjecutarComando(int comando)
    {
        try { int st = NtSetSystemInformation(SystemMemoryListInformation, ref comando, 4); return (st >= 0, st); }
        catch (Exception ex) { return (false, ex.HResult); }
    }
}
