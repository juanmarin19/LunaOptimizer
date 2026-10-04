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
            TxtMemPct.Text = $"{pct}%";
            TxtMemFree.Text = $"{free / 1024.0:F1} GB";
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
            TxtApps.Text = $"{apps} Aplicaciones";
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
            TxtDiskVal.Text = $"{usedGB:F1}GB/{totalGB:F1}GB";
            BarDisk.Value = totalGB > 0 ? Math.Min(100, usedGB / totalGB * 100) : 0;
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

    /// Tamanio aproximado de %TEMP% (con tope para no bloquear la UI).
    private async Task UpdateTempAsync()
    {
        if (_tempBusy) return;
        _tempBusy = true;
        try
        {
            long bytes = await Task.Run(() =>
            {
                long sum = 0; int n = 0;
                var sw = Stopwatch.StartNew();
                var pending = new Stack<string>();
                pending.Push(Path.GetTempPath());
                while (pending.Count > 0 && n < 8000 && sw.ElapsedMilliseconds < 1500)
                {
                    var cur = pending.Pop();
                    string[] files;
                    try { files = Directory.GetFiles(cur); } catch { files = Array.Empty<string>(); }
                    foreach (var f in files)
                    {
                        try { sum += new FileInfo(f).Length; n++; } catch { }
                        if (n >= 8000) break;
                    }
                    string[] dirs;
                    try { dirs = Directory.GetDirectories(cur); } catch { dirs = Array.Empty<string>(); }
                    foreach (var d in dirs)
                    {
                        try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) pending.Push(d); }
                        catch { }
                    }
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
            var (trimmed, gained, _) = await Task.Run(() => MemoryBoost.Boost());
            TxtHint.Text = $"Listo: {trimmed} procesos optimizados y +{gained} MB de RAM libre ({DateTime.Now:HH:mm}).";
            _status($"Mejora de rendimiento completada: +{gained} MB libres.");
            UpdateMemory();
        }
        catch (Exception ex) { TxtHint.Text = "Error: " + ex.Message; }
        finally
        {
            BarAct.Visibility = Visibility.Collapsed;
            BtnBoost.IsEnabled = true;
        }
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
            var (trimmed, gained, _) = await Task.Run(() => MemoryBoost.Boost());

            _lastClean = DateTime.Now;
            UpdateLastClean();
            string extra = failedClose > 0 ? $", {failedClose} protegidos" : "";
            TxtHint.Text = $"Reinicio limpio: {closed} procesos cerrados{extra}, " +
                           $"{r.FilesDeleted} temporales borrados ({TempCleaner.FormatBytes(r.BytesFreed)}), " +
                           $"+{gained} MB de RAM ({DateTime.Now:HH:mm}).";
            _status($"Limpieza rapida: {closed} procesos cerrados, {TempCleaner.FormatBytes(r.BytesFreed)} borrados, +{gained} MB de RAM.");
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
