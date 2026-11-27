using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

/// <summary>
/// Limpieza profunda con vista previa: tras analizar se lista, categoria por
/// categoria, exactamente que se va a borrar (archivos con su tamano, o apps con
/// su cache) y se puede destildar lo que no se quiera tocar. Los datos de
/// navegadores no aparecen nunca: los filtra BrowserGuard al analizar.
/// </summary>
public partial class DeepCleanView : UserControl
{
    private enum Phase { Idle, Scanning, Scanned, Cleaning, Cleaned, Cancelled }

    private static readonly Brush BrWait = new SolidColorBrush(Color.FromRgb(0xB9, 0xB9, 0xB9));
    private static readonly Brush BrAccent = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
    private static readonly Brush BrOk = new SolidColorBrush(Color.FromRgb(0x5F, 0xC7, 0x7F));
    private static readonly Brush BrWarn = new SolidColorBrush(Color.FromRgb(0xF5, 0xC7, 0x6A));

    private readonly Action<string> _nav;
    private readonly Action<string> _status;
    private readonly TextBlock[] _st;
    private List<DeepCatResult>? _results;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _cleanCts;
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
        // Idle/Cancelled: primera vez o se cancelo. Cleaned: volvemos tras limpiar,
        // conviene re-analizar para no mostrar resultados ya borrados.
        if (_phase is Phase.Idle or Phase.Cancelled or Phase.Cleaned) _ = StartScanAsync();
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
            case Phase.Cleaning:
                TxtHeadSub.Text = "Cancelando...";
                _cleanCts?.Cancel();
                break;
            case Phase.Scanned:
                await CleanAsync();
                break;
            default:
                await StartScanAsync();
                break;
        }
    }

    // ======================= Analisis =======================

    private async Task StartScanAsync()
    {
        _phase = Phase.Scanning;
        _cts = new CancellationTokenSource();
        _results = null;
        ResetRows();
        PanelProgreso.Visibility = Visibility.Visible;
        PanelResultados.Visibility = Visibility.Collapsed;
        PanelResultados.Children.Clear();
        BarScan.Value = 0;
        BarScan.Visibility = Visibility.Visible;
        BtnAction.Content = "Cancelar";
        BtnAction.Style = (Style)FindResource(typeof(Button));
        BtnAction.IsEnabled = true;
        TxtHead.Text = "Analizando el equipo...";
        TxtHeadSub.Text = "Preparando el análisis.";
        TxtStatus.Text = "";
        var pr = new Progress<DeepScanUpdate>(Apply);
        try
        {
            var cats = await DeepCleanService.ScanAsync(pr, _cts.Token);
            _results = cats;
            _phase = Phase.Scanned;
            long total = cats.Sum(c => c.Bytes);
            int files = cats.Sum(c => c.Files.Count);
            int regs = cats.Sum(c => c.RegistryCount);
            BarScan.Value = 100;
            TxtHead.Text = $"Se detectaron {TempCleaner.FormatBytes(total)} para limpiar";
            TxtHeadSub.Text = $"{files} archivos · {regs} entradas de registro. Revisa la lista y destilda lo que quieras conservar.";
            BtnAction.Style = (Style)FindResource("PrimaryBtn");
            BtnAction.IsEnabled = true;
            RenderResultados(cats);
            ActualizarTotales();

            PanelProgreso.Visibility = Visibility.Collapsed;
            PanelResultados.Visibility = Visibility.Visible;
            _status($"Limpieza profunda: {TempCleaner.FormatBytes(total)} listos para revisar.");

            // Al pintar la lista, WPF deja la vista desplazada: se vuelve arriba
            // para que se vean el total, el aviso y la primera categoria.
            _ = Dispatcher.BeginInvoke(new Action(ScrollArriba),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            _phase = Phase.Cancelled;
            MarkCancelled();
            BtnAction.Content = "Analizar";
            BtnAction.Style = (Style)FindResource(typeof(Button));
            BtnAction.IsEnabled = true;
            TxtHead.Text = "Análisis cancelado";
            TxtHeadSub.Text = "";
            TxtStatus.Text = "Análisis cancelado.";
            _status("Limpieza profunda: análisis cancelado.");
        }
        catch (Exception ex)
        {
            _phase = Phase.Idle;
            BtnAction.Content = "Reintentar";
            BtnAction.Style = (Style)FindResource(typeof(Button));
            BtnAction.IsEnabled = true;
            TxtHead.Text = "No se pudo analizar";
            TxtHeadSub.Text = ex.Message;
            TxtStatus.Text = "Error: " + ex.Message;
            AppLog.Error(ex, "analisis limpieza profunda");
        }
    }

    // ======================= Vista previa =======================

    /// Devuelve la pagina al principio (el ScrollViewer vive en MainWindow).
    private void ScrollArriba()
    {
        try
        {
            DependencyObject? d = this;
            while (d is not null)
            {
                if (d is ScrollViewer sv) { sv.ScrollToTop(); return; }
                d = VisualTreeHelper.GetParent(d);
            }
        }
        catch { }
    }

    private void RenderResultados(List<DeepCatResult> cats)
    {
        PanelResultados.Children.Clear();
        // La primera categoria con contenido se abre sola: que se vea de entrada
        // que hay una lista concreta y no solo numeros.
        int abierta = cats.FindIndex(c => c.Bytes > 0 || c.Files.Count > 0 || c.RegistryCount > 0);
        for (int i = 0; i < cats.Count; i++)
            PanelResultados.Children.Add(TarjetaCategoria(cats[i], i, i == abierta));
    }

    private UIElement TarjetaCategoria(DeepCatResult cat, int idx, bool expandir)
    {
        var contenedor = new StackPanel();

        // --- cabecera: casilla + nombre + tamano + flecha ---
        var cabecera = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var chk = new CheckBox
        {
            IsChecked = cat.Incluida,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            Tag = cat,
            ToolTip = "Incluir esta categoría en la limpieza",
        };
        chk.Click += (_, _) =>
        {
            cat.Incluida = chk.IsChecked == true;
            contenedor.Opacity = cat.Incluida ? 1 : 0.45;
            ActualizarTotales();
        };
        cabecera.Children.Add(chk);

        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titulo = new TextBlock
        {
            Text = cat.Name,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        titulo.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
        textos.Children.Add(titulo);

        string sub = cat.IsApps
            ? $"{cat.Apps.Count} aplicaciones detectadas"
            : cat.IsRecycle
                ? $"{cat.Files.Count} elementos en la papelera"
                : $"{cat.Files.Count} archivos";
        var detalle = new TextBlock { Text = sub, FontSize = 10, Margin = new Thickness(0, 1, 0, 0) };
        detalle.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
        textos.Children.Add(detalle);
        Grid.SetColumn(textos, 1);
        cabecera.Children.Add(textos);

        var tam = new TextBlock
        {
            Text = TempCleaner.FormatBytes(cat.Bytes),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
        };
        tam.SetResourceReference(TextBlock.ForegroundProperty, "B_Link");
        Grid.SetColumn(tam, 2);
        cabecera.Children.Add(tam);

        var flecha = new Path
        {
            Data = (Geometry)FindResource("I.ChevronRight"),
            Width = 9,
            Height = 9,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.8,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = new RotateTransform(0),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        flecha.SetResourceReference(Shape.StrokeProperty, "B_Sub");
        Grid.SetColumn(flecha, 3);
        cabecera.Children.Add(flecha);

        // --- detalle (plegado/desplegado) ---
        var panelDetalle = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        LlenarDetalle(cat, panelDetalle);
        if (expandir && panelDetalle.Children.Count > 0)
        {
            panelDetalle.Visibility = Visibility.Visible;
            flecha.RenderTransform = new RotateTransform(90);
        }

        var cabeceraClic = new Border
        {
            Child = cabecera,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Padding = new Thickness(0, 2, 0, 2),
            Tag = panelDetalle,
        };
        cabeceraClic.MouseLeftButtonUp += (_, e) =>
        {
            bool abrir = panelDetalle.Visibility != Visibility.Visible;
            panelDetalle.Visibility = abrir ? Visibility.Visible : Visibility.Collapsed;
            flecha.RenderTransform = new RotateTransform(abrir ? 90 : 0);
            e.Handled = true;
        };

        var tarjeta = new Border
        {
            Background = (Brush)FindResource("B_Card"),
            BorderBrush = (Brush)FindResource("B_CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var interior = new StackPanel();
        interior.Children.Add(cabeceraClic);
        interior.Children.Add(panelDetalle);
        tarjeta.Child = interior;

        // La categoria vacia no se muestra: no hay nada que revisar.
        if (cat.Bytes <= 0 && cat.Files.Count == 0 && cat.RegistryCount == 0)
        {
            tarjeta.Opacity = 0.6;
            chk.IsEnabled = false;
        }

        contenedor.Children.Add(tarjeta);
        return contenedor;
    }

    private void LlenarDetalle(DeepCatResult cat, StackPanel panel)
    {
        const int maximo = 40;

        if (cat.IsApps)
        {
            foreach (var app in cat.Apps.OrderByDescending(a => a.Bytes).Take(maximo))
            {
                var fila = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                fila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var chkApp = new CheckBox
                {
                    IsChecked = app.Incluida,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Tag = app,
                    ToolTip = "Borrar tambien la cache de esta aplicacion",
                };
                chkApp.Click += (_, _) =>
                {
                    app.Incluida = chkApp.IsChecked == true;
                    fila.Opacity = app.Incluida ? 1 : 0.45;
                    ActualizarTotales();
                };
                fila.Children.Add(chkApp);

                var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var nombre = new TextBlock
                {
                    Text = app.Nombre,
                    FontSize = 11.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                nombre.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
                textos.Children.Add(nombre);
                var info = new TextBlock
                {
                    Text = $"{app.Archivos} archivos" + (app.Registro > 0 ? $" · {app.Registro} de registro" : ""),
                    FontSize = 10,
                };
                info.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
                textos.Children.Add(info);
                Grid.SetColumn(textos, 1);
                fila.Children.Add(textos);

                var tam = new TextBlock
                {
                    Text = TempCleaner.FormatBytes(app.Bytes),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                tam.SetResourceReference(TextBlock.ForegroundProperty, "B_Sub");
                Grid.SetColumn(tam, 2);
                fila.Children.Add(tam);

                panel.Children.Add(fila);
            }

            int ocultas = Math.Max(0, cat.Apps.Count - maximo);
            if (ocultas > 0) panel.Children.Add(Nota($"y {ocultas} aplicaciones más"));
            return;
        }

        // Archivos (recomendados, sistema, seguimientos y papelera): los mas grandes
        // primero, con la carpeta para que se vea de donde salen.
        var vistas = cat.Sizes.Count > 0
            ? cat.Sizes.OrderByDescending(kv => kv.Value).Take(maximo).ToList()
            : cat.Files.Take(maximo).Select(f => new KeyValuePair<string, long>(f, 0)).ToList();

        foreach (var (ruta, tamano) in vistas)
        {
            var fila = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textos = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var nombre = new TextBlock
            {
                Text = System.IO.Path.GetFileName(ruta),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            nombre.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
            textos.Children.Add(nombre);

            var carpeta = new TextBlock
            {
                Text = CarpetaCorta(ruta),
                FontSize = 9.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            carpeta.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
            textos.Children.Add(carpeta);

            var conTip = new Border { Child = textos, Background = Brushes.Transparent, ToolTip = ruta };
            fila.Children.Add(conTip);

            var tam = new TextBlock
            {
                Text = TempCleaner.FormatBytes(tamano),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tam.SetResourceReference(TextBlock.ForegroundProperty, "B_Sub");
            Grid.SetColumn(tam, 1);
            fila.Children.Add(tam);

            panel.Children.Add(fila);
        }

        int restantes = Math.Max(0, cat.Files.Count - maximo);
        if (restantes > 0) panel.Children.Add(Nota($"y {restantes} archivos más"));
    }

    private TextBlock Nota(string texto)
    {
        var t = new TextBlock { Text = texto, FontSize = 10, Margin = new Thickness(0, 5, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
        return t;
    }

    private static string CarpetaCorta(string ruta)
    {
        try
        {
            var partes = ruta.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length <= 2) return ruta;
            return "…\\" + partes[^2] + "\\" + partes[^1];
        }
        catch { return ruta; }
    }

    private void ActualizarTotales()
    {
        if (_results is null) return;
        var (bytes, files, regs) = DeepCleanService.TotalesSeleccionados(_results);
        TxtHeadSub.Text = $"{files} archivos seleccionados · {TempCleaner.FormatBytes(bytes)}"
                          + (regs > 0 ? $" · {regs} de registro" : "");
        TxtStatus.Text = "Toca una categoría para ver y destildar exactamente lo que se va a borrar.";
        BtnAction.Content = bytes > 0 || regs > 0
            ? $"Limpiar {TempCleaner.FormatBytes(bytes)}"
            : "Nada seleccionado";
        BtnAction.IsEnabled = bytes > 0 || regs > 0;
    }

    // ======================= Limpieza =======================

    private async Task CleanAsync()
    {
        if (_results is null) return;
        var (total, files, regs) = DeepCleanService.TotalesSeleccionados(_results);
        if (total <= 0 && regs <= 0) return;

        string detalle = $"Eliminar {files} archivos ({TempCleaner.FormatBytes(total)})"
            + (regs > 0 ? $" y {regs} entradas de registro" : "") + "?\n\n" +
            "Las categorias y aplicaciones destildadas se conservan.";
        var r = MessageBox.Show(detalle, "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        _phase = Phase.Cleaning;
        BtnAction.Content = "Cancelar limpieza";
        BtnAction.Style = (Style)FindResource(typeof(Button));
        BtnAction.IsEnabled = true;
        BarScan.Value = 0;
        BarScan.Visibility = Visibility.Visible;
        PanelResultados.Visibility = Visibility.Collapsed;
        PanelProgreso.Visibility = Visibility.Visible;
        TxtHeadSub.Text = "Eliminando archivos...";
        TxtStatus.Text = "Limpiando...";
        var pr = new Progress<DeepScanUpdate>(Apply);
        using var cts = new CancellationTokenSource();
        _cleanCts = cts;
        try
        {
            var cats = _results;
            var (cleaned, bytes, cleanedRegs) = await Task.Run(
                () => DeepCleanService.Clean(cats, pr, cts.Token), cts.Token);

            _phase = Phase.Cleaned;
            BarScan.Value = 100;
            TxtHead.Text = $"Se liberaron {TempCleaner.FormatBytes(bytes)}";
            TxtHeadSub.Text = $"{cleaned} archivos eliminados."
                              + (cleanedRegs > 0 ? $" {cleanedRegs} entradas de registro." : "");
            BtnAction.Content = "Analizar otra vez";
            BtnAction.Style = (Style)FindResource(typeof(Button));
            BtnAction.IsEnabled = true;
            TxtStatus.Text = $"Limpieza: {cleaned} archivos, {TempCleaner.FormatBytes(bytes)} liberados."
                             + (cleanedRegs > 0 ? $" {cleanedRegs} de registro." : "");
            _status($"Limpieza profunda: {TempCleaner.FormatBytes(bytes)} liberados" +
                    (cleanedRegs > 0 ? $" + {cleanedRegs} de registro." : "."));
        }
        catch (OperationCanceledException)
        {
            _phase = Phase.Scanned;
            BtnAction.Content = "Limpiar";
            BtnAction.Style = (Style)FindResource("PrimaryBtn");
            BtnAction.IsEnabled = true;
            TxtHeadSub.Text = "Limpieza cancelada.";
            TxtStatus.Text = "Limpieza cancelada.";
            PanelProgreso.Visibility = Visibility.Collapsed;
            PanelResultados.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _phase = Phase.Scanned;
            BtnAction.Content = "Limpiar";
            BtnAction.Style = (Style)FindResource("PrimaryBtn");
            BtnAction.IsEnabled = true;
            TxtHeadSub.Text = "Error: " + ex.Message;
            TxtStatus.Text = "Error: " + ex.Message;
            PanelProgreso.Visibility = Visibility.Collapsed;
            PanelResultados.Visibility = Visibility.Visible;
            AppLog.Error(ex, "limpieza profunda");
        }
        finally { _cleanCts = null; }
    }

    // ======================= Progreso =======================

    private void Apply(DeepScanUpdate u)
    {
        BarScan.Value = u.Percent;
        if (u.CatIndex >= 0 && u.CatIndex < _st.Length)
            SetRow(u.CatIndex, u.State, u.CatBytes, u.Phase == DeepPhase.Clean);
        if (u.Phase == DeepPhase.Scan)
        {
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
