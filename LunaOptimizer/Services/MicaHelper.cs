using System.Runtime.InteropServices;

namespace LunaOptimizer.Services;

/// Ajustes DWM de la ventana: esquinas redondeadas (Windows 11) y modo
/// oscuro/claro del marco. El fondo de la ventana es siempre solido: con
/// WindowStyle=None y AllowsTransparency=false, el area sin pintar se compone
/// en negro (se comprobo con capturas), asi que no se usa backdrop Mica.
internal static class MicaHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public static bool EsquinasRedondas { get; private set; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static bool TryEnable(IntPtr hwnd)
    {
        EsquinasRedondas = false;
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            int corner = DwmwcpRound;
            int hr = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, 4);
            EsquinasRedondas = hr == 0;
        }
        catch { EsquinasRedondas = false; }
        return EsquinasRedondas;
    }

    /// Modo oscuro/claro del marco (sombra y esquinas) segun el tema elegido.
    public static void SetDarkMode(IntPtr hwnd, bool oscuro)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int v = oscuro ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref v, 4);
        }
        catch { }
    }
}
