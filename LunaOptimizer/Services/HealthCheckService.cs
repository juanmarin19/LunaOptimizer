using System.IO;
using Microsoft.Win32;

namespace LunaOptimizer.Services;

public enum AccionRevision
{
    Ninguna,
    LimpiarTemporales,
    AbrirArranque,
    AbrirAlmacenamiento,
    AbrirDefender,
    AbrirLimpieza,
}

/// <summary>Una linea de la revisión de salud (estilo "Health Check" de PC Manager).</summary>
public class RevisionItem
{
    public string Id { get; set; } = "";
    public string Titulo { get; set; } = "";
    public string Detalle { get; set; } = "";
    /// <summary>0 = correcto, 1 = aviso, 2 = problema.</summary>
    public int Nivel { get; set; }
    public AccionRevision Accion { get; set; } = AccionRevision.Ninguna;
    public string AccionTexto { get; set; } = "";

    public string Etiqueta => Nivel switch
    {
        2 => "Requiere atencion",
        1 => "Recomendado",
        _ => "Correcto",
    };
}

public class RevisionResultado
{
    public List<RevisionItem> Items { get; } = new();
    public int Puntuacion { get; set; } = 100;
    public DateTime Fecha { get; set; } = DateTime.Now;

    public int Problemas => Items.Count(i => i.Nivel == 2);
    public int Avisos => Items.Count(i => i.Nivel == 1);
    public bool TodoBien => Problemas == 0 && Avisos == 0;
    public string Resumen => Problemas > 0
        ? $"{Problemas} problema{(Problemas == 1 ? "" : "s")} encontrado{(Problemas == 1 ? "" : "s")}"
        : Avisos > 0
            ? $"{Avisos} recomendacion{(Avisos == 1 ? "" : "es")}"
            : "Todo en orden";
}

