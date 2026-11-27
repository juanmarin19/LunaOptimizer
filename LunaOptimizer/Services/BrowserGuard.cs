using System.IO;

namespace LunaOptimizer.Services;

/// <summary>
/// Proteccion de datos de navegadores.
///
/// Las reglas de Winapp2 incluyen historial, cookies, sesiones, "Web Data",
/// "Login Data", marcadores y almacenamiento de sitios de Chrome, Edge, Firefox,
/// Brave, Opera, Vivaldi... Borrar eso no es "limpiar cache": es perder sesiones,
/// contraseñas guardadas e historial. Aqui se decide, por ruta, que es dato del
/// usuario (intocable) y que es solo cache (se puede limpiar).
///
/// Regla: dentro de un perfil de navegador SOLO se permite lo que este en una
/// carpeta de cache conocida. Todo lo demas se rechaza.
/// </summary>
public static class BrowserGuard
{
    private static readonly object _candado = new();
    private static string[]? _raices;

    /// Raices de perfil, relativas a cada perfil de usuario.
    private static readonly string[] Relativas =
    {
        @"AppData\Local\Google\Chrome\User Data",
        @"AppData\Local\Google\Chrome Beta\User Data",
        @"AppData\Local\Google\Chrome SxS\User Data",
        @"AppData\Local\Microsoft\Edge\User Data",
        @"AppData\Local\BraveSoftware\Brave-Browser\User Data",
        @"AppData\Local\Chromium\User Data",
        @"AppData\Local\Vivaldi\User Data",
        @"AppData\Local\Yandex\YandexBrowser\User Data",
        @"AppData\Local\Opera Software",
        @"AppData\Roaming\Opera Software",
        @"AppData\Roaming\Mozilla\Firefox\Profiles",
        @"AppData\Local\Mozilla\Firefox\Profiles",
        @"AppData\Roaming\Thunderbird\Profiles",
    };

    /// Nombres de carpeta que son cache y se pueden vaciar sin perder datos.
    private static readonly HashSet<string> SoloCache = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Cache_Data", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache",
        "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "Media Cache", "Application Cache",
        "CacheStorage", "ScriptCache", "cache2", "startupCache", "shader-cache",
        "thumbnails", "OfflineCache", "jumpListCache", "CachedData", "cache",
    };

    /// Claves de registro de navegadores que tampoco se tocan (preferencias, perfiles,
    /// politicas): borrarlas resetea el navegador aunque no sean "datos" visibles.
    private static readonly string[] ClavesProtegidas =
    {
        @"HKCU\Software\Google",
        @"HKCU\Software\Microsoft\Edge",
        @"HKCU\Software\Mozilla",
        @"HKCU\Software\Opera Software",
        @"HKCU\Software\BraveSoftware",
        @"HKCU\Software\Vivaldi",
        @"HKLM\SOFTWARE\Google",
        @"HKLM\SOFTWARE\Microsoft\Edge",
        @"HKLM\SOFTWARE\Mozilla",
        @"HKLM\SOFTWARE\WOW6432Node\Google",
        @"HKLM\SOFTWARE\WOW6432Node\Mozilla",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Edge",
    };

    /// Raices de perfil absolutas de TODOS los usuarios, con separador final.
    private static string[] Raices()
    {
        var cache = _raices;
        if (cache is not null) return cache;

        lock (_candado)
        {
            if (_raices is not null) return _raices;
            var lista = new List<string>();

            void AgregarRaiz(string perfil, string relativa)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(perfil)) return;
                    lista.Add(Path.Combine(perfil, relativa).TrimEnd('\\', '/') + "\\");
                }
                catch { }
            }

            var perfiles = new List<string>();
            try { perfiles.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); } catch { }
            try
            {
                foreach (var u in TempCleaner.UserDirs())
                    if (!perfiles.Contains(u, StringComparer.OrdinalIgnoreCase)) perfiles.Add(u);
            }
            catch { }

            foreach (var perfil in perfiles)
                foreach (var rel in Relativas)
                    AgregarRaiz(perfil, rel);

            _raices = lista.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return _raices;
        }
    }

    /// true si la ruta es un dato del navegador que NO se debe borrar.
    public static bool EsDatoProtegido(string ruta, out string motivo)
    {
        motivo = "";
        try
        {
            if (string.IsNullOrWhiteSpace(ruta)) return false;

            foreach (var raiz in Raices())
            {
                if (!ruta.StartsWith(raiz, StringComparison.OrdinalIgnoreCase)) continue;

                string resto = ruta.Substring(raiz.Length);
                if (EsSoloCache(resto)) return false;

                motivo = "datos del navegador (solo se limpia la cache)";
                return true;
            }
        }
        catch { }
        return false;
    }

    public static bool EsDatoProtegido(string ruta) => EsDatoProtegido(ruta, out _);

    /// true si la clave de registro pertenece a un navegador.
    public static bool EsClaveProtegida(string clave)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(clave)) return false;
            foreach (var pref in ClavesProtegidas)
                if (clave.Equals(pref, StringComparison.OrdinalIgnoreCase) ||
                    clave.StartsWith(pref + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { }
        return false;
    }

    /// Cualquier segmento de la ruta relativa que sea una carpeta de cache basta:
    /// User Data\Default\Cache\Cache_Data\f_0001 -> permitido;
    /// User Data\Default\History -> rechazado.
    private static bool EsSoloCache(string resto)
    {
        var partes = resto.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var parte in partes)
            if (SoloCache.Contains(parte)) return true;
        return false;
    }
}
