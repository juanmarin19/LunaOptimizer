using System.ServiceProcess;
using Microsoft.Win32;

namespace LunaOptimizer.Services;

/// <summary>
/// Salud del sistema (estilo PC Manager): estado de servicios y ajustes que
/// afectan al rendimiento en juegos. Cada item es "leer estado -> aplicar/quitar
/// -> guardar log", el mismo patron que ThemeManager. No toca nada por su
/// cuenta: solo aplica cuando el usuario pulsa el toggle.
/// </summary>
public class HealthItem
{
    public string Nombre { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public string Estado { get; set; } = "";        // texto visible
    public bool Ok { get; set; }                     // true = bien para gaming
    public string Accion { get; set; } = "";         // "Optimizar" / "Restaurar"
    public Func<bool>? Aplicar { get; set; }         // devuelve el nuevo estado ok
    public string Tipo { get; set; } = "";           // "Servicio" / "Registro"
}

public static class HealthService
{
    // Servicios que estorban con SSD NVMe / en juegos. Deshabilitar = startup
    // Disabled; el servicio se detiene al reiniciar (no forzamos stop aqui).
    private static readonly (string Service, string Desc)[] Servicios =
    {
        ("SysMain",    "Superfetch: con SSD NVMe no aporta y consume I/O"),
        ("DiagTrack",  "Telemetria: envio de datos de uso a Microsoft"),
        ("XblAuthManager", "Xbox Live Auth: solo para juegos con cuenta Xbox"),
        ("XblGameSave",    "Xbox Game Save: guardado en la nube de Xbox"),
        ("XboxGipSvc",     "Xbox Accessory: mandos/auriculares Xbox"),
        ("XboxNetApiSvc",  "Xbox Networking: chat/party de Xbox"),
        ("MapsBroker",     "Mapas descargados: sincroniza mapas offline"),
        ("RetailDemo",     "Modo demostracion de tienda"),
    };

    // Ajustes de registro para gaming (ya medidos en el PC de referencia).
    private static readonly (string Path, string Value, int Bueno, string Nombre, string Desc)[] Registros =
    {
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0,
            "Game DVR (grabacion)", "Desactivado evita sobrecosta al grabar en segundo plano"),
        (@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0,
            "Game DVR del sistema", "Desactivado evita sobrecosta de grabacion continua"),
        (@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1,
            "Modo juego", "Activado prioriza el juego sobre el fondo"),
        (@"HKCU\System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2,
            "Optimizaciones pantalla completa", "Valor 2 = comportamiento clasico sin sobrecosta"),
    };

    public static List<HealthItem> Scan()
    {
        var items = new List<HealthItem>();

        // --- Servicios ---
        foreach (var (svc, desc) in Servicios)
        {
            bool corriendo = IsServiceRunning(svc);
            items.Add(new HealthItem
            {
                Nombre = svc,
                Descripcion = desc,
                Tipo = "Servicio",
                Ok = !corriendo,   // para gaming: que NO corran
                Estado = corriendo ? "Activo" : "Inactivo",
                Accion = corriendo ? "Optimizar" : "Restaurar",
                Aplicar = () => SetServiceDisabled(svc),
            });
        }

        // --- Registro ---
        foreach (var (path, value, bueno, nombre, desc) in Registros)
        {
            int actual = ReadReg(path, value);
            bool ok = actual == bueno;
            items.Add(new HealthItem
            {
                Nombre = nombre,
                Descripcion = desc,
                Tipo = "Registro",
                Ok = ok,
                Estado = ok ? "Optimizado" : "Sin optimizar",
                Accion = ok ? "Restaurar" : "Optimizar",
                Aplicar = () => WriteReg(path, value, bueno),
            });
        }

        return items;
    }

    // ---- helpers ----

    private static bool IsServiceRunning(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }   // no existe -> lo tratamos como inactivo
    }

    private static bool SetServiceDisabled(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            // ServiceStartMode requiere System.ServiceProcess.ServiceController
            // y permiso; si falla, al menos lo detenemos.
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
            // cambiar arranque a disabled via registro
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{name}", writable: true);
            key?.SetValue("Start", 4, RegistryValueKind.DWord);   // 4 = disabled
            return true;
        }
        catch { return false; }
    }

    private static int ReadReg(string path, string value)
    {
        try
        {
            var parts = path.Split('\\', 2);
            var hive = parts[0] == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            using var key = hive.OpenSubKey(parts[1]);
            var v = key?.GetValue(value);
            return v is int i ? i : -1;
        }
        catch { return -1; }
    }

    private static bool WriteReg(string path, string value, int nuevo)
    {
        try
        {
            var parts = path.Split('\\', 2);
            var hive = parts[0] == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            using var key = hive.CreateSubKey(parts[1], writable: true);
            key?.SetValue(value, nuevo, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }
}
