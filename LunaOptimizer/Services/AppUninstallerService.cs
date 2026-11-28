using System.IO;
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
    /// Editor · version · tamaño, salteando los que esten vacios (las apps de la
    /// Store suelen no tener editor o tamaño al principio).
    public string Subtitle => string.Join(" · ", new[] { Publisher, Version, SizeText }
        .Where(t => !string.IsNullOrWhiteSpace(t)));

    public string SizeText => EstimatedSizeKB <= 0 ? "" : EstimatedSizeKB >= 1024 * 1024
        ? $"{EstimatedSizeKB / 1024.0 / 1024:F2} GB"
        : EstimatedSizeKB >= 1024 ? $"{EstimatedSizeKB / 1024.0:F1} MB" : $"{EstimatedSizeKB} KB";

    // Para apps Store el nombre suele venir con el PackageFamilyName
    public bool IsStore => KeyPath.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0;

    /// Paquete MSIX/Store (se desinstala con Remove-AppxPackage, no con el registro).
    public string? PackageFullName { get; set; }
    public bool EsAppx => !string.IsNullOrEmpty(PackageFullName);
}

/// Un resto que dejo un desinstalador: carpeta o archivo que quedo huerfano.
public sealed class Resto
{
    public string Ruta { get; init; } = "";
    public long Bytes { get; set; }
    public bool Seleccionado { get; set; } = true;
    public string Nombre => System.IO.Path.GetFileName(Ruta.TrimEnd('\\', '/'));
    public string Tamano => TempCleaner.FormatBytes(Bytes);
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

    // ===================== Microsoft Store / MSIX =====================
    // PC Manager lista y desinstala tambien las apps de la Store (paquetes MSIX):
    // se enumeran con Get-AppxPackage y se quitan con Remove-AppxPackage, que es
    // lo unico que funciona; el registro no tiene UninstallString para ellas.

    private static readonly string[] PaquetesInfraestructura =
    {
        "MicrosoftWindows.", "Microsoft.Windows.", "Microsoft.NET.", "Microsoft.VCLibs", "Microsoft.UI.Xaml",
        "Microsoft.Services.Store", "Microsoft.WinJS", "Microsoft.AAD.", "Microsoft.DesktopAppInstaller",
        "Microsoft.HEVCVideoExtension", "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions",
        "Microsoft.StorePurchaseApp", "Microsoft.WindowsStore", "Microsoft.StoreEdgeFD",
        "Microsoft.UI.", "Microsoft.MicrosoftEdge", "MicrosoftWindows.Client", "Microsoft.SecHealthUI",
        "Microsoft.LanguageExperiencePack", "MicrosoftWindows.Speech", "Microsoft.WindowsAppRuntime",
        "Microsoft.Winget", "Microsoft.MSIX", "MicrosoftWindows.UndockedDevKit", "Microsoft.OneDriveSync",
    };

