using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

/// <summary>
/// Limpieza profunda con el mismo patron que Microsoft PC Manager: cabecera con
/// "Un total de X elementos detectados / Seleccionado: Y" y un arbol de casillas
/// (categoria -> subcategoria -> elemento) donde cada fila se puede destildar.
/// </summary>
public partial class DeepCleanView : UserControl
{
    private enum Phase { Idle, Scanning, Scanned, Cleaning, Cleaned, Cancelled }

    private readonly Action<string> _nav;
    private readonly Action<string> _status;
    private readonly List<NodoUi> _nodos = new();
    private List<DeepCatResult>? _results;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _cleanCts;
    private Phase _phase = Phase.Idle;

    /// Referencias para refrescar etiquetas sin reconstruir el arbol.
    private sealed class NodoUi
    {
        public DeepCatResult? Cat;
        public DeepSubCat? Sub;
        public DeepItem? Item;
        public TextBlock? Etiqueta;
        public CheckBox? Chk;
        public FrameworkElement? Contenedor;
    }

    public string BackTarget { get; set; } = "home";

    public DeepCleanView(Action<string> nav, Action<string> status)
    {
        InitializeComponent();
        _nav = nav;
        _status = status;
    }

    public void OnShown()
    {
        if (_phase is Phase.Idle or Phase.Cancelled or Phase.Cleaned) _ = StartScanAsync();
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e) => _nav(BackTarget);

