using System.Runtime.InteropServices;

namespace LunaOptimizer.Services;

/// <summary>
/// Detecta si el usuario esta consumiendo multimedia para NO interrumpirla.
/// Motivo: purgar la lista standby / la cache de archivos o vaciar los working
/// sets de todo el sistema mientras se ve un video o se juega deja al reproductor
/// sin paginas en memoria y sin cache de disco -> tirones y, con decodificacion por
/// hardware, cuelgues. Igual que PC Manager: si hay algo reproduciendose, no se toca.
///
/// Dos señales independientes, ambas "best effort" (si fallan, no se bloquea nada):
///   1) Pantalla completa D3D / modo presentacion (SHQueryUserNotificationState):
///      cubre juegos y videos a pantalla completa.
///   2) Nivel de audio del dispositivo de salida por defecto
///      (IAudioMeterInformation::GetPeakValue): cubre cualquier cosa que suene,
///      incluido un video de YouTube en el navegador o musica de fondo.
/// </summary>
public static class MediaGuard
{
    /// Se puede desactivar si alguna vez molesta (p. ej. musica sonando siempre).
    public static bool ProteccionActiva { get; set; } = true;

    /// true si hay multimedia activa (video/juego/musica) y conviene no interrumpir.
    public static bool EstaActivo()
    {
        if (!ProteccionActiva) return false;
        try { if (PantallaCompletaOD3D()) return true; } catch { }
        try { if (PicoActual() > UmbralPico) return true; } catch { }
        return false;
    }

    /// Pico de audio actual del dispositivo de salida por defecto (0..1).
    /// 0 si no hay dispositivo o falla. Sirve tambien para diagnostico/logs.
    public static float PicoActual()
    {
        // El nivel fluctua (silencio entre notas): muestreamos unas veces y tomamos el pico.
        float pico = 0;
        object? meterObj = null;
        IMMDevice? dev = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out dev) != 0 || dev is null)
                return 0;

            var iid = typeof(IAudioMeterInformation).GUID;
            if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out meterObj) != 0 || meterObj is null)
                return 0;

            var meter = (IAudioMeterInformation)meterObj;
            for (int i = 0; i < 3; i++)
            {
                if (meter.GetPeakValue(out float p) != 0) break;
                if (p > pico) pico = p;
                Thread.Sleep(40);
            }
            return pico;
        }
        finally
        {
            if (meterObj is not null && Marshal.IsComObject(meterObj)) Marshal.ReleaseComObject(meterObj);
            if (dev is not null && Marshal.IsComObject(dev)) Marshal.ReleaseComObject(dev);
        }
    }

    // ---------- Señal 1: pantalla completa / presentacion ----------

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private static bool PantallaCompletaOD3D()
    {
        // QUERY_USER_NOTIFICATION_STATE:
        // 1 NOT_PRESENT · 2 BUSY (app a pantalla completa) · 3 D3D FULL SCREEN
        // 4 PRESENTATION MODE · 5 ACCEPTS NOTIFICATIONS · 6 QUIET TIME · 7 APP
        if (SHQueryUserNotificationState(out int st) != 0) return false;
        return st is 2 or 3 or 4;
    }

    // ---------- Señal 2: audio sonando en la salida por defecto ----------

    private const int eRender = 0;
    private const int eMultimedia = 1;
    private const int CLSCTX_ALL = 0x17;
    private const float UmbralPico = 0.01f;   // ~ -40 dB: por encima se considera "suena"

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role,
            [MarshalAs(UnmanagedType.Interface)] out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
    }
}
