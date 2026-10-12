using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;

namespace LunaOptimizer.Services;

/// <summary>
/// Salud del sistema (estilo PC Manager): estado de servicios y ajustes que
/// afectan al rendimiento en juegos. Cada item es "leer estado -> aplicar/quitar
/// -> guardar log". Optimizar y Restaurar son operaciones DISTINTAS: al aplicar
/// se guarda el estado original para poder devolverlo exactamente despues.
/// </summary>
public class HealthItem
{
    public string Nombre { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public string Estado { get; set; } = "";        // texto visible
    public bool Ok { get; set; }                     // true = ya optimizado para gaming
    public string Accion { get; set; } = "";         // "Optimizar" / "Restaurar"
    public Func<bool>? Aplicar { get; set; }         // toggle: devuelve el nuevo estado Ok
}

public static class HealthService
{
    // Estado original (arranque de servicios y valores de registro) guardado al
    // optimizar, para que "Restaurar" devuelva el equipo a como estaba.
    private static readonly string StoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaOptimizer");
    private static readonly string StoreFile = Path.Combine(StoreDir, "salud.estado");
    private static readonly object StoreLock = new();
    private static Dictionary<string, string>? _store;

    // Servicios que estorban con SSD NVMe / en juegos. Default = tipo de arranque
    // original por defecto de Windows (2=Automatico, 3=Manual) si nunca lo guardamos.
    private static readonly (string Service, string Desc, int Default)[] Servicios =
    {
        ("SysMain",    "Superfetch: con SSD NVMe no aporta y consume I/O", 2),
        ("DiagTrack",  "Telemetria: envio de datos de uso a Microsoft", 2),
        ("XblAuthManager", "Xbox Live Auth: solo para juegos con cuenta Xbox", 3),
        ("XblGameSave",    "Xbox Game Save: guardado en la nube de Xbox", 3),
        ("XboxGipSvc",     "Xbox Accessory: mandos/auriculares Xbox", 3),
        ("XboxNetApiSvc",  "Xbox Networking: chat/party de Xbox", 3),
        ("MapsBroker",     "Mapas descargados: sincroniza mapas offline", 3),
        ("RetailDemo",     "Modo demostracion de tienda", 3),
    };

    // Ajustes de registro para gaming. Bueno = valor optimizado, Defecto = valor
    // clasico de Windows al que se vuelve con "Restaurar" si no hay estado guardado.
    private static readonly (string Path, string Value, int Bueno, int Defecto, string Nombre, string Desc)[] Registros =
    {
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1,
            "Game DVR (grabacion)", "Desactivado evita sobrecosta al grabar en segundo plano"),
        (@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0, 1,
            "Game DVR del sistema", "Desactivado evita sobrecosta de grabacion continua"),
        (@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0,
            "Modo juego", "Activado prioriza el juego sobre el fondo"),
        (@"HKCU\System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, 0,
            "Optimizaciones pantalla completa", "Valor 2 = comportamiento clasico sin sobrecosta"),
    };

    public static List<HealthItem> Scan()
    {
        var items = new List<HealthItem>();

        // --- Servicios ---
        foreach (var (svc, desc, defecto) in Servicios)
        {
            bool optimizado = EsServicioOptimizado(svc);
            items.Add(new HealthItem
            {
                Nombre = svc,
                Descripcion = desc,
                Ok = optimizado,
                Estado = optimizado ? "Deshabilitado" : (IsServiceRunning(svc) ? "Activo" : "Inactivo"),
                Accion = optimizado ? "Restaurar" : "Optimizar",
                Aplicar = optimizado
                    ? () => RestaurarServicio(svc, defecto)
                    : () => OptimizarServicio(svc),
            });
        }

        // --- Registro ---
        foreach (var (path, value, bueno, defecto, nombre, desc) in Registros)
        {
            int actual = ReadReg(path, value);
            bool ok = actual == bueno;
            string clave = path + "|" + value;
            items.Add(new HealthItem
            {
                Nombre = nombre,
                Descripcion = desc,
                Ok = ok,
                Estado = ok ? "Optimizado" : "Sin optimizar",
                Accion = ok ? "Restaurar" : "Optimizar",
                Aplicar = ok
                    ? () => RestaurarReg(clave, path, value, bueno, defecto)
                    : () => OptimizarReg(clave, path, value, bueno),
            });
        }

        return items;
    }

