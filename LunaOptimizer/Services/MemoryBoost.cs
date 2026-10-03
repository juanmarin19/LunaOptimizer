using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LunaOptimizer.Services;

/// Memoria: EmptyWorkingSet por proceso + medicion real de RAM disponible.
/// Codigo propio MIT inspirado en la idea de WinMemoryCleaner (no copiado, GPL).
public static class MemoryBoost
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static (int trimmed, long freedMB, string log) Boost(bool aggressive = false)
    {
        var (totalBefore, freeBefore) = ProcessService.GetMemory();
        int trimmed = 0;
        var log = new System.Text.StringBuilder();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                // lista compartida con la pestaña de procesos (dwm, csrss, svchost, ...)
                if (ProcessService.IsProtected(p.ProcessName, p.Id)) continue;
                if (EmptyWorkingSet(p.Handle)) trimmed++;
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Medicion real de la RAM disponible (antes/desues), no el GC de este proceso.
        var (totalAfter, freeAfter) = ProcessService.GetMemory();
        long gainedMB = Math.Max(0, freeAfter - freeBefore);

        log.AppendLine($"Procesos optimizados: {trimmed}");
        log.AppendLine($"RAM disponible: {freeBefore} MB -> {freeAfter} MB");
        log.AppendLine($"Ganancia: {gainedMB} MB de {totalAfter} MB totales");
        return (trimmed, gainedMB, log.ToString());
    }
}
