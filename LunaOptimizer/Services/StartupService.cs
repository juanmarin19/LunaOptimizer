using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LunaOptimizer.Services;

/// <summary>
/// Aplicaciones que se inician con Windows (equivalente a "Arranque" de PC Manager).
/// Lee las claves Run (usuario y equipo, 32/64 bits), las carpetas Inicio y el
/// registro StartupApproved que usa el propio Administrador de tareas para saber
/// que esta habilitado y que no. Deshabilitar NO borra nada: escribe el mismo
/// valor binario que escribe Windows (02 = activo, 03 = desactivado).
/// </summary>
public class StartupItem
{
    public string Nombre { get; set; } = "";
    public string Comando { get; set; } = "";
    public string Origen { get; set; } = "";     // "Usuario", "Equipo", "Carpeta Inicio"
    public string Exe { get; set; } = "";
    public bool Habilitado { get; set; }
    public bool Propio { get; set; }             // es LunaOptimizer: no se toca

    internal bool EsCarpeta { get; set; }
    internal string RutaAprobado { get; set; } = "";
    internal string NombreClave { get; set; } = "";
    internal string RutaArchivo { get; set; } = "";

    public string Letra => Habilitado ? "\uE73E" : "";
    public string Detalle => string.IsNullOrEmpty(Exe) ? Origen : Origen + " · " + Exe;
}

public static class StartupService
{
    private const string RunUsuario = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunEquipo = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunEquipo32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string AprobadoUsuario = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AprobadoUsuario32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string AprobadoEquipo = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AprobadoCarpetaUsuario = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string ValorPropio = "LunaOptimizer";

    public static List<StartupItem> GetItems()
    {
        var items = new List<StartupItem>();
        LeerRun(Registry.CurrentUser, RunUsuario, AprobadoUsuario, "Usuario", items);
        LeerRun(Registry.LocalMachine, RunEquipo, AprobadoEquipo, "Equipo", items);
        LeerRun(Registry.LocalMachine, RunEquipo32, AprobadoEquipo, "Equipo (32 bits)", items);
        LeerCarpeta(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AprobadoCarpetaUsuario,
            "Carpeta Inicio", items);
        LeerCarpeta(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), AprobadoCarpetaUsuario,
            "Carpeta Inicio (equipo)", items);

        return items
            .GroupBy(i => i.Nombre, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(i => i.Habilitado)
            .ThenBy(i => i.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void LeerRun(RegistryKey raiz, string clave, string claveAprobado, string origen, List<StartupItem> items)
    {
        try
        {
            using var run = raiz.OpenSubKey(clave);
            if (run is null) return;
            using var aprobado = raiz.OpenSubKey(claveAprobado);

            foreach (var nombre in run.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(nombre)) continue;
                string comando;
                try { comando = run.GetValue(nombre)?.ToString() ?? ""; }
                catch { continue; }
                if (comando.Length == 0) continue;

                bool habilitado = EstaHabilitado(aprobado, nombre);
                string exe = ExtraerExe(comando);
                items.Add(new StartupItem
                {
                    Nombre = nombre,
                    Comando = comando,
                    Origen = origen,
                    Exe = exe,
                    Habilitado = habilitado,
                    Propio = nombre.Equals(ValorPropio, StringComparison.OrdinalIgnoreCase),
                    RutaAprobado = claveAprobado,
                    NombreClave = nombre,
                });
            }
        }
        catch (Exception ex) { AppLog.Error(ex, "leer arranque " + clave); }
    }

    private static void LeerCarpeta(string carpeta, string claveAprobado, string origen, List<StartupItem> items)
    {
        try
        {
            if (string.IsNullOrEmpty(carpeta) || !Directory.Exists(carpeta)) return;
            RegistryKey? aprobado = null;
            try { aprobado = Registry.CurrentUser.OpenSubKey(claveAprobado); } catch { }

            foreach (var archivo in Directory.EnumerateFiles(carpeta))
            {
                string nombre = Path.GetFileNameWithoutExtension(archivo);
                if (string.IsNullOrWhiteSpace(nombre)) continue;
                bool habilitado = EstaHabilitado(aprobado, Path.GetFileName(archivo));
                items.Add(new StartupItem
                {
                    Nombre = nombre,
                    Comando = archivo,
                    Origen = origen,
                    Exe = archivo,
                    Habilitado = habilitado,
                    RutaAprobado = claveAprobado,
                    NombreClave = Path.GetFileName(archivo),
                    EsCarpeta = true,
                    RutaArchivo = archivo,
                });
            }
            aprobado?.Dispose();
        }
        catch (Exception ex) { AppLog.Error(ex, "leer carpeta de inicio"); }
    }

    /// <summary>StartupApproved: byte 0 == 3 -> desactivado por el usuario.</summary>
    private static bool EstaHabilitado(RegistryKey? aprobado, string nombre)
    {
        try
        {
            if (aprobado?.GetValue(nombre) is byte[] b && b.Length > 0)
                return b[0] != 3 && b[0] != 7 && b[0] != 9;
        }
        catch { }
        return true;
    }

    public static bool SetEnabled(StartupItem item, bool habilitado)
    {
        try
        {
            var hklm = item.Origen.StartsWith("Equipo", StringComparison.OrdinalIgnoreCase);
            using var raiz = hklm ? Registry.LocalMachine : Registry.CurrentUser;
            using var clave = raiz.CreateSubKey(item.RutaAprobado, writable: true);
            if (clave is null) return false;

            var datos = new byte[12];
            datos[0] = habilitado ? (byte)2 : (byte)3;
            clave.SetValue(item.NombreClave, datos, RegistryValueKind.Binary);
            item.Habilitado = habilitado;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "cambiar arranque " + item.Nombre);
            return false;
        }
    }

