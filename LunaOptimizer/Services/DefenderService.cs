using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace LunaOptimizer.Services;

public class DefenderEstado
{
    public bool Disponible { get; set; }
    public bool Antivirus { get; set; }
    public bool TiempoReal { get; set; }
    public DateTime? Firmas { get; set; }
    public DateTime? UltimoExamen { get; set; }
    public string? Error { get; set; }

    public string FirmasTexto => Firmas is null ? "—" : Firmas.Value.ToString("dd/MM/yyyy");
    public string UltimoExamenTexto => UltimoExamen is null ? "Nunca" : UltimoExamen.Value.ToString("dd/MM/yyyy HH:mm");
    public int DiasSinFirmas => Firmas is null ? 999 : (int)(DateTime.Now - Firmas.Value).TotalDays;
}

/// <summary>
/// Antivirus de Windows: estado (proteccion en tiempo real, firmas) y examen
/// rapido. Usa Get-MpComputerStatus y MpCmdRun.exe, las herramientas propias de
/// Defender; si el equipo usa otro antivirus se informa y no se toca nada.
/// </summary>
public static class DefenderService
{
    private static Process? _examen;

    public static async Task<DefenderEstado> GetEstadoAsync(CancellationToken token = default)
    {
        var estado = new DefenderEstado();
        try
        {
            string salida = await RunPowerShellAsync(
                "try { Get-MpComputerStatus | Select-Object AntivirusEnabled,RealTimeProtectionEnabled," +
                "AntivirusSignatureLastUpdated,QuickScanEndTime,FullScanEndTime | ConvertTo-Json -Compress } " +
                "catch { '{}' }", token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(salida) ? "{}" : salida);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("AntivirusEnabled", out var av))
            {
                estado.Error = "Windows Defender no responde";
                return estado;
            }
            estado.Disponible = true;
            estado.Antivirus = av.ValueKind == JsonValueKind.True;
            estado.TiempoReal = r.TryGetProperty("RealTimeProtectionEnabled", out var rt) && rt.ValueKind == JsonValueKind.True;
            estado.Firmas = Fecha(r, "AntivirusSignatureLastUpdated");
            estado.UltimoExamen = Fecha(r, "QuickScanEndTime") ?? Fecha(r, "FullScanEndTime");
            if (!estado.Antivirus)
                estado.Error = "Otro antivirus esta activo";
        }
        catch (Exception ex)
        {
            estado.Error = ex.Message;
            AppLog.Error(ex, "estado de Defender");
        }
        return estado;
    }

    private static DateTime? Fecha(JsonElement r, string propiedad)
    {
        try
        {
            if (!r.TryGetProperty(propiedad, out var v)) return null;
            if (v.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                return d.ToLocalTime();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Examen rapido. Devuelve el codigo de MpCmdRun (0 = limpio, 2 = amenazas).
    /// Se puede cancelar: se mata el proceso de MpCmdRun.
    /// </summary>
    public static async Task<(int codigo, string salida)> ExamenRapidoAsync(CancellationToken token)
    {
        string? exe = RutaMpCmdRun();
        if (exe is null) return (-1, "No se encontro MpCmdRun.exe (Defender no esta instalado).");

        try
        {
            var psi = new ProcessStartInfo(exe, "-Scan -ScanType 1")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var p = new Process { StartInfo = psi };
            _examen = p;
            if (!p.Start()) return (-1, "No se pudo iniciar el examen.");

            using var reg = token.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });

            var salida = new System.Text.StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) salida.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) salida.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            await p.WaitForExitAsync(token).ConfigureAwait(false);
            return (p.ExitCode, salida.ToString().Trim());
        }
        catch (OperationCanceledException) { return (-2, "Examen cancelado."); }
        catch (Exception ex)
        {
            AppLog.Error(ex, "examen rapido");
            return (-1, "Error: " + ex.Message);
        }
        finally { _examen = null; }
    }

    public static void Cancelar()
    {
        try { if (_examen is { HasExited: false }) _examen.Kill(true); } catch { }
    }

    private static string? RutaMpCmdRun()
    {
        try
        {
            string directo = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Windows Defender", "MpCmdRun.exe");
            if (File.Exists(directo)) return directo;

            string plataforma = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows Defender", "Platform");
            if (Directory.Exists(plataforma))
            {
                var carpeta = Directory.GetDirectories(plataforma)
                    .Select(d => new { Ruta = d, Nombre = Path.GetFileName(d) })
                    .Where(d => Version.TryParse(d.Nombre, out _))
                    .OrderByDescending(d => Version.Parse(d.Nombre))
                    .FirstOrDefault();
                if (carpeta is not null)
                {
                    string exe = Path.Combine(carpeta.Ruta, "MpCmdRun.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch { }
        return null;
    }

    public static async Task<string> RunPowerShellAsync(string comando, CancellationToken token, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + comando.Replace("\"", "\\\"") + "\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = new Process { StartInfo = psi };
        if (!p.Start()) return "";
        string salida = await p.StandardOutput.ReadToEndAsync(token).ConfigureAwait(false);
        _ = p.StandardError.ReadToEndAsync(token);
        using var cts = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token);
        try { await p.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        return salida.Trim();
    }
}