    private async void BtnAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_phase)
        {
            case Phase.Scanning:
                BtnAction.IsEnabled = false;
                TxtSel.Text = "Cancelando...";
                _cts?.Cancel();
                break;
            case Phase.Cleaning:
                TxtSel.Text = "Cancelando...";
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
        _nodos.Clear();
        PanelArbol.Children.Clear();
        TxtHead.Text = "Analizando el equipo...";
        TxtSel.Text = "";
        BarScan.Value = 0;
        BarScan.Visibility = Visibility.Visible;
        BtnAction.Content = "Cancelar";
        BtnAction.IsEnabled = true;
        TxtStatus.Text = "Buscando elementos que se puedan borrar sin riesgo...";

        var pr = new Progress<DeepScanUpdate>(Apply);
        try
        {
            var cats = await DeepCleanService.ScanAsync(pr, _cts.Token);
            _results = cats;
            _phase = Phase.Scanned;
            BarScan.Visibility = Visibility.Collapsed;

            long total = cats.Sum(c => c.Bytes);
            int files = cats.Sum(c => c.Archivos);
            TxtHead.Text = $"Un total de {Compacto(total)} elementos detectados";
            TxtSel.Text = $"Seleccionado: {Compacto(cats.Sum(c => c.BytesSel))}";
            BtnAction.Content = "Continuar";
            BtnAction.IsEnabled = total > 0 || cats.Any(c => c.Registro > 0);
            ConstruirArbol(cats);
            Refrescar();
            TxtStatus.Text = files == 0
                ? "No se encontraron elementos para limpiar."
                : "Destilda lo que quieras conservar y pulsa Continuar.";
            _status($"Limpieza profunda: {Compacto(total)} detectados.");
            ScrollArriba();
        }
        catch (OperationCanceledException)
        {
            _phase = Phase.Cancelled;
            TxtHead.Text = "Análisis cancelado";
            TxtSel.Text = "";
            BarScan.Visibility = Visibility.Collapsed;
            BtnAction.Content = "Analizar";
            BtnAction.IsEnabled = true;
            TxtStatus.Text = "Análisis cancelado.";
        }
        catch (Exception ex)
        {
            _phase = Phase.Idle;
            TxtHead.Text = "No se pudo analizar";
            TxtSel.Text = ex.Message;
            BarScan.Visibility = Visibility.Collapsed;
            BtnAction.Content = "Reintentar";
            BtnAction.IsEnabled = true;
            TxtStatus.Text = "Error: " + ex.Message;
            AppLog.Error(ex, "analisis limpieza profunda");
        }
    }

    private void Apply(DeepScanUpdate u)
    {
        BarScan.Value = u.Percent;
        if (u.Phase != DeepPhase.Scan) return;
        if (u.TotalBytes > 0)
            TxtHead.Text = $"Un total de {Compacto(u.TotalBytes)} elementos detectados";
        if (u.Current.Length > 0)
            TxtStatus.Text = "Examinando: " + u.Current;
    }

    // ======================= Arbol =======================

    private void ConstruirArbol(List<DeepCatResult> cats)
    {
        PanelArbol.Children.Clear();
        _nodos.Clear();

        for (int i = 0; i < cats.Count; i++)
        {
            var cat = cats[i];
            PanelArbol.Children.Add(FilaCategoria(cat));
            if (i < cats.Count - 1)
                PanelArbol.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 4, 0, 4),
                    Background = (Brush)FindResource("B_BorderSoft"),
                });
        }
    }

    private UIElement FilaCategoria(DeepCatResult cat)
    {
        var hijos = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        var etiqueta = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
        };

        var nodo = new NodoUi { Cat = cat, Etiqueta = etiqueta };
        var cabecera = FilaBase(cat.Nombre, cat.TieneAlgo ? null : "Sin elementos", 0, 12.5, true,
            etiqueta, out CheckBox chk, out Path flecha);
        nodo.Chk = chk;
        nodo.Contenedor = hijos;
        _nodos.Add(nodo);

        chk.IsChecked = cat.Incluida;
        chk.IsEnabled = cat.TieneAlgo;
        chk.Click += (_, _) =>
        {
            cat.Incluida = chk.IsChecked == true;
            SetHijos(cat, cat.Incluida);
            Refrescar();
        };

        cabecera.MouseLeftButtonUp += (_, e) =>
        {
            bool abrir = hijos.Visibility != Visibility.Visible;
            hijos.Visibility = abrir ? Visibility.Visible : Visibility.Collapsed;
            flecha.RenderTransform = new RotateTransform(abrir ? 90 : 0);
            e.Handled = true;
        };

        // hijos: subcategorias o items directos
        foreach (var sub in cat.Subs) hijos.Children.Add(FilaSub(sub));
        foreach (var item in cat.Items) hijos.Children.Add(FilaItem(item, 1));
        hijos.Visibility = hijos.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hijos.Visibility == Visibility.Visible) flecha.RenderTransform = new RotateTransform(90);

        var contenedor = new StackPanel();
        contenedor.Children.Add(cabecera);
        contenedor.Children.Add(hijos);
        return contenedor;
    }

    private void SetHijos(DeepCatResult cat, bool incluir)
    {
        foreach (var sub in cat.Subs)
        {
            sub.Incluida = incluir;
            foreach (var item in sub.Items) item.Incluida = incluir;
        }
        foreach (var item in cat.Items) item.Incluida = incluir;
        // sincronizar casillas visibles
        foreach (var n in _nodos)
        {
            if (n.Sub is not null && cat.Subs.Contains(n.Sub)) n.Chk!.IsChecked = incluir;
            if (n.Item is not null && cat.Todos.Contains(n.Item)) n.Chk!.IsChecked = incluir;
        }
    }

    private UIElement FilaSub(DeepSubCat sub)
    {
        var hijos = new StackPanel { Margin = new Thickness(0, 1, 0, 0) };
        var etiqueta = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
        };
        var nodo = new NodoUi { Sub = sub, Etiqueta = etiqueta };
        var cabecera = FilaBase(sub.Nombre, sub.HayAlgo ? null : "Sin elementos", 1, 12, true,
            etiqueta, out CheckBox chk, out Path flecha);
        nodo.Chk = chk;
        nodo.Contenedor = hijos;
        _nodos.Add(nodo);

        chk.IsChecked = sub.Incluida;
        chk.IsEnabled = sub.HayAlgo;
        chk.Click += (_, _) =>
        {
            sub.Incluida = chk.IsChecked == true;
            foreach (var item in sub.Items) item.Incluida = sub.Incluida;
            foreach (var n in _nodos)
                if (n.Item is not null && sub.Items.Contains(n.Item))
                    n.Chk!.IsChecked = sub.Incluida;
            Refrescar();
        };

        cabecera.MouseLeftButtonUp += (_, e) =>
        {
            bool abrir = hijos.Visibility != Visibility.Visible;
            hijos.Visibility = abrir ? Visibility.Visible : Visibility.Collapsed;
            flecha.RenderTransform = new RotateTransform(abrir ? 90 : 0);
            e.Handled = true;
        };

        foreach (var item in sub.Items) hijos.Children.Add(FilaItem(item, 2));
        hijos.Visibility = Visibility.Visible;
        flecha.RenderTransform = new RotateTransform(90);

        var contenedor = new StackPanel();
        contenedor.Children.Add(cabecera);
        contenedor.Children.Add(hijos);
        return contenedor;
    }

    private UIElement FilaItem(DeepItem item, int nivel)
    {
        var etiqueta = new TextBlock
        {
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
        };
        var nodo = new NodoUi { Item = item, Etiqueta = etiqueta };
        var cabecera = FilaBase(item.Nombre, item.Nota, nivel, 11.5, false,
            etiqueta, out CheckBox chk, out Path flecha, item.Icono);
        nodo.Chk = chk;
        _nodos.Add(nodo);

        chk.IsChecked = item.Incluida;
        chk.IsEnabled = item.TieneAlgo;
        chk.Click += (_, _) =>
        {
            item.Incluida = chk.IsChecked == true;
            Refrescar();
        };

        flecha.Visibility = Visibility.Collapsed;

        cabecera.ToolTip = item.Files.Count > 0
            ? $"{item.Files.Count} archivos" + (item.Registro > 0 ? $" y {item.Registro} entradas de registro" : "")
            : null;

        return cabecera;
    }

    /// Fila base: casilla + icono (opcional) + nombre + hueco para la etiqueta + flecha.
    private Border FilaBase(string nombre, string? nota, int nivel, double tamFuente, bool conFlecha,
        TextBlock etiqueta, out CheckBox chk, out Path flecha, string? icono = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // casilla
        if (icono is not null) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // etiqueta
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // flecha

        chk = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Focusable = false,
        };
        grid.Children.Add(chk);

        int colNombre = 1;
        if (icono is not null)
        {
            var ico = new Path
            {
                Data = (Geometry)FindResource(icono),
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            ico.SetResourceReference(Shape.StrokeProperty, "B_Sub");
            Grid.SetColumn(ico, 1);
            colNombre = 2;
            grid.Children.Add(ico);
        }

        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock
        {
            Text = nombre,
            FontSize = tamFuente,
            FontWeight = nivel == 0 ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
        sp.Children.Add(t);
        if (!string.IsNullOrEmpty(nota))
        {
            var n = new TextBlock { Text = nota, FontSize = 9.5 };
            n.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
            sp.Children.Add(n);
        }
        Grid.SetColumn(sp, colNombre);
        grid.Children.Add(sp);

        Grid.SetColumn(etiqueta, colNombre + 1);
        grid.Children.Add(etiqueta);

        flecha = new Path
        {
            Data = (Geometry)FindResource("I.ChevronRight"),
            Width = 9,
            Height = 9,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Margin = new Thickness(2, 0, 0, 0),
            RenderTransform = new RotateTransform(0),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        flecha.SetResourceReference(Shape.StrokeProperty, "B_Sub");
        Grid.SetColumn(flecha, colNombre + 2);
        if (!conFlecha) flecha.Visibility = Visibility.Collapsed;
        grid.Children.Add(flecha);

        var contenedor = new Border
        {
            Child = grid,
            Background = Brushes.Transparent,
            Cursor = conFlecha ? Cursors.Hand : Cursors.Arrow,
            Padding = new Thickness(2 + nivel * 20, 8, 2, 7),
            CornerRadius = new CornerRadius(5),
        };
        return contenedor;
    }

    // ======================= Estado / totales =======================

    private void Refrescar()
    {
        if (_results is null) return;

        var (bytes, files, regs) = DeepCleanService.TotalesSeleccionados(_results);
        TxtSel.Text = $"Seleccionado: {Compacto(bytes)}";
        BtnAction.Content = bytes > 0 || regs > 0 ? "Continuar" : "Nada marcado";
        if (_phase == Phase.Scanned) BtnAction.IsEnabled = bytes > 0 || regs > 0;

        foreach (var n in _nodos)
        {
            if (n.Etiqueta is null) continue;
            if (n.Cat is not null)
            {
                long sel = n.Cat.BytesSel, tot = n.Cat.Bytes;
                n.Etiqueta.Text = n.Cat.TieneAlgo ? $"{Compacto(sel)}/{Compacto(tot)}" : "";
                n.Etiqueta.SetResourceReference(TextBlock.ForegroundProperty, sel > 0 ? "B_Link" : "B_Muted");
                if (n.Contenedor is not null) n.Contenedor.Opacity = n.Cat.Incluida ? 1 : 0.45;
            }
            else if (n.Sub is not null)
            {
                long sel = n.Sub.BytesSel, tot = n.Sub.Bytes;
                n.Etiqueta.Text = n.Sub.HayAlgo ? (sel != tot ? $"{Compacto(sel)}/{Compacto(tot)}" : Compacto(tot)) : "";
                n.Etiqueta.SetResourceReference(TextBlock.ForegroundProperty, sel > 0 ? "B_Sub" : "B_Muted");
            }
            else if (n.Item is not null)
            {
                bool visible = n.Item.TieneAlgo;
                long bytesItem = n.Item.Incluida ? n.Item.Bytes : 0;
                n.Etiqueta.Text = visible ? Compacto(bytesItem) : "";
                n.Etiqueta.SetResourceReference(TextBlock.ForegroundProperty,
                    n.Item.Incluida ? "B_Sub" : "B_Muted");
                if (n.Chk is not null) n.Chk.IsChecked = n.Item.Incluida;
            }
        }

        if (files == 0 && regs == 0 && _phase == Phase.Scanned)
            TxtStatus.Text = "No hay nada marcado: destilda o marca alguna categoría.";
    }

    /// "111,7 MB" -> "111.7MB" (formato compacto de PC Manager, para que quepa).
    private static string Compacto(long bytes)
    {
        if (bytes <= 0) return "0KB";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb < 10 ? $"{kb:F1}KB" : $"{kb:F0}KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb < 10 ? $"{mb:F1}MB" : $"{mb:F0}MB";
        return $"{mb / 1024.0:F1}GB";
    }

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

    // ======================= Limpieza =======================

    private async Task CleanAsync()
    {
        if (_results is null) return;
        var (total, files, regs) = DeepCleanService.TotalesSeleccionados(_results);
        if (total <= 0 && regs <= 0) return;

        string detalle = $"Eliminar {files} archivos ({Compacto(total)})"
            + (regs > 0 ? $" y {regs} entradas de registro" : "") + "?\n\n" +
            "Lo que esté destildado se conserva. Los datos de navegadores y documentos no se tocan.";
        if (MessageBox.Show(detalle, "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        _phase = Phase.Cleaning;
        BtnAction.Content = "Cancelar";
        BarScan.Value = 0;
        BarScan.Visibility = Visibility.Visible;
        TxtSel.Text = "Limpiando...";
        TxtStatus.Text = "Eliminando los elementos marcados...";
        var pr = new Progress<DeepScanUpdate>(ApplyClean);

        using var cts = new CancellationTokenSource();
        _cleanCts = cts;
        try
        {
            var cats = _results;
            var (cleaned, bytes, cleanedRegs) = await Task.Run(
                () => DeepCleanService.Clean(cats, pr, cts.Token), cts.Token);

            _phase = Phase.Cleaned;
            BarScan.Visibility = Visibility.Collapsed;
            TxtHead.Text = $"Se liberaron {Compacto(bytes)}";
            TxtSel.Text = $"{cleaned} archivos eliminados"
                          + (cleanedRegs > 0 ? $" · {cleanedRegs} de registro" : "");
            BtnAction.Content = "Analizar otra vez";
            BtnAction.IsEnabled = true;
            PanelArbol.IsEnabled = false;
            PanelArbol.Opacity = 0.45;
            TxtStatus.Text = $"Limpieza completada a las {DateTime.Now:HH:mm}.";
            _status($"Limpieza profunda: {Compacto(bytes)} liberados.");
        }
        catch (OperationCanceledException)
        {
            _phase = Phase.Scanned;
            BarScan.Visibility = Visibility.Collapsed;
            TxtSel.Text = "Limpieza cancelada.";
            BtnAction.Content = "Continuar";
            BtnAction.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _phase = Phase.Scanned;
            BarScan.Visibility = Visibility.Collapsed;
            TxtSel.Text = "Error: " + ex.Message;
            BtnAction.Content = "Continuar";
            BtnAction.IsEnabled = true;
            AppLog.Error(ex, "limpieza profunda");
        }
        finally { _cleanCts = null; }
    }

    private void ApplyClean(DeepScanUpdate u)
    {
        BarScan.Value = u.Percent;
        TxtStatus.Text = u.State == CatState.Cleaning
            ? "Limpiando: " + DeepCleanService.CatNames[Math.Max(0, Math.Min(u.CatIndex, DeepCleanService.CatNames.Length - 1))]
            : "Limpiando...";
    }
}
