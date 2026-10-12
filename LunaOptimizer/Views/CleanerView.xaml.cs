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
            if (TxtEmpty is not null)
            {
                TxtEmpty.Text = "No se pudo cargar la base de limpieza.\n" +
                                "Descarga Winapp2.ini junto a LunaOptimizer.exe y vuelve a abrir la app.";
                TxtEmpty.Visibility = Visibility.Visible;
            }
            return;
        }
        await Task.Run(() =>
        {
            var all = _parser.Parse(content);
            _entries = all.Where(e => _detect.IsInstalled(e)).ToList();
        });
        List.ItemsSource = _entries.Select(e => new EntryVm(e)).ToList();
        ActualizarVacio();
        TxtStatus.Text = $"{_entries.Count} apps detectadas. Analizando tamanos...";
        // Auto-analizar tamanos en fondo para que no aparezca "-" (tu reporte)
        _ = AutoAnalyzeAsync();
    }

    /// Mensaje centrado cuando no hay nada que listar.
    private void ActualizarVacio()
    {
        if (TxtEmpty is null || List is null) return;
        if (List.Items.Count > 0)
        {
            TxtEmpty.Visibility = Visibility.Collapsed;
            return;
        }
        TxtEmpty.Text = "No se detectaron aplicaciones instaladas con reglas de limpieza.";
        TxtEmpty.Visibility = Visibility.Visible;
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
            TxtStatus.Text = $"Analizando tamanos... {done}/{vms.Count}";
        }
        TxtStatus.Text = $"{vms.Count} apps instaladas, {ScanResult.FormatBytes(total)} de datos acumulados.";
    }

    private void BtnDeep_Click(object sender, RoutedEventArgs e) => _go("deepclean");

    /// Vista de cada app de la lista. Implementa INotifyPropertyChanged para
    /// que Size se actualice EN VIVO cuando el analisis en fondo termina con
    /// esa app: sin esto habria que hacer List.Items.Refresh() al final y el
    /// usuario veria "calculando..." minutos enteros.
    public class EntryVm : System.ComponentModel.INotifyPropertyChanged
    {
        private string _size = "calculando...";
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public EntryVm(CleanerEntry e)
        {
            Entry = e; Name = e.Name;
            Icon = ResolveIcon(e);
        }
        public CleanerEntry Entry { get; }
        public string Name { get; }
        public ScanResult? Result { get; set; }
        public BitmapSource? Icon { get; }

        public string Size
        {
            get => _size;
            set { _size = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Size))); }
        }
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
