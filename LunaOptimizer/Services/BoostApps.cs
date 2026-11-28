using System.Diagnostics;

namespace LunaOptimizer.Services;

public sealed class AppPesada
{
    public string Proceso { get; init; } = "";
    public string Nombre { get; init; } = "";
    public List<int> Pids { get; init; } = new();
    public long RamMB { get; init; }
    public string Detalle => $"{Nombre} · {RamMB / 1024.0:F1} GB";
}

/// <summary>
/// Apps de fondo conocidas por consumir recursos, igual que la lista curada de
/// PC Manager (que trae entradas propias para Discord, Razer, Grammarly...).
/// Solo se OFRECEN: el usuario decide si se cierran al optimizar, y se cierran
/// con el mismo mecanismo ordenado + Job Object que el resto.
/// </summary>
public static class BoostApps
{
    private static readonly (string Proceso, string Nombre)[] Conocidas =
    {
        ("Discord", "Discord"),
        ("Spotify", "Spotify"),
        ("Steam", "Steam"),
        ("EpicGamesLauncher", "Epic Games"),
        ("Battle.net", "Battle.net"),
        ("Telegram", "Telegram"),
        ("WhatsApp", "WhatsApp"),
        ("Slack", "Slack"),
        ("ms-teams", "Microsoft Teams"),
        ("Teams", "Microsoft Teams"),
        ("Zoom", "Zoom"),
        ("Grammarly.Desktop", "Grammarly"),
        ("RazerSynapse", "Razer Synapse"),
        ("Razer Central", "Razer Central"),
        ("WeChat", "WeChat"),
        ("SpotifyAB.SpotifyMusic", "Spotify (Store)"),
    };

    /// Apps pesadas que estan corriendo ahora mismo.
    public static List<AppPesada> Detectar()
    {
        var lista = new List<AppPesada>();
        foreach (var grupo in Conocidas.GroupBy(c => c.Nombre))
        {
            var procesos = grupo.Select(g => g.Proceso).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var pids = new List<int>();
            long ram = 0;
            foreach (var nombre in procesos)
            {
                Process[] encontrados;
                try { encontrados = Process.GetProcessesByName(nombre); }
                catch { continue; }
                foreach (var p in encontrados)
                {
                    try
                    {
                        pids.Add(p.Id);
                        ram += p.WorkingSet64 / 1048576;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            if (pids.Count == 0) continue;
            lista.Add(new AppPesada
            {
                Proceso = procesos[0],
                Nombre = grupo.Key,
                Pids = pids,
                RamMB = ram,
            });
        }
        return lista.OrderByDescending(a => a.RamMB).ToList();
    }

    /// Cierra las apps indicadas (ordenado primero, Job Object despues).
    public static (int cerradas, long ramLiberada) Cerrar(IEnumerable<AppPesada> apps)
    {
        int cerradas = 0;
        long ram = 0;
        foreach (var app in apps)
        {
            var r = AppCloser.Cerrar(app.Proceso, app.Pids, ordenado: true);
            if (r.Cerrados > 0)
            {
                cerradas++;
                ram += app.RamMB;
            }
        }
        return (cerradas, ram);
    }
}