    /// <summary>Tiempo de arranque real de Windows (evento 100 de Diagnostics-Performance).</summary>
    public static double? BootSegundos()
    {
        try
        {
            var psi = new ProcessStartInfo("wevtutil.exe",
                "qe \"Microsoft-Windows-Diagnostics-Performance/Operational\" " +
                "/q:\"*[System[(EventID=100)]]\" /c:1 /rd:true /f:xml")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string salida = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } return null; }

            // f:xml es independiente del idioma: <Data Name="BootTime">30420</Data>
            var m = Regex.Match(salida, @"Name=['""]BootTime['""]>\s*(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(salida, @"BootTime>\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success && long.TryParse(m.Groups[1].Value, out long ms) && ms > 0)
                return ms / 1000.0;
        }
        catch (Exception ex) { AppLog.Error(ex, "tiempo de arranque"); }
        return null;
    }

    public static string TiempoArranqueTexto()
    {
        double? s = BootSegundos();
        if (s is null) return "—";
        return s < 60 ? $"{s:F0} s" : $"{s / 60:F1} min";
    }

    /// <summary>Alta/baja de LunaOptimizer en el arranque (Ajustes).</summary>
    public static bool AutoStartPropio
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunUsuario);
                return k?.GetValue(ValorPropio) is string s && s.Length > 0;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunUsuario, writable: true);
                if (k is null) return;
                if (value)
                {
                    string exe = Environment.ProcessPath ?? "";
                    if (exe.Length == 0) return;
                    k.SetValue(ValorPropio, "\"" + exe + "\" --inicio", RegistryValueKind.String);
                }
                else
                {
                    k.DeleteValue(ValorPropio, throwOnMissingValue: false);
                }
                SettingsStore.Current.IniciarConWindows = value;
                SettingsStore.Save();
            }
            catch (Exception ex) { AppLog.Error(ex, "autoinicio"); }
        }
    }

    private static string ExtraerExe(string comando)
    {
        try
        {
            string c = comando.Trim();
            if (c.StartsWith('"'))
            {
                int fin = c.IndexOf('"', 1);
                if (fin > 1) return c.Substring(1, fin - 1);
            }
            int idx = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return c.Substring(0, idx + 4).Trim();
            var partes = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return partes.Length > 0 ? partes[0] : c;
        }
        catch { return comando; }
    }
}
