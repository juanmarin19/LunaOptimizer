using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class StorageView : UserControl
{
    private readonly Action<string> _go;
    private readonly Action<string> _status;
    private CancellationTokenSource? _ctsCarpetas;
    private CancellationTokenSource? _ctsGrandes;

    public StorageView(Action<string> go, Action<string> status)
    {
        InitializeComponent();
        _go = go;
        _status = status;
        Loaded += (_, _) => PintarDiscos();
    }

    public void OnShown()
    {
        PintarDiscos();
    }

    private void PintarDiscos()
    {
        ListaDiscos.Children.Clear();
        List<StorageService.Unidad> unidades;
        try { unidades = StorageService.GetUnidades(); }
        catch { return; }

        foreach (var u in unidades)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var cab = new Grid();
            cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nombre = new TextBlock
            {
                Text = u.Nombre,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            nombre.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
            cab.Children.Add(nombre);

            var detalle = new TextBlock { Text = u.Detalle, FontSize = 10.5 };
            detalle.SetResourceReference(TextBlock.ForegroundProperty, "B_Sub");
            Grid.SetColumn(detalle, 1);
            cab.Children.Add(detalle);
            sp.Children.Add(cab);

            var barra = new ProgressBar
            {
                Height = 4,
                Maximum = 100,
                Value = u.UsadoPct,
                Margin = new Thickness(0, 5, 0, 0),
            };
            sp.Children.Add(barra);
            ListaDiscos.Children.Add(sp);
        }

        if (unidades.Count == 0)
        {
            var vacio = new TextBlock { Text = "No se encontraron unidades.", FontSize = 11 };
            vacio.SetResourceReference(TextBlock.ForegroundProperty, "B_Sub");
            ListaDiscos.Children.Add(vacio);
        }
    }

    private async void BtnMedir_Click(object sender, RoutedEventArgs e)
    {
        if (_ctsCarpetas is not null)
        {
            _ctsCarpetas.Cancel();
            return;
        }

        _ctsCarpetas = new CancellationTokenSource();
        BtnMedir.Content = "Cancelar";
        ListaCarpetas.Children.Clear();
        var progreso = new Progress<string>(s => TxtMedirEstado.Text = s);
        try
        {
            var carpetas = await StorageService.MedirCarpetasAsync(progreso, _ctsCarpetas.Token);
            long total = carpetas.Sum(c => c.Bytes);
            TxtMedirEstado.Text = $"{carpetas.Count} carpetas · {TempCleaner.FormatBytes(total)} en total";
            long max = Math.Max(1, carpetas.Count > 0 ? carpetas[0].Bytes : 1);
            foreach (var c in carpetas)
                ListaCarpetas.Children.Add(FilaCarpeta(c, max));
        }
        catch (OperationCanceledException) { TxtMedirEstado.Text = "Medicion cancelada."; }
        catch (Exception ex)
        {
            TxtMedirEstado.Text = "Error: " + ex.Message;
            AppLog.Error(ex, "medir carpetas");
        }
        finally
        {
            _ctsCarpetas.Dispose();
            _ctsCarpetas = null;
            BtnMedir.Content = "Medir";
        }
    }

    private UIElement FilaCarpeta(CarpetaUso c, long max)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sp = new StackPanel();
        var nombre = new TextBlock { Text = c.Nombre, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        nombre.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
        sp.Children.Add(nombre);

        var barra = new ProgressBar
        {
            Height = 3,
            Maximum = 100,
            Value = max <= 0 ? 0 : c.Bytes * 100.0 / max,
            Margin = new Thickness(0, 5, 8, 0),
        };
        sp.Children.Add(barra);
        grid.Children.Add(sp);

        var tam = new TextBlock
        {
            Text = c.Tamano,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        tam.SetResourceReference(TextBlock.ForegroundProperty, "B_Sub");
        Grid.SetColumn(tam, 1);
        grid.Children.Add(tam);

        var boton = new Button
        {
            Content = grid,
            Style = (Style)FindResource("LinkBtn"),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = c.Ruta,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        boton.Click += (_, _) => AbrirCarpeta(c.Ruta);
        boton.ToolTip = c.Ruta;
        return boton;
    }

    private void AbrirCarpeta(string ruta)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ruta + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { _status("No se pudo abrir la carpeta: " + ex.Message); }
    }

    private async void BtnGrandes_Click(object sender, RoutedEventArgs e)
    {
        if (_ctsGrandes is not null)
        {
            _ctsGrandes.Cancel();
            return;
        }

        _ctsGrandes = new CancellationTokenSource();
        BtnGrandes.Content = "Cancelar";
        BarGrandes.Visibility = Visibility.Visible;
        ListaGrandes.Children.Clear();
        var progreso = new Progress<string>(s => TxtGrandesEstado.Text = s);
        try
        {
            var archivos = await StorageService.BuscarGrandesAsync(262144000, progreso, _ctsGrandes.Token);
            TxtGrandesEstado.Text = archivos.Count == 0
                ? "No se encontraron archivos de más de 250 MB."
                : $"{archivos.Count} archivos · {TempCleaner.FormatBytes(archivos.Sum(a => a.Bytes))}";
            foreach (var a in archivos.Take(40))
                ListaGrandes.Children.Add(FilaArchivo(a));
        }
        catch (OperationCanceledException) { TxtGrandesEstado.Text = "Busqueda cancelada."; }
        catch (Exception ex)
        {
            TxtGrandesEstado.Text = "Error: " + ex.Message;
            AppLog.Error(ex, "buscar archivos grandes");
        }
        finally
        {
            _ctsGrandes.Dispose();
            _ctsGrandes = null;
            BtnGrandes.Content = "Buscar";
            BarGrandes.Visibility = Visibility.Collapsed;
        }
    }

    private UIElement FilaArchivo(ArchivoGrande a)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var nombre = new TextBlock
        {
            Text = a.Nombre,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nombre.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
        sp.Children.Add(nombre);

        var sub = new TextBlock
        {
            Text = a.Carpeta,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
        sp.Children.Add(sub);
        grid.Children.Add(sp);

        var tam = new TextBlock
        {
            Text = a.Tamano,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        tam.SetResourceReference(TextBlock.ForegroundProperty, "B_Sub");
        Grid.SetColumn(tam, 1);
        grid.Children.Add(tam);

        var borrar = new Button
        {
            Content = "Papelera",
            Style = (Style)FindResource("LinkBtn"),
            Tag = a,
            VerticalAlignment = VerticalAlignment.Center,
        };
        borrar.Click += BorrarArchivo_Click;
        Grid.SetColumn(borrar, 2);
        grid.Children.Add(borrar);
        return grid;
    }

    private void BorrarArchivo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ArchivoGrande a }) return;
        bool ok = StorageService.EnviarAPapelera(a.Ruta);
        _status(ok ? $"Enviado a la papelera: {a.Nombre}" : "No se pudo enviar a la papelera.");
        if (ok && sender is Button b && b.Parent is Grid g && g.Parent is StackPanel padre)
            padre.Children.Remove(g);
    }

    private void BtnDeep_Click(object sender, RoutedEventArgs e) => _go("deep");
}
