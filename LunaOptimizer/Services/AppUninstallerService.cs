using Microsoft.Win32;

namespace LunaOptimizer.Services;

/// <summary>
/// Desinstalador de apps (estilo PC Manager / Geek / BCUninstaller), adaptado a
/// esta app. Lee las 4 fuentes de registro donde Windows guarda lo instalado,
/// distingue MSI de EXE y corre el desinstalador registrado. NO reimplementa
/// toda la suite de BCU (Apache-2.0): solo listar + desinstalar + borrar la
/// entrada del registro, que es lo que necesita la pestana.
/// </summary>
public class InstalledApp
{
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string DisplayIcon { get; set; } = "";
    public string InstallLocation { get; set; } = "";

    // donde vive la entrada, para poder borrarla tras desinstalar
    public RegistryHive? Hive { get; set; }
    public RegistryView View { get; set; } = RegistryView.Default; // vista con la que se leyo, para borrar en la misma
    public string KeyPath { get; set; } = "";      // sub-key dentro del hive
    public string KeyName { get; set; } = "";      // nombre de la sub-key

    public bool IsMsi { get; set; }
    public string UninstallString { get; set; } = "";
    public string QuietUninstallString { get; set; } = "";

    public long EstimatedSizeKB { get; set; }
    public string SizeText => EstimatedSizeKB <= 0 ? "" : EstimatedSizeKB >= 1024 * 1024
        ? $"{EstimatedSizeKB / 1024.0 / 1024:F2} GB"
        : EstimatedSizeKB >= 1024 ? $"{EstimatedSizeKB / 1024.0:F1} MB" : $"{EstimatedSizeKB} KB";

    // Para apps Store el nombre suele venir con el PackageFamilyName
    public bool IsStore => KeyPath.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0;
}

public static class AppUninstallerService
{
    // Las 4 fuentes (64 y 32 bits, maquina y usuario). BCU usa estas mismas.
    private static readonly (RegistryHive Hive, RegistryView View, string Sub, string Label)[] Sources =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "Equipo 64"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "Equipo 32"),
        (RegistryHive.CurrentUser,  RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "Usuario"),
        (RegistryHive.CurrentUser,  RegistryView.Registry32, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "Usuario 32"),
    };

    public static List<InstalledApp> GetInstalledApps()
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view, sub, _) in Sources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(sub);
                if (key is null) continue;

                foreach (var subName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var app = key.OpenSubKey(subName);
                        if (app is null) continue;

                        string name = app.GetValue("DisplayName")?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (!seen.Add(name)) continue;   // dedupe por nombre

                        // SystemComponent=1 y ParentKeyName -> componentes ocultos, no apps del usuario
                        if (app.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (!string.IsNullOrWhiteSpace(app.GetValue("ParentKeyName")?.ToString())) continue;

                        string uninst = app.GetValue("UninstallString")?.ToString() ?? "";
                        string quiet = app.GetValue("QuietUninstallString")?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(uninst) && string.IsNullOrWhiteSpace(quiet)) continue;

                        // ¿MSI? WindowsInstaller=1 o el UninstallString empieza con MsiExec
                        bool isMsi = (app.GetValue("WindowsInstaller") is int wi && wi == 1)
                                     || uninst.TrimStart('"').StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase);

                        long sizeKb = 0;
                        if (app.GetValue("EstimatedSize") is int es) sizeKb = es;

                        apps.Add(new InstalledApp
                        {
                            Name = name,
                            Publisher = app.GetValue("Publisher")?.ToString() ?? "",
                            Version = app.GetValue("DisplayVersion")?.ToString() ?? "",
                            DisplayIcon = app.GetValue("DisplayIcon")?.ToString() ?? "",
                            InstallLocation = app.GetValue("InstallLocation")?.ToString() ?? "",
                            Hive = hive,
                            View = view,
                            KeyPath = sub,
                            KeyName = subName,
                            IsMsi = isMsi,
                            UninstallString = uninst,
                            QuietUninstallString = quiet,
                            EstimatedSizeKB = sizeKb,
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// Desinstala: MSI usa msiexec /x GUID /qn /norestart (silencioso);
    /// el resto corre su UninstallString (o la Quiet si existe). Devuelve
    /// el codigo de salida; 0 = bien. No bloquea el hilo de UI: llamar con Task.Run.
    public static (int exitCode, string error) Uninstall(InstalledApp app)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi;

            if (app.IsMsi)
            {
                // el GUID es el nombre de la sub-key (o el que haya en el UninstallString)
                string guid = app.KeyName;
                if (!guid.StartsWith("{")) guid = "{" + guid + "}";
                psi = new System.Diagnostics.ProcessStartInfo("msiexec.exe", $"/x {guid} /qn /norestart")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            else
            {
                string cmd = !string.IsNullOrWhiteSpace(app.QuietUninstallString)
                    ? app.QuietUninstallString
                    : app.UninstallString;
                if (string.IsNullOrWhiteSpace(cmd)) return (-1, "sin comando de desinstalacion");

                // el UninstallString puede traer ruta + args; separar exe de argumentos
                var (exe, args) = SplitCommand(cmd);
                if (string.IsNullOrWhiteSpace(exe)) return (-1, "comando invalido");

                psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return (-1, "no se pudo iniciar");
            if (!p.WaitForExit(120000)) return (-1, "el desinstalador sigue en ejecucion (timeout de 2 min)");
            int code = p.ExitCode;
            // 0 = OK; 3010 = OK pero requiere reiniciar; 1641 = OK y ya reinicio.
            if (code is 0 or 3010 or 1641) return (code, "");
            return (code, $"codigo {code}");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Desinstalando " + app.Name);
            return (-1, ex.Message);
        }
    }

    /// Borra la entrada del registro (algunos desinstaladores la dejan huerfana).
    public static bool RemoveRegistryEntry(InstalledApp app)
    {
        try
        {
            if (app.Hive is null || string.IsNullOrWhiteSpace(app.KeyPath) || string.IsNullOrWhiteSpace(app.KeyName))
                return false;
            using var baseKey = RegistryKey.OpenBaseKey(app.Hive.Value, app.View);
            using var parent = baseKey.OpenSubKey(app.KeyPath, writable: true);
            parent?.DeleteSubKeyTree(app.KeyName, throwOnMissingSubKey: false);
            return true;
        }
        catch { return false; }
    }

    /// "C:\path\uninst.exe" /S  ->  ("C:\path\uninst.exe", "/S")
    private static (string exe, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith("\""))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0)
                return (cmd.Substring(1, end - 1), cmd.Substring(end + 1).Trim());
        }
        int sp = cmd.IndexOf(' ');
        return sp > 0 ? (cmd.Substring(0, sp), cmd.Substring(sp + 1).Trim()) : (cmd, "");
    }
}
