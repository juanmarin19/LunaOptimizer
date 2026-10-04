using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class DeepCleanView : UserControl
{
    private enum Phase { Idle, Scanning, Scanned, Cleaning, Cleaned, Cancelled }

    private static readonly Brush BrWait = new SolidColorBrush(Color.FromRgb(0xB9, 0xB9, 0xB9));
    private static readonly Brush BrAccent = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
    private static readonly Brush BrOk = new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x7F));
    private static readonly Brush BrWarn = new SolidColorBrush(Color.FromRgb(0xF5, 0xC7, 0x6A));

    private readonly Action<string> _nav;
    private readonly Action<string> _status;
    private readonly TextBlock[] _st;
    private List<DeepCatResult>? _results;
    private CancellationTokenSource? _cts;
    private Phase _phase = Phase.Idle;

    public string BackTarget { get; set; } = "home";

    public DeepCleanView(Action<string> nav, Action<string> status)
    {
        InitializeComponent();
        _nav = nav;
        _status = status;
        _st = new[] { St0, St1, St2, St3, St4 };
        ResetRows();
    }

    public void OnShown()
    {
        if (_phase is Phase.Idle or Phase.Cancelled) _ = StartScanAsync();
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e) => _nav(BackTarget);

    private async void BtnAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_phase)
        {
            case Phase.Scanning:
                BtnAction.IsEnabled = false;
                TxtHeadSub.Text = "Cancelando...";
                _cts?.Cancel();
                break;
            case Phase.Scanned:
                await CleanAsync();
                break;
            default:
                await StartScanAsync();
                break;
        }
    }

    private async Task StartScanAsync()
    {
        _phase = Phase.Scanning;
        _cts = new CancellationTokenSource();
        ResetRows();
        BarScan.Value = 0;
        BarScan.Visibility = Visibility.Visible;
        BtnAction.Content = "Cancelar";
        BtnAction.Style = (Style)FindResource(typeof(Button));
        BtnAction.IsEnabled = true;
        TxtHead.Text = "Se detectaron 0 elementos para limpiar";
        TxtHeadSub.Text = "Preparando el analisis...";
        TxtStatus.Text = "Examinando el equipo...";
        var pr = new Progress<DeepScanUpdate>(Apply);
        try
        {
            var cats = await DeepCleanService.ScanAsync(pr, _cts.Token);
            _results = cats;
            _phase = Phase.Scanned;
            long total = cats.Sum(c => c.Bytes);
            int files = cats.Sum(c => c.Files.Count);
            int found = cats.Count(c => c.Bytes > 0);
            BarScan.Value = 100;
            TxtHead.Text = $"Se detectaron {TempCleaner.FormatBytes(total)} elementos para limpiar";
            TxtHeadSub.Text = $"{files} archivos en {found} de {cats.Count} categorias.";
            BtnAction.Content = "Limpiar";
            BtnAction.Style = (Style)FindResource("PrimaryBtn");
            BtnAction.IsEnabled = true;
            TxtStatus.Text = total > 0
                ? $"Analisis completado: {TempCleaner.FormatBytes(total)}."
                : "No se encontraron elementos para limpiar.";
            _status($"Limpieza profunda: {TempCleaner.FormatBytes(total)} listos para limpiar.");
        }
        catch (OperationCanceledException)
        {
            _phase = Phase.Cancelled;
            MarkCancelled();
            BtnAction.Content = "Analizar";
            BtnAction.Style = (Style)FindResource(typeof(Button));
            BtnAction.IsEnabled = true;
            TxtHeadSub.Text = "Analisis cancelado.";
            TxtStatus.Text = "Analisis cancelado.";
            _status("Limpieza profunda: analisis cancelado.");
        }
        catch (Exception ex)
        {
            _phase = Phase.Idle;
            BtnAction.Content = "Reintentar";
            BtnAction.Style = (Style)FindResource(typeof(Button));
            BtnAction.IsEnabled = true;
            TxtHeadSub.Text = "Error: " + ex.Message;
            TxtStatus.Text = "Error: " + ex.Message;
        }
    }

    private async Task CleanAsync()
    {
        if (_results is null) return;
        long total = _results.Sum(c => c.Bytes);
        int files = _results.Sum(c => c.Files.Count);
        if (total <= 0) return;
        var r = MessageBox.Show($"Eliminar {files} archivos ({TempCleaner.FormatBytes(total)})?",
            "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        _phase = Phase.Cleaning;
        BtnAction.Content = "Limpiando...";
        BtnAction.IsEnabled = false;
        BarScan.Value = 0;
        TxtHeadSub.Text = "Eliminando archivos...";
        TxtStatus.Text = "Limpiando...";
        var pr = new Progress<DeepScanUpdate>(Apply);
        try
        {
            var cats = _results;
            var (cleaned, bytes) = await Task.Run(() => DeepCleanService.Clean(cats, pr, default));
            _phase = Phase.Cleaned;
            BarScan.Value = 100;
            TxtHead.Text = $"Se liberaron {TempCleaner.FormatBytes(bytes)}";
            TxtHeadSub.Text = $"{cleaned} archivos eliminados.";
            BtnAction.Content = "Analizar";
            BtnAction.Style = (Style)FindResource(typeof(Button));
            BtnAction.IsEnabled = true;
            TxtStatus.Text = $"Limpieza: {cleaned} archivos, {TempCleaner.FormatBytes(bytes)} liberados.";
            _status($"Limpieza profunda: {TempCleaner.FormatBytes(bytes)} liberados.");
        }
        catch (Exception ex)
        {
            _phase = Phase.Scanned;
            BtnAction.Content = "Limpiar";
            BtnAction.Style = (Style)FindResource("PrimaryBtn");
            BtnAction.IsEnabled = true;
            TxtHeadSub.Text = "Error: " + ex.Message;
            TxtStatus.Text = "Error: " + ex.Message;
        }
    }

    private void Apply(DeepScanUpdate u)
    {
        BarScan.Value = u.Percent;
        if (u.CatIndex >= 0 && u.CatIndex < _st.Length)
            SetRow(u.CatIndex, u.State, u.CatBytes, u.Phase == DeepPhase.Clean);
        if (u.Phase == DeepPhase.Scan)
        {
            TxtHead.Text = $"Se detectaron {TempCleaner.FormatBytes(u.TotalBytes)} elementos para limpiar";
            if (u.State == CatState.Scanning)
                TxtHeadSub.Text = u.Current.Length > 0 ? $"Examinando: {u.Current}" : "Examinando...";
        }
        else if (u.State == CatState.Cleaning)
        {
            TxtHeadSub.Text = $"Limpiando: {DeepCleanService.CatNames[u.CatIndex]}";
        }
    }

    private void SetRow(int idx, CatState state, long bytes, bool cleaning)
    {
        if (idx < 0 || idx >= _st.Length) return;
        string text;
        Brush color;
        switch (state)
        {
            case CatState.Waiting:
                text = "Esperando a examinar..."; color = BrWait; break;
            case CatState.Scanning:
                text = "Examinando..."; color = BrAccent; break;
            case CatState.Cleaning:
                text = "Limpiando..."; color = BrAccent; break;
            default:
                long scan = 0;
                if (_results is not null && idx < _results.Count) scan = _results[idx].Bytes;
                if (cleaning)
                {
                    // Distingue "no habia nada" de "habia cosas pero estaban en uso".
                    if (scan <= 0) { text = "Sin elementos"; color = BrWait; }
                    else if (bytes > 0) { text = $"Liberado {TempCleaner.FormatBytes(bytes)}"; color = BrOk; }
                    else { text = "No liberado (en uso)"; color = BrWarn; }
                }
                else if (bytes > 0)
                {
                    text = $"Descubierto {TempCleaner.FormatBytes(bytes)}";
                    color = BrOk;
                }
                else
                {
                    text = "Sin elementos"; color = BrWait;
                }
                break;
        }
        _st[idx].Text = text;
        _st[idx].Foreground = color;
    }

    private void ResetRows()
    {
        foreach (var t in _st)
        {
            t.Text = "Esperando a examinar...";
            t.Foreground = BrWait;
        }
    }

    private void MarkCancelled()
    {
        foreach (var t in _st)
        {
            if (t.Text is "Examinando..." or "Esperando a examinar...")
            {
                t.Text = "Cancelado";
                t.Foreground = BrWarn;
            }
        }
        BarScan.Visibility = Visibility.Collapsed;
    }
}
