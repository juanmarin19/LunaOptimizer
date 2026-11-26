using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class HomeView : UserControl
{
    private readonly Action<string> _go;
    private readonly Action<string> _status;
    private bool _tempBusy;
    private CancellationTokenSource? _cts;

    // Ultimos valores mostrados, guardados como numero (nunca re-parsear el texto
    // del control: "11,3" con InvariantCulture se lee 113).
    private double? _memPctPrev;
    private double? _diskPrev;
    private double _appsPrev;
    private string? _bootTexto;
    private DateTime _bootFecha = DateTime.MinValue;

    public HomeView(Action<string> go, Action<string> status)
    {
        InitializeComponent();
        _go = go;
        _status = status;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        UpdateMemory();
        UpdateApps();
        UpdateDisk();
        UpdateRevision();
        UpdateSmart();
        _ = UpdateTempAsync();
        _ = UpdateBootAsync();
        UpdateLastClean();
    }

    /// Lo llama el temporizador de MainWindow cada 3 segundos.
    public void UpdateMemory()
    {
        try
        {
            var (total, free) = ProcessService.GetMemory();
            if (total <= 0) return;
            int pct = (int)Math.Round((total - free) * 100.0 / total);
            double libreGB = free / 1024.0;

            Tween.Text("mempct", _memPctPrev ?? pct, pct, 380, "{0:0}%", s => TxtMemPct.Text = s);
            _memPctPrev = pct;

            double alto = Math.Clamp(pct * 34.0 / 100.0, 3, 34);
            BarMemPct.Height = alto;
        }
        catch { }
    }

    private void UpdateApps()
    {
        try
        {
            int apps = 0;
            foreach (var p in Process.GetProcesses())
            {
                try { if (!string.IsNullOrEmpty(p.MainWindowTitle)) apps++; }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            Tween.Text("apps", _appsPrev, apps, 380, "{0:0} aplicaciones", s => TxtApps.Text = s);
            _appsPrev = apps;
        }
        catch { TxtApps.Text = "—"; }
    }

    private void UpdateDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
            var d = new DriveInfo(root);
            double usedGB = (d.TotalSize - d.AvailableFreeSpace) / 1073741824.0;
            double totalGB = d.TotalSize / 1073741824.0;
            TxtDiskName.Text = $"Disco local ({d.Name.TrimEnd('\\')})";
            Tween.To("disk", _diskPrev ?? usedGB, usedGB, 380, v =>
                TxtDiskVal.Text = $"{v:F1} GB de {totalGB:F0} GB");
            _diskPrev = usedGB;
            double pct = totalGB > 0 ? Math.Min(100, usedGB / totalGB * 100) : 0;
            Tween.To("bar", BarDisk.Value, pct, 420, v => BarDisk.Value = v);
        }
        catch { TxtDiskVal.Text = "—"; }
    }

    private void UpdateRevision()
    {
        try
        {
            var cfg = SettingsStore.Current;
            if (DateTime.TryParse(cfg.UltimaRevision, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var fecha))
            {
                var hace = DateTime.Now - fecha;
                string cuando = hace.TotalMinutes < 2 ? "Ahora mismo"
                    : hace.TotalHours < 1 ? $"Hace {(int)hace.TotalMinutes} min"
                    : hace.TotalDays < 1 ? $"Hace {(int)hace.TotalHours} h"
                    : $"Hace {(int)hace.TotalDays} días";
                TxtRevision.Text = cfg.UltimaPuntuacion >= 0 ? $"{cuando} · {cfg.UltimaPuntuacion}%" : cuando;

                if (cfg.UltimaPuntuacion >= 85)
                {
                    TxtHeroTitle.Text = "Tu PC está en buen estado";
                    TxtHeroSub.Text = $"Puntuación {cfg.UltimaPuntuacion}%. Pulsa para revisar otra vez.";
                }
                else if (cfg.UltimaPuntuacion >= 0)
                {
                    TxtHeroTitle.Text = "Tu PC necesita una revisión";
                    TxtHeroSub.Text = $"Puntuación {cfg.UltimaPuntuacion}%. Hay cosas que puedes mejorar.";
                }
            }
            else
            {
                TxtRevision.Text = "Nunca";
                TxtHeroTitle.Text = "Revisa el estado de tu PC";
                TxtHeroSub.Text = "Temporales, memoria, disco, arranque y antivirus en una pasada.";
            }
        }
        catch { }
    }

    private void UpdateSmart()
    {
        bool on = SettingsStore.Current.SmartBoost;
        BtnSmart.Content = on ? "Smart Boost: activado" : "Activar Smart Boost";
    }

    private void UpdateLastClean()
    {
        var deep = DeepCleanService.LastClean;
        TxtLastClean.Text = deep is null
            ? "Temporales, caches y DNS. Como reiniciar sin reiniciar."
            : $"Ultima limpieza: {deep.Value:dd/MM HH:mm}. Temporales, caches y DNS.";
    }

    /// Tamanio de %TEMP% (recorrido compartido con la limpieza rapida).
    private async Task UpdateTempAsync()
    {
        if (_tempBusy) return;
        _tempBusy = true;
        try
        {
            var (_, bytes) = await Task.Run(() => TempCleaner.MeasureTemp());
            TxtTempVal.Text = TempCleaner.FormatBytes(bytes);
            BarTemp.Height = Math.Clamp(bytes / 1073741824.0 * 20 + 3, 3, 34);
        }
        catch { TxtTempVal.Text = "—"; }
        finally { _tempBusy = false; }
    }

    private async Task UpdateBootAsync()
    {
        if (_bootTexto is not null && (DateTime.Now - _bootFecha).TotalMinutes < 60)
        {
            TxtBoot.Text = _bootTexto;
            return;
        }
        try
        {
            string t = await Task.Run(StartupService.TiempoArranqueTexto);
            _bootTexto = t;
            _bootFecha = DateTime.Now;
            TxtBoot.Text = t;
        }
        catch { TxtBoot.Text = "—"; }
    }

    private void BtnSmart_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SettingsStore.Current;
        cfg.SmartBoost = !cfg.SmartBoost;
        SettingsStore.Save();
        UpdateSmart();
        _status(cfg.SmartBoost
            ? $"Smart Boost activado: se optimizará solo cuando la RAM pase del {cfg.SmartBoostRamPct}%."
            : "Smart Boost desactivado.");
    }

    private async void BtnBoost_Click(object sender, RoutedEventArgs e)
    {
        BtnBoost.IsEnabled = false;
        BarAct.Visibility = Visibility.Visible;
        BtnCancel.Visibility = Visibility.Visible;
        TxtHint.Text = "Optimizando memoria...";
        _cts = new CancellationTokenSource();
        try
        {
            var boost = await Task.Run(() => MemoryBoost.Boost());
            TxtHint.Text = "Borrando temporales...";
            var prog = new Progress<string>(s => TxtHint.Text = s);
            var r = await TempCleaner.QuickCleanAsync(prog, _cts.Token);

            TxtHint.Text = ResumenBoost(boost.trimmed, boost.multimedia, boost.usoAntesMB, boost.usoDespuesMB, r)
                           + $" ({DateTime.Now:HH:mm}).";
            if (boost.multimedia)
                _status($"Multimedia en curso: no se tocó la RAM. {r.FilesDeleted} temporales borrados.");
            else
                _status($"Mejora de rendimiento: RAM en uso {boost.usoAntesMB / 1024.0:F1} GB -> " +
                        $"{boost.usoDespuesMB / 1024.0:F1} GB, {r.FilesDeleted} temporales borrados.");
            UpdateMemory();
            _ = UpdateTempAsync();
        }
        catch (OperationCanceledException) { TxtHint.Text = "Cancelado."; }
        catch (Exception ex) { TxtHint.Text = "Error: " + ex.Message; }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            BarAct.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Collapsed;
            BtnBoost.IsEnabled = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); TxtHint.Text = "Cancelando..."; } catch { }
    }

    private static string ResumenBoost(int trimmed, bool multimedia, long usoAntesMB, long usoDespuesMB,
        TempCleaner.CleanResult r)
    {
        string gpu = r.Multimedia ? " (sin caches de GPU)" : "";
        if (multimedia)
            return "Multimedia en curso: no se tocó la RAM para no cortar el video/juego" +
                   $" · {r.FilesDeleted} temporales borrados{gpu} ({TempCleaner.FormatBytes(r.BytesFreed)})";

        long baja = Math.Max(0, usoAntesMB - usoDespuesMB);
        string msg = $"{trimmed} procesos · RAM en uso {usoAntesMB / 1024.0:F1} GB -> " +
                     $"{usoDespuesMB / 1024.0:F1} GB (-{baja / 1024.0:F1} GB)" +
                     $" · {r.FilesDeleted} temporales borrados{gpu} ({TempCleaner.FormatBytes(r.BytesFreed)})";
        if (r.ScheduledReboot > 0)
            msg += $" · {r.ScheduledReboot} al reiniciar ({TempCleaner.FormatBytes(r.ScheduledBytes)})";
        return msg;
    }

    private async void BtnQuickClean_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Limpieza rapida: como un reinicio, sin reiniciar.\n\n" +
            "  · Se cierran tus aplicaciones abiertas (navegador, Discord...)\n" +
            "  · Se borran temporales, caches y la DNS\n" +
            "  · Se libera la memoria RAM\n\n" +
            "Lo que no hayas guardado en las apps se pierde. ¿Continuar?",
            "Limpieza rapida", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        BtnQuickClean.IsEnabled = false;
        BtnBoost.IsEnabled = false;
        BarAct.Visibility = Visibility.Visible;
        BtnCancel.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var prog = new Progress<string>(s => TxtHint.Text = s);
        try
        {
            var (closed, failedClose) = await Task.Run(() => ProcessService.CloseAllApps(prog));
            var r = await TempCleaner.QuickCleanAsync(prog, _cts.Token);
            var boost = await Task.Run(() => MemoryBoost.Boost());

            string extra = failedClose > 0 ? $", {failedClose} protegidos" : "";
            string reinicio = r.ScheduledReboot > 0 ? $", {r.ScheduledReboot} al reiniciar" : "";
            long baja = Math.Max(0, boost.usoAntesMB - boost.usoDespuesMB);
            string ramTxt = boost.multimedia
                ? "RAM no liberada (multimedia en curso)"
                : $"RAM en uso -{baja / 1024.0:F1} GB";
            TxtHint.Text = $"Reinicio limpio: {closed} procesos cerrados{extra}, " +
                           $"{r.FilesDeleted} temporales borrados ({TempCleaner.FormatBytes(r.BytesFreed)}){reinicio}, " +
                           $"{ramTxt} ({DateTime.Now:HH:mm}).";
            _status($"Limpieza rapida: {closed} procesos cerrados, {TempCleaner.FormatBytes(r.BytesFreed)} borrados{reinicio}, {ramTxt}.");
            UpdateMemory();
            UpdateApps();
            UpdateLastClean();
            _ = UpdateTempAsync();
        }
        catch (OperationCanceledException) { TxtHint.Text = "Cancelado."; }
        catch (Exception ex) { TxtHint.Text = "Error: " + ex.Message; }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            BarAct.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Collapsed;
            BtnQuickClean.IsEnabled = true;
            BtnBoost.IsEnabled = true;
        }
    }

    private void GoProtect(object sender, MouseButtonEventArgs e) => _go("protect");
    private void GoProc(object sender, MouseButtonEventArgs e) => _go("proc");
    private void GoDeep(object sender, MouseButtonEventArgs e) => _go("deep");
    private void GoStartup(object sender, MouseButtonEventArgs e) => _go("startup");
}
