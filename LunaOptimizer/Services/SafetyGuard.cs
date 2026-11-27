using System.IO;

namespace LunaOptimizer.Services;

/// <summary>
/// Barrera unica de seguridad para TODO borrado. La app corre elevada (admin), asi
/// que una ruta mal resuelta (una regla de Winapp2, un %VAR% que quedo vacio, un
/// junction que apunta a otro lado) podria borrar algo critico. Antes de cada
/// File.Delete / MoveFileEx se pregunta aca: si dice "no", se omite el archivo.
///
/// Reglas (todas conservadoras: ante la duda, NO se borra):
///  1. Nunca una raiz de disco (C:\, D:\) ni una ruta vacia/invalida.
///  2. Nunca dentro de zonas criticas: System32, SysWOW64, WinSxS, drivers, el
///     ejecutable y la carpeta de datos del propio LunaOptimizer, ni las carpetas de
///     datos del usuario (Documentos, Escritorio, Imagenes, Videos, Musica).
///  3. Nunca se sigue un reparse point (symlink/junction): si el archivo lo es, se omite.
///  4. Nunca ejecutables ni binarios del sistema (.exe, .dll, .sys, .msi...): un
///     limpiador de caches no tiene por que tocarlos; si una regla los pide, se ignora.
///  5. No se borran archivos modificados hace menos de <see cref="MinutosRecientes"/>
///     minutos: casi siempre son temporales de algo que se esta instalando/usando AHORA.
///
/// Nota: no se aplica a la papelera de reciclaje (ahi el usuario ya decidio tirar
/// esas cosas) ni al cierre de procesos, que tienen su propia lista protegida.
/// </summary>
public static class SafetyGuard
{
    /// Ventana de proteccion para archivos recien modificados, en minutos.
    /// 0 desactiva esta regla (vuelve al comportamiento de borrar todo lo que no
    /// este en uso). Por defecto 5: evita comerse los temporales de una instalacion
    /// o actualizacion que esta corriendo en este momento.
    public static int MinutosRecientes { get; set; } = 5;

