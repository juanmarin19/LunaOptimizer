using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class HomeView : UserControl
{
    private readonly Action<string> _go;
    private readonly Action<string> _status;
    private DateTime? _lastClean;
    private bool _tempBusy;

    // Historial de uso de RAM: 20 muestras cada 3 s = 60 segundos.
    private readonly double[] _hist = new double[20];
    private int _histN;
    private double _appsPrev;

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
            double liberoGB = free / 1024.0;
            double prevPct = double.TryParse(TxtMemPct.Text.Replace("%", ""),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
            double prevFree = double.TryParse(TxtMemFree.Text.Replace(" GB", ""),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f : liberoGB;

            Tween.Text("mempct", prevPct, pct, 420, "{0:0}%", s => TxtMemPct.Text = s);
            Tween.Text("memfree", prevFree, liberoGB, 420, "{0:F1} GB", s => TxtMemFree.Text = s);
            TxtRamTotal.Text = $"RAM {total / 1024.0:F0} GB";

            Array.Copy(_hist, 1, _hist, 0, _hist.Length - 1);
            _hist[^1] = pct;
            if (_histN < _hist.Length) _histN++;
            DibujarSpark();
        }
        catch { }
    }

    /// Grafico lineal del uso de RAM (se reescala al tamanio real del Path).
    private void DibujarSpark()
    {
        if (SparkRam is null || _histN < 2) return;
        var sb = new StringBuilder();
        int n = _histN;
        for (int i = 0; i < n; i++)
        {
            int idx = _hist.Length - n + i;
            double x = i * 100.0 / (n - 1);
            double y = 100 - Math.Clamp(_hist[idx], 0, 100);
            sb.Append(i == 0 ? "M" : "L")
              .Append(x.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(y.ToString("F2", CultureInfo.InvariantCulture)).Append(' ');
        }
        try { SparkRam.Data = Geometry.Parse(sb.ToString()); } catch { }
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
            double prevUsed = double.TryParse(TxtDiskVal.Text.Split('/')[0].Replace("GB", ""),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var u) ? u : usedGB;
            Tween.To("disk", prevUsed, usedGB, 420, v =>
                TxtDiskVal.Text = $"{v:F1}GB/{totalGB:F1}GB");
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
            long bytes = await Task.Run(() =>
            {
                long sum = 0;
                foreach (var f in TempCleaner.SafeEnumerateFiles(Path.GetTempPath()))
                {
                    long len = TempCleaner.SizeOf(f);
                    if (len > 0) sum += len;
                }
                return sum;
            });
            TxtTempVal.Text = TempCleaner.FormatBytes(bytes);
        }
        catch { TxtTempVal.Text = "-"; }
        finally { _tempBusy = false; }
    }

    private async void BtnBoost_Click(object sender, RoutedEventArgs e)
    {
        BtnBoost.IsEnabled = false;
        BarAct.Visibility = Visibility.Visible;
        TxtHint.Text = "Optimizando memoria...";
        try
        {
            var boost = await Task.Run(() => MemoryBoost.Boost());
            TxtHint.Text = "Borrando temporales...";
            var prog = new Progress<string>(s => TxtHint.Text = s);
            var r = await TempCleaner.QuickCleanAsync(prog);

            TxtHint.Text = ResumenBoost(boost.trimmed, boost.usoAntesMB, boost.usoDespuesMB, r) + $" ({DateTime.Now:HH:mm}).";
            _status($"Mejora de rendimiento: RAM en uso {boost.usoAntesMB / 1024.0:F1} GB -> " +
                    $"{boost.usoDespuesMB / 1024.0:F1} GB, {r.FilesDeleted} temporales borrados.");
            UpdateMemory();
            _ = UpdateTempAsync();
        }
        catch (Exception ex) { TxtHint.Text = "Error: " + ex.Message; }
        finally
        {
            BarAct.Visibility = Visibility.Collapsed;
            BtnBoost.IsEnabled = true;
        }
    }

    /// "N procesos · RAM en uso 9,2 GB -> 6,7 GB (−2,5 GB) · X temporales (Y MB) · Z al reiniciar"
    private static string ResumenBoost(int trimmed, long usoAntesMB, long usoDespuesMB, TempCleaner.CleanResult r)
    {
        long baja = Math.Max(0, usoAntesMB - usoDespuesMB);
        string msg = $"{trimmed} procesos · RAM en uso {usoAntesMB / 1024.0:F1} GB -> {usoDespuesMB / 1024.0:F1} GB (−{baja / 1024.0:F1} GB)" +
                     $" · {r.FilesDeleted} temporales borrados ({TempCleaner.FormatBytes(r.BytesFreed)})";
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
        var prog = new Progress<string>(s => TxtHint.Text = s);
        try
        {
            var (closed, failedClose) = await Task.Run(() => ProcessService.CloseAllApps(prog));
            var r = await TempCleaner.QuickCleanAsync(prog);
            var boost = await Task.Run(() => MemoryBoost.Boost());

            _lastClean = DateTime.Now;
            UpdateLastClean();
            string extra = failedClose > 0 ? $", {failedClose} protegidos" : "";
            string reinicio = r.ScheduledReboot > 0 ? $", {r.ScheduledReboot} al reiniciar" : "";
            long baja = Math.Max(0, boost.usoAntesMB - boost.usoDespuesMB);
            TxtHint.Text = $"Reinicio limpio: {closed} procesos cerrados{extra}, " +
                           $"{r.FilesDeleted} temporales borrados ({TempCleaner.FormatBytes(r.BytesFreed)}){reinicio}, " +
                           $"RAM en uso −{baja / 1024.0:F1} GB ({DateTime.Now:HH:mm}).";
            _status($"Limpieza rapida: {closed} procesos cerrados, {TempCleaner.FormatBytes(r.BytesFreed)} borrados{reinicio}, " +
                    $"RAM en uso −{baja / 1024.0:F1} GB.");
            UpdateMemory();
            UpdateApps();
            _ = UpdateTempAsync();
        }
        catch (Exception ex) { TxtHint.Text = "Error: " + ex.Message; }
        finally
        {
            BarAct.Visibility = Visibility.Collapsed;
            BtnQuickClean.IsEnabled = true;
            BtnBoost.IsEnabled = true;
        }
    }

    private void GoProcesos(object sender, MouseButtonEventArgs e) => _go("proc");
    private void GoLimpieza(object sender, MouseButtonEventArgs e) => _go("clean");
    private void GoDeep(object sender, MouseButtonEventArgs e) => _go("deep");
}
