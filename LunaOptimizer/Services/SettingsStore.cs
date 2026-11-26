using System.IO;
using System.Text.Json;

namespace LunaOptimizer.Services;

/// <summary>
/// Preferencias de la app. Se guardan en %LOCALAPPDATA%\LunaOptimizer\ajustes.json
/// (sin telemetria, sin nube: un archivo local que el usuario puede borrar).
/// </summary>
public class AppSettings
{
    public bool TemaOscuro { get; set; }
    /// <summary>PC Manager: "Smart Boost" — optimiza solo cuando el PC se pone lento.</summary>
    public bool SmartBoost { get; set; }
    public int SmartBoostRamPct { get; set; } = 90;
    public int SmartBoostTempMB { get; set; } = 1024;
    public bool IniciarConWindows { get; set; }
    /// <summary>Ultima revisión de salud (texto ISO 8601) para la tarjeta del Inicio.</summary>
    public string? UltimaRevision { get; set; }
    public int UltimaPuntuacion { get; set; } = -1;
}

public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaOptimizer");

    private static readonly string Archivo = Path.Combine(Dir, "ajustes.json");
    private static readonly object Candado = new();
    private static AppSettings? _actual;

    public static string CarpetaDatos => Dir;

    public static AppSettings Current
    {
        get
        {
            if (_actual is not null) return _actual;
            lock (Candado)
            {
                _actual ??= Cargar();
                return _actual;
            }
        }
    }

    private static AppSettings Cargar()
    {
        try
        {
            if (File.Exists(Archivo))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Archivo));
                if (s is not null)
                {
                    s.SmartBoostRamPct = Math.Clamp(s.SmartBoostRamPct, 60, 99);
                    s.SmartBoostTempMB = Math.Clamp(s.SmartBoostTempMB, 256, 10240);
                    return s;
                }
            }
        }
        catch (Exception ex) { AppLog.Error(ex, "leer ajustes"); }
        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            lock (Candado)
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Archivo, JsonSerializer.Serialize(Current,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex) { AppLog.Error(ex, "guardar ajustes"); }
    }
}