    // Extensiones que un limpiador nunca deberia borrar. Un .exe/.dll en una carpeta
    // de cache es mas probable que sea parte de la app que basura.
    private static readonly HashSet<string> ExtensionesProtegidas = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".ocx", ".drv", ".cpl", ".scr", ".msi", ".msp", ".msu",
        ".com", ".bat", ".cmd", ".ps1", ".vbs", ".psm1", ".reg",
    };

    /// Prefijos (normalizados y con separador final) que JAMAS se tocan.
    private static readonly string[] PrefijosProtegidos = ConstruirPrefijos();

    /// Ruta del ejecutable en ejecucion (para no auto-borrarse).
    private static readonly string ExePropio = ConstruirExePropio();

    private static string ConstruirExePropio()
    {
        try { return Path.GetFullPath(Environment.ProcessPath ?? "").TrimEnd('\\', '/'); }
        catch { return ""; }
    }

    private static string[] ConstruirPrefijos()
    {
        var list = new List<string>();

        void Add(string? camino)
        {
            if (string.IsNullOrWhiteSpace(camino)) return;
            try
            {
                var full = Path.GetFullPath(camino).TrimEnd('\\', '/');
                // Evita que "" o "C:\" entren como prefijo valido.
                if (full.Length <= 2) return;
                list.Add(full + "\\");
            }
            catch { }
        }

        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Columna vertebral de Windows.
        Add(Path.Combine(win, "System32"));
        Add(Path.Combine(win, "SysWOW64"));
        Add(Path.Combine(win, "WinSxS"));
        Add(Path.Combine(win, "Boot"));
        Add(Path.Combine(win, "servicing"));
        Add(Path.Combine(win, "assembly"));
        Add(Path.Combine(win, "Microsoft.NET"));
        Add(Path.Combine(win, "Fonts"));
        Add(Path.Combine(win, "INF"));
        Add(Path.Combine(win, "SystemApps"));
        Add(Path.Combine(win, "ImmersiveControlPanel"));
        Add(Path.Combine(win, "Provisioning"));
        Add(Path.Combine(win, "System32", "drivers"));
        Add(Path.Combine(win, "System32", "config"));

        // El propio LunaOptimizer: su carpeta de datos (logs, tema, estado) y su
        // ejecutable. No se borra a si mismo.
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaOptimizer"));

        // Datos del usuario: un limpiador de caches no toca trabajo/documentos.
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// Decide si un archivo se puede borrar. Devuelve false y el motivo cuando no.
    /// <paramref name="protegerEjecutables"/> en false permite borrar .exe/.dll: se
    /// usa solo en carpetas temporales conocidas por el propio programa (el payload
    /// de un instalador viejo es basura legitima). Para rutas de Winapp2, dejar true.
    public static bool CanDelete(FileInfo fi, out string motivo, bool protegerEjecutables = true)
    {
        motivo = "";
        if (fi is null) { motivo = "ruta nula"; return false; }

        string full;
        try { full = Path.GetFullPath(fi.FullName); }
        catch { motivo = "ruta invalida"; return false; }

        if (!RutaPermitida(full, out motivo)) return false;

        try { if (!fi.Exists) { motivo = "no existe"; return false; } }
        catch { motivo = "no accesible"; return false; }

        try
        {
            // No se siguen enlaces: borrar a traves de un symlink/junction es impredecible.
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) { motivo = "es un enlace (reparse point)"; return false; }

            if (protegerEjecutables && ExtensionesProtegidas.Contains(fi.Extension))
            { motivo = "extension protegida " + fi.Extension; return false; }

            if (MinutosRecientes > 0 &&
                (DateTime.Now - fi.LastWriteTime) < TimeSpan.FromMinutes(MinutosRecientes))
            { motivo = $"modificado hace menos de {MinutosRecientes} min"; return false; }
        }
        catch { motivo = "atributos no disponibles"; return false; }

        return true;
    }

    /// Igual que <see cref="CanDelete(FileInfo, out string, bool)"/> pero desde una ruta.
    public static bool CanDelete(string path, out string motivo, bool protegerEjecutables = true)
    {
        try { return CanDelete(new FileInfo(path), out motivo, protegerEjecutables); }
        catch { motivo = "ruta invalida"; return false; }
    }

    /// Igual que <see cref="CanDelete(FileInfo, out string, bool)"/> pero con los
    /// metadatos ya leidos (atributos y fecha UTC). La limpieza de temporales borra
    /// decenas de miles de archivos: repetir stat() por archivo solo para las reglas
    /// de seguridad era media limpieza.
    public static bool CanDeleteConMetadata(string path, FileAttributes atributos, DateTime escrituraUtc,
        bool protegerEjecutables, out string motivo)
    {
        motivo = "";
        if (string.IsNullOrWhiteSpace(path)) { motivo = "ruta vacia"; return false; }

        // Rutas absolutas y sin "..": aqui NO se normaliza (es la ruta caliente), asi
        // que una ruta con "..\" podria colarse en una zona protegida. Los recorridos
        // siempre dan rutas absolutas y limpias; cualquier otra cosa se rechaza.
        if (!Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal))
        { motivo = "ruta relativa o con puntos"; return false; }

        if (!RutaPermitida(path, out motivo)) return false;

        if ((atributos & FileAttributes.ReparsePoint) != 0) { motivo = "es un enlace (reparse point)"; return false; }

        if (protegerEjecutables && ExtensionesProtegidas.Contains(Path.GetExtension(path)))
        { motivo = "extension protegida " + Path.GetExtension(path); return false; }

        if (MinutosRecientes > 0 &&
            (DateTime.UtcNow - escrituraUtc) < TimeSpan.FromMinutes(MinutosRecientes))
        { motivo = $"modificado hace menos de {MinutosRecientes} min"; return false; }

        return true;
    }

    /// Permite borrar una CARPETA que esta vacia. No aplica controles de extension
    /// (es un directorio); si aplica zona protegida y reparse point.
    public static bool CanDeleteEmptyDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        string full;
        try { full = Path.GetFullPath(dir); }
        catch { return false; }

        if (!RutaPermitida(full, out _)) return false;

        try
        {
            var attr = File.GetAttributes(full);
            if ((attr & FileAttributes.ReparsePoint) != 0) return false;
            if ((attr & FileAttributes.Directory) == 0) return false;
        }
        catch { return false; }

        return true;
    }

    /// true si <paramref name="path"/> esta dentro de <paramref name="root"/> (o es root).
    public static bool IsInside(string path, string root)
    {
        try
        {
            string p = Path.GetFullPath(path).TrimEnd('\\', '/');
            string r = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
            return p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// Quita separadores finales sin asignar memoria (TrimEnd de spans solo acepta
    /// un conjunto de chars como ReadOnlySpan, no dos chars sueltos).
    private static ReadOnlySpan<char> RecortarSeparadores(ReadOnlySpan<char> s)
    {
        int fin = s.Length;
        while (fin > 0 && (s[fin - 1] == '\\' || s[fin - 1] == '/')) fin--;
        return s[..fin];
    }

    /// Reglas que dependen solo de la ruta (raiz de disco y zonas protegidas).
    /// Version string: se usa cuando la ruta y sus metadatos ya estan en mano.
    private static bool RutaPermitida(string full, out string motivo) => RutaPermitida(full.AsSpan(), out motivo);

    /// Igual que la anterior pero sin asignaciones: con 60.000 archivos la version
    /// que hacia TrimEnd + "\\" + Path.GetPathRoot dejaba megas de basura al GC.
    private static bool RutaPermitida(ReadOnlySpan<char> full, out string motivo)
    {
        motivo = "";
        if (full.IsEmpty) { motivo = "ruta vacia"; return false; }

        ReadOnlySpan<char> recortada = RecortarSeparadores(full);

        // Raiz de disco (C:\, D:\): jamas.
        try
        {
            ReadOnlySpan<char> raiz = Path.GetPathRoot(full);
            if (!raiz.IsEmpty && recortada.Equals(RecortarSeparadores(raiz), StringComparison.OrdinalIgnoreCase))
            { motivo = "raiz de disco"; return false; }
        }
        catch { motivo = "ruta invalida"; return false; }

        // El propio ejecutable de LunaOptimizer: jamas.
        if (ExePropio.Length > 0 &&
            recortada.Equals(ExePropio.AsSpan(), StringComparison.OrdinalIgnoreCase))
        { motivo = "es el ejecutable de LunaOptimizer"; return false; }

        // Los prefijos se guardan con separador final, asi que cualquier ruta DENTRO
        // de la zona protegida empieza por ellos. Se compara ademas la carpeta exacta
        // (sin separador) porque tambien se llama para borrar carpetas vacias.
        foreach (var pref in PrefijosProtegidos)
        {
            var p = pref.AsSpan();
            if (full.StartsWith(p, StringComparison.OrdinalIgnoreCase) ||
                (p.Length > 1 && recortada.Equals(p[..^1], StringComparison.OrdinalIgnoreCase)))
            { motivo = "zona protegida: " + pref.TrimEnd('\\'); return false; }
        }

        return true;
    }
}
