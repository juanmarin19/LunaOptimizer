using System.IO;
using System.Runtime.InteropServices;

namespace LunaOptimizer.Services;

public class CarpetaUso
{
    public string Nombre { get; set; } = "";
    public string Ruta { get; set; } = "";
    public long Bytes { get; set; }
    public bool Existe { get; set; } = true;
    public string Tamano => TempCleaner.FormatBytes(Bytes);
}

public class ArchivoGrande
{
    public string Ruta { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime Modificado { get; set; }
    public string Nombre => Path.GetFileName(Ruta);
    public string Carpeta => Path.GetDirectoryName(Ruta) ?? "";
    public string Tamano => TempCleaner.FormatBytes(Bytes);
    public string FechaTexto => Modificado.ToString("dd/MM/yyyy");
}

/// <summary>
/// Almacenamiento estilo PC Manager: espacio por unidad, tamanio de las carpetas
/// del usuario, archivos grandes y envio a la papelera (recuperable).
/// </summary>
public static class StorageService
{
    public record Unidad(string Nombre, long Total, long Libre)
    {
        public long Usado => Total - Libre;
        public double UsadoPct => Total <= 0 ? 0 : Usado * 100.0 / Total;
        public string Detalle => $"{TempCleaner.FormatBytes(Usado)} de {TempCleaner.FormatBytes(Total)}";
    }

    public static List<Unidad> GetUnidades()
    {
        var lista = new List<Unidad>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                    string letra = d.Name.TrimEnd('\\');
                    string etiqueta;
                    try { etiqueta = d.VolumeLabel; } catch { etiqueta = ""; }
                    string nombre = string.IsNullOrWhiteSpace(etiqueta)
                        ? (d.DriveType == DriveType.Removable ? $"Extraible ({letra})" : $"Disco local ({letra})")
                        : $"{etiqueta} ({letra})";
                    lista.Add(new Unidad(nombre, d.TotalSize, d.AvailableFreeSpace));
                }
                catch { }
            }
        }
        catch (Exception ex) { AppLog.Error(ex, "unidades"); }
        return lista;
    }

    public static List<(string Nombre, string Ruta)> CarpetasConocidas()
    {
        var lista = new List<(string, string)>();
        void Add(string nombre, string ruta)
        {
            try
            {
                if (!string.IsNullOrEmpty(ruta) && Directory.Exists(ruta)) lista.Add((nombre, ruta));
            }
            catch { }
        }

        string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("Descargas", Path.Combine(perfil, "Downloads"));
        Add("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Imagenes", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add("Musica", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("Escritorio", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Temporales", Path.GetTempPath());
        return lista;
    }

    public static async Task<List<CarpetaUso>> MedirCarpetasAsync(
        IProgress<string>? progreso, CancellationToken token)
    {
        var carpetas = CarpetasConocidas();
        var resultado = new List<CarpetaUso>();
        foreach (var (nombre, ruta) in carpetas)
        {
            token.ThrowIfCancellationRequested();
            progreso?.Report($"Midiendo {nombre}...");
            long bytes = await Task.Run(() => MedirCarpeta(ruta, token), token).ConfigureAwait(false);
            resultado.Add(new CarpetaUso { Nombre = nombre, Ruta = ruta, Bytes = bytes });
        }
        return resultado.OrderByDescending(c => c.Bytes).ToList();
    }

    public static long MedirCarpeta(string ruta, CancellationToken token = default)
    {
        long total = 0;
        try
        {
            foreach (var archivo in Directory.EnumerateFiles(ruta, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                     }))
            {
                token.ThrowIfCancellationRequested();
                try { total += new FileInfo(archivo).Length; } catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return total;
    }

    public static async Task<List<ArchivoGrande>> BuscarGrandesAsync(
        long minimoBytes, IProgress<string>? progreso, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            var lista = new List<ArchivoGrande>();
            var raices = new List<string>();
            string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Directory.Exists(perfil)) raices.Add(perfil);
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && d.DriveType == DriveType.Fixed && !perfil.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase))
                        raices.Add(d.Name);
                }
                catch { }
            }

            foreach (var raiz in raices)
            {
                token.ThrowIfCancellationRequested();
                progreso?.Report($"Buscando en {raiz}...");
                try
                {
                    foreach (var archivo in Directory.EnumerateFiles(raiz, "*", new EnumerationOptions
                             {
                                 RecurseSubdirectories = true,
                                 IgnoreInaccessible = true,
                                 AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                             }))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var fi = new FileInfo(archivo);
                            if (fi.Length >= minimoBytes)
                                lista.Add(new ArchivoGrande
                                {
                                    Ruta = archivo,
                                    Bytes = fi.Length,
                                    Modificado = fi.LastWriteTime,
                                });
                        }
                        catch { }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppLog.Error(ex, "buscar grandes " + raiz); }
            }

            return lista.OrderByDescending(a => a.Bytes).Take(80).ToList();
        }, token).ConfigureAwait(false);
    }

    /// <summary>Borra a la papelera (recuperable), nunca definitivo.</summary>
    public static bool EnviarAPapelera(string ruta)
    {
        try
        {
            if (!SafetyGuard.CanDelete(ruta, out _)) return false;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = ruta + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            return SHFileOperation(ref op) == 0;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "papelera " + ruta);
            return false;
        }
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
