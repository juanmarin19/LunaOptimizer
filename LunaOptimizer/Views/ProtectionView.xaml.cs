using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class ProtectionView : UserControl
{
    private readonly Action<string> _go;
    private readonly Action<string> _status;
    private CancellationTokenSource? _cts;
    private bool _revisado;
    private bool _examenActivo;

    public ProtectionView(Action<string> go, Action<string> status)
    {
        InitializeComponent();
        _go = go;
        _status = status;
        Loaded += (_, _) => { PintarGuardado(); _ = CargarDefenderAsync(); };
    }

    public void OnShown()
    {
        if (!_revisado) _ = RevisarAsync();
        _ = CargarDefenderAsync();
    }

    private void PintarGuardado()
    {
        var cfg = SettingsStore.Current;
        if (cfg.UltimaPuntuacion >= 0) PintarPuntuacion(cfg.UltimaPuntuacion);
    }

    private void PintarPuntuacion(int puntos)
    {
        TxtScore.Text = puntos.ToString();
        var color = puntos >= 85 ? (Color)ColorConverter.ConvertFromString("#2E9E5B")
            : puntos >= 60 ? (Color)ColorConverter.ConvertFromString("#C98A00")
            : (Color)ColorConverter.ConvertFromString("#C42B1C");
        var brocha = new SolidColorBrush(color);
        brocha.Freeze();
        ScoreRing.BorderBrush = brocha;
        TxtScore.Foreground = brocha;

        TxtRevTitulo.Text = puntos >= 85 ? "Tu PC está en buen estado"
            : puntos >= 60 ? "Tu PC puede ir mejor"
            : "Tu PC necesita atención";
    }

    private async Task RevisarAsync()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        BtnRevisar.IsEnabled = false;
        BtnRevisar.Content = "Revisando...";
        BarRev.Visibility = Visibility.Visible;
        ListaRevision.Children.Clear();

        var progreso = new Progress<string>(s => TxtRevProgreso.Text = s);
        try
        {
            var r = await HealthCheckService.RevisarAsync(progreso, _cts.Token);
            PintarPuntuacion(r.Puntuacion);
            TxtRevDetalle.Text = r.Resumen + $" · {r.Fecha:HH:mm}";
            TxtRevProgreso.Text = r.TodoBien ? "No hay nada que corregir." : "Pulsa el enlace de cada línea para arreglarlo.";
            PintarItems(r);
            _revisado = true;
            _status($"Revisión de salud: {r.Puntuacion} puntos, {r.Resumen.ToLowerInvariant()}.");

            // Al pintar las filas, WPF puede desplazar el ScrollViewer para dejar
            // visible el ultimo elemento: se devuelve la vista al principio.
            _ = Dispatcher.BeginInvoke(new Action(ScrollArriba), System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { TxtRevProgreso.Text = "Revision cancelada."; }
        catch (Exception ex)
        {
            TxtRevProgreso.Text = "Error: " + ex.Message;
            AppLog.Error(ex, "revisión de salud");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            BtnRevisar.IsEnabled = true;
            BtnRevisar.Content = "Revisar otra vez";
            BarRev.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Devuelve la pagina al principio (el ScrollViewer vive en MainWindow).</summary>
    private void ScrollArriba()
    {
        try
        {
            System.Windows.DependencyObject? d = this;
            while (d is not null)
            {
                if (d is ScrollViewer sv) { sv.ScrollToTop(); return; }
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
        }
        catch { }
    }

    private void PintarItems(RevisionResultado r)
    {
        ListaRevision.Children.Clear();
        foreach (var item in r.Items)
        {
            var fila = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var punto = new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(1, 6, 9, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Fill = new SolidColorBrush(item.Nivel switch
                {
                    2 => Color.FromRgb(0xC4, 0x2B, 0x1C),
                    1 => Color.FromRgb(0xC9, 0x8A, 0x00),
                    _ => Color.FromRgb(0x2E, 0x9E, 0x5B),
                }),
            };
            Grid.SetColumn(punto, 0);
            fila.Children.Add(punto);

            var textos = new StackPanel();
            textos.Children.Add(new TextBlock
            {
                Text = item.Titulo,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("B_Text"),
                TextWrapping = TextWrapping.Wrap,
            });
            textos.Children.Add(new TextBlock
            {
                Text = item.Detalle,
                FontSize = 10.5,
                Foreground = (Brush)FindResource("B_Sub"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });
            Grid.SetColumn(textos, 1);
            fila.Children.Add(textos);

            if (item.Accion != AccionRevision.Ninguna && !string.IsNullOrEmpty(item.AccionTexto))
            {
                var btn = new Button
                {
                    Content = item.AccionTexto,
                    Style = (Style)FindResource("LinkBtn"),
                    Tag = item,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                btn.Click += Accion_Click;
                Grid.SetColumn(btn, 2);
                fila.Children.Add(btn);
            }

            ListaRevision.Children.Add(fila);
        }
    }

    private async void Accion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RevisionItem item }) return;
        switch (item.Accion)
        {
            case AccionRevision.LimpiarTemporales:
                BtnRevisar.IsEnabled = false;
                BarRev.Visibility = Visibility.Visible;
                try
                {
                    var prog = new Progress<string>(s => TxtRevProgreso.Text = s);
                    var r = await TempCleaner.QuickCleanAsync(prog);
                    TxtRevProgreso.Text = $"Temporales: {TempCleaner.FormatBytes(r.BytesFreed)} liberados.";
                    _status($"Temporales limpiados: {TempCleaner.FormatBytes(r.BytesFreed)}.");
                    await RevisarAsync();
                }
                catch (Exception ex) { TxtRevProgreso.Text = "Error: " + ex.Message; }
                break;
            case AccionRevision.AbrirArranque:
                _go("startup");
                break;
            case AccionRevision.AbrirAlmacenamiento:
                _go("storage");
                break;
            case AccionRevision.AbrirLimpieza:
                _go("deep");
                break;
            case AccionRevision.AbrirDefender:
                AbrirSeguridadWindows();
                break;
        }
    }

    private async void BtnRevisar_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        await RevisarAsync();
    }

    private async Task CargarDefenderAsync()
    {
        try
        {
            var def = await DefenderService.GetEstadoAsync();
            if (!def.Disponible)
            {
                TxtDefEstado.Text = def.Error ?? "No disponible";
                TxtDefTiempoReal.Text = "Sin datos";
                TxtDefExamen.Text = "—";
                BtnExamen.IsEnabled = false;
                return;
            }
            BtnExamen.IsEnabled = true;
            TxtDefEstado.Text = def.TiempoReal ? "Protegido" : "Desatendido";
            TxtDefTiempoReal.Text = def.TiempoReal ? "Activada" : "Desactivada";
            TxtDefExamen.Text = def.UltimoExamenTexto;
        }
        catch (Exception ex) { TxtDefEstado.Text = ex.Message; }
    }

    private async void BtnExamen_Click(object sender, RoutedEventArgs e)
    {
        if (_examenActivo)
        {
            DefenderService.Cancelar();
            TxtExamen.Text = "Cancelando examen...";
            return;
        }

        _examenActivo = true;
        BtnExamen.Content = "Cancelar";
        BarExamen.Visibility = Visibility.Visible;
        TxtExamen.Text = "Analizando... puede tardar unos minutos. El PC seguira respondiendo.";
        try
        {
            var (codigo, salida) = await DefenderService.ExamenRapidoAsync(CancellationToken.None);
            TxtExamen.Text = codigo switch
            {
                0 => "Examen completado: no se encontró ninguna amenaza.",
                2 => "Examen completado: se encontraron amenazas y Windows Defender ya las esta tratando.",
                -2 => "Examen cancelado.",
                _ => "No se pudo completar el examen. " + PrimeraLinea(salida),
            };
            _status(codigo == 0 ? "Examen rapido: sin amenazas." : TxtExamen.Text);
            _ = CargarDefenderAsync();
        }
        catch (Exception ex) { TxtExamen.Text = "Error: " + ex.Message; }
        finally
        {
            _examenActivo = false;
            BtnExamen.Content = "Examen rapido";
            BarExamen.Visibility = Visibility.Collapsed;
        }
    }

    private static string PrimeraLinea(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "";
        var linea = texto.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return linea.Trim();
    }

    private void BtnSeguridad_Click(object sender, RoutedEventArgs e) => AbrirSeguridadWindows();

    private static void AbrirSeguridadWindows()
    {
        try { Process.Start(new ProcessStartInfo("windowsdefender://threat") { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true }); } catch { } }
    }

    private void BtnNotificaciones_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); }
        catch (Exception ex) { _status("No se pudo abrir Configuracion: " + ex.Message); }
    }
}