    public static async Task<List<InstalledApp>> GetStoreAppsAsync(CancellationToken token = default)
    {
        var apps = new List<InstalledApp>();
        try
        {
            string ps =
                "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.NonRemovable } | " +
                "Select-Object Name,Publisher,Version,PackageFullName,InstallLocation | ConvertTo-Json -Compress";
            string json = await DefenderService.RunPowerShellAsync(ps, token, 45000).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return apps;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var elementos = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<System.Text.Json.JsonElement> { doc.RootElement };

            foreach (var e in elementos)
            {
                string nombre = e.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                if (nombre.Length == 0) continue;
                if (PaquetesInfraestructura.Any(p => nombre.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                string full = e.TryGetProperty("PackageFullName", out var f) ? f.GetString() ?? "" : "";
                string ubicacion = e.TryGetProperty("InstallLocation", out var i) ? i.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(ubicacion)) continue;   // paquete sin carpeta propia
                string version = e.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
                string editor = e.TryGetProperty("Publisher", out var pb) ? pb.GetString() ?? "" : "";
                if (NombreEditor(editor).Equals("Microsoft Windows", StringComparison.OrdinalIgnoreCase))
                    continue;   // componentes del sistema (CrossDevice, Speech...), no apps

                apps.Add(new InstalledApp
                {
                    Name = nombre,
                    Publisher = NombreEditor(editor),
                    Version = version,
                    InstallLocation = ubicacion,
                    PackageFullName = full,
                    KeyPath = "WindowsApps",
                });
            }
        }
        catch (Exception ex) { AppLog.Error(ex, "listando apps de la Store"); }
        return apps;
    }

    /// CN=Microsoft Corporation, O=... -> "Microsoft Corporation"
    private static string NombreEditor(string publisher)
    {
        try
        {
            foreach (var parte in publisher.Split(','))
            {
                var t = parte.Trim();
                if (!t.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) continue;
                t = t.Substring(3).Trim();
                // Muchas apps de la Store usan un GUID como editor: no se muestra.
                if (System.Text.RegularExpressions.Regex.IsMatch(t,
                        @"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$"))
                    return "";
                return t;
            }
        }
        catch { }
        return "";
    }

    /// Tamano de las apps MSIX en paralelo y con tope de tiempo (no bloquea la UI).
    public static void MedirTamanoApps(IEnumerable<InstalledApp> apps, int presupuestoMs = 6000)
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Parallel.ForEach(apps.Where(a => !string.IsNullOrEmpty(a.InstallLocation) && a.EstimatedSizeKB <= 0),
                new ParallelOptions { MaxDegreeOfParallelism = 4 }, app =>
                {
                    if (reloj.ElapsedMilliseconds > presupuestoMs) return;
                    try
                    {
                        if (!Directory.Exists(app.InstallLocation)) return;
                        long bytes = TempCleaner.MeasureDir(app.InstallLocation).bytes;
                        app.EstimatedSizeKB = bytes / 1024;
                    }
                    catch { }
                });
        }
        catch { }
    }

    /// Desinstala un paquete MSIX del usuario actual.
    public static async Task<(int exitCode, string error)> UninstallStoreAsync(InstalledApp app, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(app.PackageFullName)) return (-1, "paquete desconocido");
        try
        {
            string ps = $"try {{ Remove-AppxPackage -Package '{app.PackageFullName}' -ErrorAction Stop; 'OK' }} catch {{ $_.Exception.Message }}";
            string salida = await DefenderService.RunPowerShellAsync(ps, token, 300000).ConfigureAwait(false);
            if (salida.Trim().EndsWith("OK", StringComparison.Ordinal)) return (0, "");
            return (-1, salida.Trim());
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "desinstalando app Store " + app.Name);
            return (-1, ex.Message);
        }
    }

    // ===================== Restos de una desinstalacion =====================

    /// Busca carpetas que el desinstalador dejo atras: la carpeta de instalacion si
    /// siguio existiendo, y carpetas con el nombre del programa o del editor en
    /// LocalAppData / Roaming / ProgramData / Program Files. Solo devuelve rutas
    /// que la barrera de seguridad permite borrar.
    public static List<Resto> BuscarRestos(InstalledApp app, CancellationToken token = default)
    {
        var restos = new List<Resto>();
        var vistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Agregar(string ruta)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ruta)) return;
                ruta = Path.GetFullPath(ruta).TrimEnd('\\');
                if (vistas.Contains(ruta) || !Directory.Exists(ruta)) return;
                if (!SafetyGuard.CanDeleteEmptyDir(ruta)) return;
                vistas.Add(ruta);
            }
            catch { }
        }

        // 1) la carpeta de instalacion declarada, si el desinstalador la dejo
        if (!string.IsNullOrWhiteSpace(app.InstallLocation) && !EsCarpetaDeSistema(app.InstallLocation))
            Agregar(app.InstallLocation);

        // 2) carpetas con el nombre del programa / editor
        var tokens = Tokens(app);
        if (tokens.Count > 0)
        {
            var raices = new List<string>();
            void Raiz(Environment.SpecialFolder carpeta)
            {
                try
                {
                    var r = Environment.GetFolderPath(carpeta);
                    if (!string.IsNullOrEmpty(r) && Directory.Exists(r)) raices.Add(r);
                }
                catch { }
            }
            Raiz(Environment.SpecialFolder.LocalApplicationData);
            Raiz(Environment.SpecialFolder.ApplicationData);
            Raiz(Environment.SpecialFolder.CommonApplicationData);
            Raiz(Environment.SpecialFolder.ProgramFiles);
            Raiz(Environment.SpecialFolder.ProgramFilesX86);
            try
            {
                var prog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
                if (Directory.Exists(prog)) raices.Add(prog);
                var paquetes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                if (Directory.Exists(paquetes)) raices.Add(paquetes);
            }
            catch { }

            foreach (var raiz in raices)
            {
                if (token.IsCancellationRequested) break;
                IEnumerable<string> hijos;
                try { hijos = Directory.EnumerateDirectories(raiz); }
                catch { continue; }

                foreach (var hijo in hijos)
                {
                    if (token.IsCancellationRequested) break;
                    string nombreCarpeta = Path.GetFileName(hijo);
                    if (nombreCarpeta.Equals("LunaOptimizer", StringComparison.OrdinalIgnoreCase)) continue;
                    string normal = Normalizar(nombreCarpeta);
                    // Coincidencia por PREFIJO, no por "contiene": asi "Discord" no se
                    // lleva "MyDiscordBackup" ni carpetas ajenas que mencionan la palabra.
                    if (tokens.Any(t => normal.Equals(t, StringComparison.Ordinal) ||
                                         normal.StartsWith(t, StringComparison.Ordinal))) Agregar(hijo);
                }
            }
        }

        foreach (var ruta in vistas)
        {
            long bytes = 0;
            try { bytes = TempCleaner.MeasureDir(ruta).bytes; } catch { }
            restos.Add(new Resto { Ruta = ruta, Bytes = bytes });
        }
        return restos.OrderByDescending(r => r.Bytes).ToList();
    }

    /// C:\Windows, C:\Program Files, etc. no son "restos" de nadie.
    private static bool EsCarpetaDeSistema(string ruta)
    {
        try
        {
            string r = Path.GetFullPath(ruta).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var sistema = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach (var s in sistema)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (r.Equals(Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { return true; }
        return false;
    }

    private static List<string> Tokens(InstalledApp app)
    {
        var lista = new List<string>();
        void Partir(string texto)
        {
            foreach (var parte in texto.Split(new[] { ' ', '-', '_', '.', ',', '(', ')', '[', ']' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string t = Normalizar(parte);
                // Palabras genericas: si no se filtran, buscar restos de "Windows"
                // devolveria carpetas de OTROS programas que solo contienen esa palabra.
                bool generica = t is "windows" or "microsoft" or "program" or "programs" or "software"
                    or "app" or "apps" or "store" or "client" or "service" or "desktop" or "suite"
                    or "launcher" or "manager" or "player" or "edition" or "version" or "setup"
                    or "update" or "install" or "corp" or "corporation" or "team" or "studio";
                if (t.Length >= 4 && !generica && !lista.Contains(t)) lista.Add(t);
            }
        }
        Partir(app.Name);
        if (app.Publisher.Length > 0) Partir(app.Publisher);
        return lista.Take(6).ToList();
    }

    private static string Normalizar(string texto)
    {
        var sb = new System.Text.StringBuilder(texto.Length);
        foreach (char c in texto.ToLowerInvariant())
        {
            char d = c;
            if (d is 'á' or 'à' or 'ä' or 'â') d = 'a';
            else if (d is 'é' or 'è' or 'ë' or 'ê') d = 'e';
            else if (d is 'í' or 'ì' or 'ï' or 'î') d = 'i';
            else if (d is 'ó' or 'ò' or 'ö' or 'ô') d = 'o';
            else if (d is 'ú' or 'ù' or 'ü' or 'û') d = 'u';
            else if (d == 'ñ') d = 'n';
            if (char.IsLetterOrDigit(d)) sb.Append(d);
        }
        return sb.ToString();
    }

    /// Envia los restos marcados a la papelera (recuperables).
    public static (int borrados, long bytes) EliminarRestos(IEnumerable<Resto> restos)
    {
        int n = 0; long bytes = 0;
        foreach (var r in restos.Where(r => r.Seleccionado))
        {
            try
            {
                if (!SafetyGuard.CanDeleteEmptyDir(r.Ruta)) continue;
                if (!StorageService.EnviarAPapelera(r.Ruta)) continue;
                n++; bytes += r.Bytes;
            }
            catch (Exception ex) { AppLog.Error(ex, "borrando restos de " + r.Ruta); }
        }
        return (n, bytes);
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
