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
    private DateTime? _lastClean;
    private bool _tempBusy;
    private CancellationTokenSource? _cts;

    // Ultimos valores MOSTRADOS, guardados como numero. Antes se re-parseaba el
    // texto del control ("11,3 GB") con CultureInfo.InvariantCulture, donde la coma
    // es separador de miles: "11,3" -> 113 y la animacion arrancaba desde ahi, asi
    // que el valor "saltaba" en cada refresco. Con un campo ya no se parsea nada.
    private double? _memPctPrev;
    private double? _memFreePrev;
    private double? _diskPrev;
    private double _appsPrev;

    public HomeView(Action<string> go, Action<string> status)
    {
        InitializeComponent();
        _go = go;
        _status = status;
        // MediaGuard queda activo por defecto (ver MediaGuard.ProteccionActiva);
        // ya no hay control visible en la interfaz.
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        UpdateMemory();
        UpdateApps();
        UpdateDisk();
        _ = UpdateTempAsync();
        UpdateLastClean();
    }

    /// Lo llama el temporizador de MainWindow cada pocos segundos.
    public void UpdateMemory()
    {
        try
        {
            var (total, free) = ProcessService.GetMemory();
            if (total <= 0) return;
            int pct = (int)Math.Round((total - free) * 100.0 / total);
            double libreGB = free / 1024.0;

            Tween.Text("mempct", _memPctPrev ?? pct, pct, 420, "{0:0}%", s => TxtMemPct.Text = s);
            Tween.Text("memfree", _memFreePrev ?? libreGB, libreGB, 420, "{0:F1} GB", s => TxtMemFree.Text = s);
            _memPctPrev = pct;
            _memFreePrev = libreGB;
            TxtRamTotal.Text = $"RAM {total / 1024.0:F0} GB";
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
            Tween.Text("apps", _appsPrev, apps, 420, "{0:0} Aplicaciones", s => TxtApps.Text = s);
            _appsPrev = apps;
        }
        catch { TxtApps.Text = "-"; }
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
            Tween.To("disk", _diskPrev ?? usedGB, usedGB, 420, v =>
                TxtDiskVal.Text = $"{v:F1}GB/{totalGB:F1}GB");
            _diskPrev = usedGB;
            double prevBar = BarDisk.Value;
            Tween.To("bar", prevBar, Math.Min(100, usedGB / totalGB * 100), 500, v => BarDisk.Value = v);
        }
        catch { TxtDiskVal.Text = "-"; }
    }

    private void UpdateLastClean()
    {
        DateTime? last = _lastClean;
        var deep = DeepCleanService.LastClean;
        if (deep is not null && (last is null || deep > last)) last = deep;
        TxtLastClean.Text = last is null ? "Nunca" : last.Value.ToString("HH:mm");
    }

    /// Tamanio de %TEMP% (recorrido compartido con la limpieza rapida).
    private async Task UpdateTempAsync()
    {
        if (_tempBusy) return;
        _tempBusy = true;
        try
        {
            // Igual que la limpieza rapida: TEMP de TODOS los usuarios, no solo el actual.
            var (_, bytes) = await Task.Run(() => TempCleaner.MeasureTemp());
            TxtTempVal.Text = TempCleaner.FormatBytes(bytes);
        }
        catch { TxtTempVal.Text = "-"; }
        finally { _tempBusy = false; }
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

            TxtHint.Text = ResumenBoost(boost.trimmed, boost.multimedia, boost.usoAntesMB, boost.usoDespuesMB, r) + $" ({DateTime.Now:HH:mm}).";
            if (boost.multimedia)
                _status($"Multimedia en curso: no se toco la RAM. {r.FilesDeleted} temporales borrados.");
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

    /// Cancela la fase cancelable (borrado de temporales) de la operacion en curso.
    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); TxtHint.Text = "Cancelando..."; } catch { }
    }

    /// "N procesos · RAM en uso 9,2 GB -> 6,7 GB (−2,5 GB) · X temporales (Y MB) · Z al reiniciar"
    private static string ResumenBoost(int trimmed, bool multimedia, long usoAntesMB, long usoDespuesMB, TempCleaner.CleanResult r)
    {
        string gpu = r.Multimedia ? " (sin caches de GPU)" : "";
        if (multimedia)
            return "Multimedia en curso: no se toco la RAM para no cortar el video/juego" +
                   $" · {r.FilesDeleted} temporales borrados{gpu} ({TempCleaner.FormatBytes(r.BytesFreed)})";

        long baja = Math.Max(0, usoAntesMB - usoDespuesMB);
        string msg = $"{trimmed} procesos · RAM en uso {usoAntesMB / 1024.0:F1} GB -> {usoDespuesMB / 1024.0:F1} GB (−{baja / 1024.0:F1} GB)" +
                     $" · {r.FilesDeleted} temporales borrados{gpu} ({TempCleaner.FormatBytes(r.BytesFreed)})";
        if (r.ScheduledReboot > 0)
            msg += $" · {r.ScheduledReboot} al reiniciar ({TempCleaner.FormatBytes(r.ScheduledBytes)})";
        return msg;
    }

    private async void BtnQuickClean_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Limpieza rapida: como un reinicio, sin reiniciar.\n\n" +
            "  - Se cierran tus aplicaciones abiertas (navegador, Discord...)\n" +
            "  - Se borran temporales, caches y la DNS\n" +
            "  - Se libera la memoria RAM\n\n" +
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

            _lastClean = DateTime.Now;
            UpdateLastClean();
            string extra = failedClose > 0 ? $", {failedClose} protegidos" : "";
            string reinicio = r.ScheduledReboot > 0 ? $", {r.ScheduledReboot} al reiniciar" : "";
            long baja = Math.Max(0, boost.usoAntesMB - boost.usoDespuesMB);
            string ramTxt = boost.multimedia
                ? "RAM no liberada (multimedia en curso)"
                : $"RAM en uso −{baja / 1024.0:F1} GB";
            TxtHint.Text = $"Reinicio limpio: {closed} procesos cerrados{extra}, " +
                           $"{r.FilesDeleted} temporales borrados ({TempCleaner.FormatBytes(r.BytesFreed)}){reinicio}, " +
                           $"{ramTxt} ({DateTime.Now:HH:mm}).";
            _status($"Limpieza rapida: {closed} procesos cerrados, {TempCleaner.FormatBytes(r.BytesFreed)} borrados{reinicio}, " +
                    $"{ramTxt}.");
            UpdateMemory();
            UpdateApps();
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

    private void GoProcesos(object sender, MouseButtonEventArgs e) => _go("proc");
    private void GoLimpieza(object sender, MouseButtonEventArgs e) => _go("clean");
    private void GoDeep(object sender, MouseButtonEventArgs e) => _go("deep");
}