/// <summary>
/// Revisión de salud: reune en una sola pasada los mismos controles que hace
/// PC Manager (temporales, memoria, disco, arranque, antivirus, limpieza y
/// reinicio pendiente) y devuelve una puntuacion con acciones para arreglarlos.
/// Todo son lecturas; no cambia nada por si sola.
/// </summary>
public static class HealthCheckService
{
    public static async Task<RevisionResultado> RevisarAsync(
        IProgress<string>? progreso = null, CancellationToken token = default)
    {
        var r = new RevisionResultado();
        int descuento = 0;

        // 1) Archivos temporales
        progreso?.Report("Comprobando archivos temporales...");
        long tempBytes = 0;
        try { (_, tempBytes) = await Task.Run(TempCleaner.MeasureTemp, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Error(ex, "revision temporales"); }
        r.Items.Add(new RevisionItem
        {
            Id = "temp",
            Titulo = "Archivos temporales",
            Detalle = tempBytes > 0
                ? $"{TempCleaner.FormatBytes(tempBytes)} que se pueden liberar"
                : "Sin archivos temporales acumulados",
            Nivel = tempBytes > 1073741824 ? 2 : tempBytes > 209715200 ? 1 : 0,
            Accion = tempBytes > 104857600 ? AccionRevision.LimpiarTemporales : AccionRevision.Ninguna,
            AccionTexto = tempBytes > 104857600 ? "Limpiar" : "",
        });
        if (tempBytes > 1073741824) descuento += 12;
        else if (tempBytes > 209715200) descuento += 6;

        token.ThrowIfCancellationRequested();

        // 2) Memoria en uso
        progreso?.Report("Comprobando memoria...");
        try
        {
            var (total, libre) = ProcessService.GetMemory();
            int uso = total > 0 ? (int)Math.Round((total - libre) * 100.0 / total) : 0;
            r.Items.Add(new RevisionItem
            {
                Id = "ram",
                Titulo = "Memoria RAM",
                Detalle = total > 0 ? $"{uso}% en uso · {libre / 1024.0:F1} GB libres de {total / 1024.0:F0} GB" : "No disponible",
                Nivel = uso >= 90 ? 2 : uso >= 78 ? 1 : 0,
                Accion = uso >= 85 ? AccionRevision.Ninguna : AccionRevision.Ninguna,
                AccionTexto = "",
            });
            if (uso >= 90) descuento += 10;
            else if (uso >= 78) descuento += 5;
        }
        catch (Exception ex) { AppLog.Error(ex, "revision memoria"); }

        // 3) Disco del sistema
        progreso?.Report("Comprobando almacenamiento...");
        try
        {
            string raiz = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
            var d = new DriveInfo(raiz);
            double librePct = d.TotalSize > 0 ? d.AvailableFreeSpace * 100.0 / d.TotalSize : 100;
            double libreGB = d.AvailableFreeSpace / 1073741824.0;
            r.Items.Add(new RevisionItem
            {
                Id = "disk",
                Titulo = "Espacio en disco",
                Detalle = $"{libreGB:F1} GB libres ({librePct:F0}%) en {d.Name.TrimEnd('\\')}",
                Nivel = librePct < 10 ? 2 : librePct < 20 ? 1 : 0,
                Accion = librePct < 20 ? AccionRevision.AbrirAlmacenamiento : AccionRevision.Ninguna,
                AccionTexto = librePct < 20 ? "Ver almacenamiento" : "",
            });
            if (librePct < 10) descuento += 12;
            else if (librePct < 20) descuento += 6;
        }
        catch (Exception ex) { AppLog.Error(ex, "revision disco"); }

        // 4) Aplicaciones de arranque
        progreso?.Report("Revisando aplicaciones de arranque...");
        try
        {
            var inicio = await Task.Run(StartupService.GetItems, token).ConfigureAwait(false);
            int activas = inicio.Count(i => i.Habilitado && !i.Propio);
            r.Items.Add(new RevisionItem
            {
                Id = "startup",
                Titulo = "Aplicaciones de arranque",
                Detalle = activas == 0 ? "Ninguna aplicacion se inicia con Windows"
                    : $"{activas} aplicacion{(activas == 1 ? "" : "es")} se inicia{(activas == 1 ? "" : "n")} con Windows",
                Nivel = activas > 18 ? 2 : activas > 10 ? 1 : 0,
                Accion = activas > 6 ? AccionRevision.AbrirArranque : AccionRevision.Ninguna,
                AccionTexto = activas > 6 ? "Revisar" : "",
            });
            if (activas > 18) descuento += 10;
            else if (activas > 10) descuento += 5;
        }
        catch (Exception ex) { AppLog.Error(ex, "revision arranque"); }

        token.ThrowIfCancellationRequested();

        // 5) Antivirus de Windows
        progreso?.Report("Comprobando la proteccion antivirus...");
        try
        {
            var def = await DefenderService.GetEstadoAsync(token).ConfigureAwait(false);
            if (!def.Disponible)
            {
                r.Items.Add(new RevisionItem
                {
                    Id = "defender",
                    Titulo = "Proteccion antivirus",
                    Detalle = def.Error ?? "Windows Defender no disponible",
                    Nivel = 1,
                    Accion = AccionRevision.AbrirDefender,
                    AccionTexto = "Abrir Seguridad de Windows",
                });
                descuento += 4;
            }
            else
            {
                int nivel = !def.TiempoReal ? 2 : def.DiasSinFirmas > 7 ? 1 : 0;
                r.Items.Add(new RevisionItem
                {
                    Id = "defender",
                    Titulo = "Proteccion antivirus",
                    Detalle = !def.TiempoReal
                        ? "La proteccion en tiempo real esta desactivada"
                        : $"Activa · firmas del {def.FirmasTexto} · ultimo examen: {def.UltimoExamenTexto}",
                    Nivel = nivel,
                    Accion = nivel > 0 ? AccionRevision.AbrirDefender : AccionRevision.Ninguna,
                    AccionTexto = nivel > 0 ? "Revisar" : "",
                });
                if (!def.TiempoReal) descuento += 12;
                else if (def.DiasSinFirmas > 7) descuento += 4;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Error(ex, "revision defender"); }

        // 6) Ultima limpieza
        DateTime? ultima = DeepCleanService.LastClean;
        if (ultima is null)
        {
            r.Items.Add(new RevisionItem
            {
                Id = "clean",
                Titulo = "Limpieza del sistema",
                Detalle = "Todavia no se ha hecho una limpieza profunda",
                Nivel = 1,
                Accion = AccionRevision.AbrirLimpieza,
                AccionTexto = "Limpiar",
            });
            descuento += 3;
        }
        else
        {
            int dias = (int)(DateTime.Now - ultima.Value).TotalDays;
            r.Items.Add(new RevisionItem
            {
                Id = "clean",
                Titulo = "Limpieza del sistema",
                Detalle = $"Ultima limpieza: {ultima.Value:dd/MM/yyyy HH:mm}",
                Nivel = dias > 30 ? 1 : 0,
                Accion = dias > 30 ? AccionRevision.AbrirLimpieza : AccionRevision.Ninguna,
                AccionTexto = dias > 30 ? "Limpiar" : "",
            });
            if (dias > 30) descuento += 3;
        }

        // 7) Reinicio pendiente
        if (ReinicioPendiente(out string motivo))
        {
            r.Items.Add(new RevisionItem
            {
                Id = "reboot",
                Titulo = "Reinicio pendiente",
                Detalle = motivo,
                Nivel = 1,
                Accion = AccionRevision.Ninguna,
                AccionTexto = "",
            });
            descuento += 4;
        }

        r.Puntuacion = Math.Clamp(100 - descuento, 0, 100);
        SettingsStore.Current.UltimaRevision = r.Fecha.ToString("o");
        SettingsStore.Current.UltimaPuntuacion = r.Puntuacion;
        SettingsStore.Save();
        progreso?.Report("Revisión completada.");
        return r;
    }

    private static bool ReinicioPendiente(out string motivo)
    {
        motivo = "";
        try
        {
            using (var cbs = Registry.LocalMachine.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (cbs is not null) { motivo = "Windows necesita reiniciar para terminar una actualizacion"; return true; }

            using (var wu = Registry.LocalMachine.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (wu is not null) { motivo = "Hay una actualizacion esperando un reinicio"; return true; }
        }
        catch { }
        return false;
    }
}
