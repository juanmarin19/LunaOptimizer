using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FluentCleaner.Models;
using FluentCleaner.Services;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class CleanerView : UserControl
{
    private readonly Winapp2Parser _parser = new();
    private readonly DetectionService _detect = new();
    private readonly Action<string> _go;
    private List<CleanerEntry> _entries = new();
    private static readonly Dictionary<string, BitmapSource?> _appIcons = new(StringComparer.OrdinalIgnoreCase);

    public CleanerView(Action<string> go)
    {
        InitializeComponent();
        _go = go;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        TxtStatus.Text = "Cargando Winapp2.ini...";
        var content = await Task.Run(DeepCleanService.LoadWinapp2);
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

    private async Task AutoAnalyzeAsync()
    {
        long total = 0;
        var vms = List.Items.Cast<EntryVm>().ToList();
        int done = 0;
        foreach (var vm in vms)
        {
            // Una regla con ruta rara no debe tumbar el analisis completo.
            try
            {
                var r = await Services.ScanEngine.AnalyzeAsync(vm.Entry, null, default);
                vm.Size = r.FormattedSize; vm.Result = r;
                total += r.TotalBytes;
            }
            catch { vm.Size = "-"; }
            done++;
        }
        List.Items.Refresh();
        TxtStatus.Text = $"{vms.Count} apps instaladas, {ScanResult.FormatBytes(total)} de datos acumulados.";
    }

    private void BtnDeep_Click(object sender, RoutedEventArgs e) => _go("deepclean");

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
