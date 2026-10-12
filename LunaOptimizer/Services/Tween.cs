using System.Windows.Threading;

namespace LunaOptimizer.Services;

/// Animaciones numericas suaves (contadores, barras) sin Storyboard:
/// un timer por objeto animado, con cancelacion por clave.
internal static class Tween
{
    private static readonly Dictionary<object, DispatcherTimer> Activos = new();

    public static void To(object clave, double desde, double hasta, double ms, Action<double> aplicar)
    {
        if (Activos.TryGetValue(clave, out var viejo))
        {
            viejo.Stop();
            Activos.Remove(clave);
        }
        if (ms <= 0 || Math.Abs(hasta - desde) < 0.0001)
        {
            aplicar(hasta);
            return;
        }

        var t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        t.Tick += (_, _) =>
        {
            double p = Math.Min(1, sw.ElapsedMilliseconds / ms);
            double e = 1 - Math.Pow(1 - p, 3); // easeOutCubic
            aplicar(desde + (hasta - desde) * e);
            if (p >= 1)
            {
                t.Stop();
                Activos.Remove(clave);
            }
        };
        Activos[clave] = t;
        t.Start();
    }

    /// Igual que To, pero formateando el valor a texto en cada paso.
    public static void Text(object clave, double desde, double hasta, double ms, string formato, Action<string> aplicar)
        => To(clave, desde, hasta, ms, v => aplicar(string.Format(formato, v)));
}
