using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FluentCleaner.Models;
using FluentCleaner.Services;

namespace LunaOptimizer.Views;

public partial class CleanerView : UserControl
{
    private readonly Winapp2Parser _parser = new();
    private readonly DetectionService _detect = new();
    private List<CleanerEntry> _entries = new();
    private CancellationTokenSource? _cts;
    private static readonly Dictionary<string, BitmapSource?> _appIcons = new(StringComparer.OrdinalIgnoreCase);

    public CleanerView()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        TxtStatus.Text = "Cargando Winapp2.ini...";
        var content = await Task.Run(LoadWinapp2Async);
        if (content is null)
        {
            TxtStatus.Text = "No se encontro Winapp2.ini (embebido ni en disco).";
            return;
        }
        await Task.Run(() =>
        {
            var all = _parser.Parse(content);
            _entries = all.Where(e => _detect.IsInstalled(e)).ToList();
        });
        List.ItemsSource = _entries.Select(e => new EntryVm(e)).ToList();
        TxtStatus.Text = $"{_entries.Count} apps detectadas. Analizando tamanos...";
        // Auto-analizar tamanos en fondo para que no aparezca "-" (tu reporte)
        _ = AutoAnalyzeAsync();
    }

    /// 1) recurso embebido en el exe (portable, funciona en cualquier PC)
    /// 2) archivo junto al exe   3) copia del repo local
    private static string? LoadWinapp2Async()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("LunaOptimizer.Winapp2.ini");
            if (s is not null)
            {
                using var r = new StreamReader(s);
                return r.ReadToEnd();
            }
        }
        catch { }

        foreach (var path in new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Winapp2.ini"),
            @"D:\LunaOptimizer\FluentCleaner\Winapp2.ini",
        })
        {
            try { if (File.Exists(path)) return File.ReadAllText(path); }
            catch { }
        }
        return null;
    }

    private async Task AutoAnalyzeAsync()
    {
        try
        {
            long total = 0;
            var vms = List.Items.Cast<EntryVm>().ToList();
            foreach (var vm in vms)
            {
                var r = await Services.ScanEngine.AnalyzeAsync(vm.Entry, null, default);
                vm.Size = r.FormattedSize; vm.Result = r;
                total += r.TotalBytes;
            }
            List.Items.Refresh();
            TxtStatus.Text = $"{vms.Count} apps, {ScanResult.FormatBytes(total)} recuperables. Selecciona y pulsa Limpiar.";
        }
        catch { }
    }

    private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
    {
        var sel = List.SelectedItems.Cast<EntryVm>().ToList();
        if (sel.Count == 0) { TxtStatus.Text = "Selecciona al menos 1."; return; }
        _cts = new CancellationTokenSource();
        BtnAnalyze.IsEnabled = false; BtnClean.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        long total = 0; int nfiles = 0;
        var results = new List<(EntryVm vm, ScanResult r)>();
        // Analizar con el CleaningService real de FluentCleaner via reflexion
        // (es public en el proyecto WinUI, usamos nuestra copia local ScanEngine)
        foreach (var vm in sel)
        {
            if (_cts.Token.IsCancellationRequested) break;
            TxtStatus.Text = $"Analizando {vm.Entry.Name}...";
            var r = await Services.ScanEngine.AnalyzeAsync(vm.Entry, null, _cts.Token);
            vm.Size = r.FormattedSize; vm.Result = r;
            total += r.TotalBytes; nfiles += r.FilesToDelete.Count;
            results.Add((vm, r));
            List.Items.Refresh();
        }
        TxtStatus.Text = $"Analisis: {nfiles} archivos, {ScanResult.FormatBytes(total)} recuperables en {sel.Count} apps.";
        BtnAnalyze.IsEnabled = true; BtnClean.IsEnabled = true;
        Bar.Visibility = Visibility.Collapsed;
    }

    private async void BtnClean_Click(object sender, RoutedEventArgs e)
    {
        var done = List.Items.Cast<EntryVm>().Where(v => v.Result != null).ToList();
        if (done.Count == 0) { TxtStatus.Text = "Primero Analizar."; return; }
        var r = MessageBox.Show($"Borrar lo analizado?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        BtnClean.IsEnabled = false; Bar.Visibility = Visibility.Visible;
        int files = 0; long bytes = 0;
        foreach (var vm in done)
        {
            TxtStatus.Text = $"Limpiando {vm.Entry.Name}...";
            var (c, b) = await Services.ScanEngine.CleanAsync(vm.Result!, null, default);
            files += c; bytes += b;
            vm.Result = null; vm.Size = "-";
            List.Items.Refresh();
        }
        TxtStatus.Text = $"Limpieza: {files} archivos, {ScanResult.FormatBytes(bytes)} liberados.";
        BtnClean.IsEnabled = true; Bar.Visibility = Visibility.Collapsed;
    }

    public class EntryVm
    {
        public EntryVm(CleanerEntry e)
        {
            Entry = e; Name = e.Name; Size = "calculando...";
            Icon = ResolveIcon(e);
        }
        public CleanerEntry Entry { get; }
        public string Name { get; }
        public string Size { get; set; }
        public ScanResult? Result { get; set; }
        public BitmapSource? Icon { get; }
    }

    /// Icono: busca el primer DetectFile existente y extrae su icono.
    private static BitmapSource? ResolveIcon(CleanerEntry e)
    {
        try
        {
            var exp = new FluentCleaner.Services.PathExpander();
            foreach (var d in e.DetectFiles)
            {
                foreach (var p in exp.ResolvePaths(d))
                {
                    string exe = p;
                    if (Directory.Exists(p))
                    {
                        exe = Directory.EnumerateFiles(p, "*.exe").FirstOrDefault() ?? "";
                        if (exe == "") continue;
                    }
                    if (!File.Exists(exe)) continue;
                    if (_appIcons.TryGetValue(exe, out var c)) return c;
                    var bmp = Services.IconHelper.Extract(exe);
                    _appIcons[exe] = bmp;
                    return bmp;
                }
            }
        }
        catch { }
        return null;
    }
}