    // ---- Servicios ----

    /// Para gaming "optimizado" = arranque Deshabilitado (4). Es estable: no
    /// depende de si el servicio ya se detuvo o no (algunos solo paran al reiniciar).
    private static bool EsServicioOptimizado(string name) => ReadServiceStart(name) == 4;

    private static int? ReadServiceStart(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key?.GetValue("Start") is int s ? s : null;
        }
        catch { return null; }
    }

    private static bool SetServiceStart(string name, int start)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{name}", writable: true);
            if (key is null) return false;
            key.SetValue("Start", start, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    private static bool OptimizarServicio(string name)
    {
        // Guardar el arranque original (si no estaba ya deshabilitado) para restaurar.
        int? original = ReadServiceStart(name);
        if (original is int o && o != 4) StoreSet("svc:" + name, o.ToString());

        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                try
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                }
                catch { /* algun servicio no deja pararse; Start=4 bastara al reiniciar */ }
            }
        }
        catch { }

        SetServiceStart(name, 4);
        return EsServicioOptimizado(name);
    }

    private static bool RestaurarServicio(string name, int defecto)
    {
        int start = StoreGetInt("svc:" + name) ?? defecto;
        if (!SetServiceStart(name, start)) return false;

        // Volver a arrancarlo si el arranque no es deshabilitado y esta parado.
        try
        {
            using var sc = new ServiceController(name);
            if (start != 4 && sc.Status == ServiceControllerStatus.Stopped) sc.Start();
        }
        catch { }

        StoreRemove("svc:" + name);
        return EsServicioOptimizado(name);
    }

    private static bool IsServiceRunning(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }   // no existe -> lo tratamos como inactivo
    }

    // ---- Registro ----

    private static bool OptimizarReg(string clave, string path, string value, int bueno)
    {
        StoreSet("reg:" + clave, ReadReg(path, value).ToString());
        WriteReg(path, value, bueno);
        return ReadReg(path, value) == bueno;
    }

    private static bool RestaurarReg(string clave, string path, string value, int bueno, int defecto)
    {
        int destino = StoreGetInt("reg:" + clave) ?? defecto;
        WriteReg(path, value, destino);
        StoreRemove("reg:" + clave);
        return ReadReg(path, value) == bueno;
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

    // ---- Almacen del estado original ----

    private static Dictionary<string, string> Store()
    {
        lock (StoreLock)
        {
            if (_store is not null) return _store;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(StoreFile))
                    foreach (var line in File.ReadAllLines(StoreFile))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line[..i]] = line[(i + 1)..];
                    }
            }
            catch { }
            return _store = d;
        }
    }

    private static void StoreSet(string k, string v)
    {
        lock (StoreLock) { Store()[k] = v; }
        SaveStore();
    }

    private static string? StoreGet(string k)
    {
        lock (StoreLock) return Store().TryGetValue(k, out var v) ? v : null;
    }

    private static int? StoreGetInt(string k) => int.TryParse(StoreGet(k), out var v) ? v : null;

    private static void StoreRemove(string k)
    {
        bool changed;
        lock (StoreLock) changed = Store().Remove(k);
        if (changed) SaveStore();
    }

    private static void SaveStore()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            string texto;
            lock (StoreLock) texto = string.Join(Environment.NewLine, Store().Select(kv => kv.Key + "=" + kv.Value));
            File.WriteAllText(StoreFile, texto);
        }
        catch { }
    }
}
