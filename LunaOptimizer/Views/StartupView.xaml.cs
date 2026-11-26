using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class StartupView : UserControl
{
    private readonly Action<string> _go;
    private readonly Action<string> _status;
    private bool _cargando;

    public StartupView(Action<string> go, Action<string> status)
    {
        InitializeComponent();
        _go = go;
        _status = status;
        Loaded += (_, _) => Cargar();
    }

    public void OnShown() => Cargar();

    private void BtnBack_Click(object sender, RoutedEventArgs e) => _go("home");

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Cargar();

    private void Cargar()
    {
        if (_cargando) return;
        _cargando = true;
        ListaArranque.Children.Clear();
        TxtVacio.Text = "Cargando...";

        try
        {
            var items = StartupService.GetItems();
            int activas = items.Count(i => i.Habilitado && !i.Propio);
            TxtCuenta.Text = activas.ToString();
            foreach (var item in items)
                ListaArranque.Children.Add(Fila(item));
            TxtVacio.Text = items.Count == 0 ? "No hay aplicaciones de arranque." : "";
        }
        catch (Exception ex)
        {
            TxtVacio.Text = "Error al leer el arranque: " + ex.Message;
            AppLog.Error(ex, "vista arranque");
        }
        finally { _cargando = false; }

        _ = CargarTiempoAsync();
    }

    private async Task CargarTiempoAsync()
    {
        try { TxtBoot.Text = await Task.Run(StartupService.TiempoArranqueTexto); }
        catch { TxtBoot.Text = "—"; }
    }

    private UIElement Fila(StartupItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var nombre = new TextBlock
        {
            Text = item.Nombre,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nombre.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
        sp.Children.Add(nombre);

        var detalle = new TextBlock
        {
            Text = item.Origen + (string.IsNullOrEmpty(item.Exe) ? "" : " · " + item.Exe),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        };
        detalle.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
        detalle.ToolTip = item.Comando;
        sp.Children.Add(detalle);
        grid.Children.Add(sp);

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !item.Propio,
            Tag = item,
            ToolTip = item.Propio ? "LunaOptimizer no se cambia desde aqui" : "Activar o desactivar al iniciar Windows",
        };
        toggle.IsChecked = item.Habilitado;
        toggle.Checked += Toggle_Changed;
        toggle.Unchecked += Toggle_Changed;
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        return grid;
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: StartupItem item } chk) return;
        bool nuevo = chk.IsChecked == true;
        if (nuevo == item.Habilitado) return;

        if (StartupService.SetEnabled(item, nuevo))
        {
            _status(nuevo
                ? $"{item.Nombre} se iniciará con Windows."
                : $"{item.Nombre} ya no se iniciará con Windows.");
            int activas = ListaArranque.Children.OfType<Grid>()
                .Select(g => g.Children.OfType<CheckBox>().FirstOrDefault())
                .Count(c => c?.IsChecked == true);
            TxtCuenta.Text = activas.ToString();
        }
        else
        {
            _status($"No se pudo cambiar {item.Nombre} (permisos).");
            chk.IsChecked = item.Habilitado;
        }
    }
}
