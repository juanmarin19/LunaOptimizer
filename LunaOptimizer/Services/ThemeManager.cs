using System.IO;
using System.Windows;
using System.Windows.Media;

namespace LunaOptimizer.Services;

/// Tema claro/oscuro: intercambia el diccionario Theme/Dark.xaml <-> Theme/Light.xaml,
/// aplica el modo oscuro al marco DWM y guarda la preferencia en disco.
internal static class ThemeManager
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaOptimizer");

    private static readonly string FileTema = Path.Combine(Dir, "tema.txt");
    private static readonly string FileLog = Path.Combine(Dir, "tema.log");

    public static bool Oscuro { get; private set; } = true;

    private static Window? _win;
    private static FrameworkElement? _raiz;

    /// Se llama antes de crear la ventana (App.OnStartup).
    public static void Init()
    {
        try
        {
            string s = File.Exists(FileTema) ? File.ReadAllText(FileTema).Trim() : "oscuro";
            Oscuro = !s.Equals("claro", StringComparison.OrdinalIgnoreCase);
        }
        catch { Oscuro = true; }
        Aplicar(Oscuro, guardar: false);
    }

    /// La ventana se registra tras inicializarse (hwnd disponible).
    public static void Registrar(Window win, FrameworkElement raiz, IntPtr hwnd)
    {
        _win = win;
        _raiz = raiz;
        MicaHelper.SetDarkMode(hwnd, Oscuro);
        AplicarFondos();
        Log($"ventana registrada esquinas={MicaHelper.EsquinasRedondas} tema={(Oscuro ? "oscuro" : "claro")}");
    }

    public static void Toggle() => Aplicar(!Oscuro, guardar: true);

    private static void Aplicar(bool oscuro, bool guardar)
    {
        Oscuro = oscuro;
        try
        {
            var dicts = Application.Current.Resources.MergedDictionaries;
            ResourceDictionary? vieja = null;
            int indice = 0;
            for (int i = 0; i < dicts.Count; i++)
            {
                var src = dicts[i].Source?.OriginalString ?? "";
                if (src.Contains("Dark.xaml") || src.Contains("Light.xaml"))
                {
                    vieja = dicts[i];
                    indice = i;
                    break;
                }
            }
            var nueva = new ResourceDictionary
            {
                Source = new Uri(oscuro ? "Theme/Dark.xaml" : "Theme/Light.xaml", UriKind.Relative)
            };
            if (vieja is not null) dicts.RemoveAt(indice);
            dicts.Insert(indice, nueva);

            if (_win is not null)
            {
                MicaHelper.SetDarkMode(new System.Windows.Interop.WindowInteropHelper(_win).Handle, Oscuro);
                AplicarFondos();
            }
        }
        catch (Exception ex) { Log("error al aplicar tema: " + ex.Message); }

        if (guardar)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FileTema, oscuro ? "oscuro" : "claro");
            }
            catch { }
        }
        Log($"tema={(oscuro ? "oscuro" : "claro")} " + Colores());
    }

    /// Fondo solido de la ventana y de la capa raiz (misma clave que el tema).
    private static void AplicarFondos()
    {
        if (_win is null || _raiz is null) return;
        const string clave = "B_Window";
        _win.SetResourceReference(Window.BackgroundProperty, clave);
        _raiz.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, clave);
    }

    private static string Colores()
    {
        string Br(string k)
        {
            var b = Application.Current.TryFindResource(k) as SolidColorBrush;
            return b is null ? k + "=?" : k + "=" + b.Color;
        }
        return Br("B_Bg") + " " + Br("B_Card") + " " + Br("B_Text");
    }

    private static void Log(string s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(FileLog, $"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
        }
        catch { }
    }
}
