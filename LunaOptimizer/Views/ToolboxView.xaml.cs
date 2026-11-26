using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class ToolboxView : UserControl
{
    private readonly Action<string> _go;

    private static readonly (string Nombre, string Detalle, string Comando, string Icono)[] Utilidades =
    {
        ("Liberador de espacio", "Quita archivos temporales de Windows", "cleanmgr.exe", "I.Trash"),
        ("Administrador de tareas", "Procesos, inicio y rendimiento", "taskmgr.exe", "I.List"),
        ("Sensor de almacenamiento", "Limpieza automatica de Windows", "ms-settings:storagesense", "I.Pie"),
        ("Windows Update", "Actualizaciones del sistema", "ms-settings:windowsupdate", "I.Rotation"),
        ("Firewall de Windows", "Reglas de red del equipo", "firewall.cpl", "I.Shield"),
        ("Restaurar sistema", "Volver a un punto anterior", "rstrui.exe", "I.Clock"),
        ("Panel de control", "Ajustes clasicos de Windows", "control.exe", "I.Settings"),
        ("Informacion del sistema", "Hardware y version de Windows", "msinfo32.exe", "I.Cpu"),
    };

    public ToolboxView(Action<string> go)
    {
        InitializeComponent();
        _go = go;
        ConstruirUtilidades();
    }

    private void ConstruirUtilidades()
    {
        foreach (var (nombre, detalle, comando, icono) in Utilidades)
            ListaUtilidades.Children.Add(Fila(nombre, detalle, comando, icono));
    }

    private UIElement Fila(string nombre, string detalle, string comando, string icono)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var path = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(icono),
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "B_Sub");
        grid.Children.Add(path);

        var sp = new StackPanel();
        var t1 = new TextBlock { Text = nombre, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        t1.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
        sp.Children.Add(t1);
        var t2 = new TextBlock { Text = detalle, FontSize = 10 };
        t2.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
        sp.Children.Add(t2);
        Grid.SetColumn(sp, 1);
        grid.Children.Add(sp);

        var abrir = new Button
        {
            Content = "Abrir",
            Style = (Style)FindResource("LinkBtn"),
            Tag = comando,
            VerticalAlignment = VerticalAlignment.Center,
        };
        abrir.Click += Abrir_Click;
        Grid.SetColumn(abrir, 2);
        grid.Children.Add(abrir);

        var clic = new Border
        {
            Child = grid,
            Background = Brushes.Transparent,
            Padding = new Thickness(2),
            Cursor = Cursors.Hand,
        };
        clic.MouseLeftButtonUp += (_, _) => Ejecutar(comando);
        return clic;
    }

    private void Abrir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string comando }) Ejecutar(comando);
    }

    private static void Ejecutar(string comando)
    {
        try { Process.Start(new ProcessStartInfo(comando) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Error(ex, "abrir utilidad " + comando); }
    }

    private void GoGames(object sender, MouseButtonEventArgs e) => _go("games");
    private void GoHealth(object sender, MouseButtonEventArgs e) => _go("health");
    private void GoStartup(object sender, MouseButtonEventArgs e) => _go("startup");
    private void GoDeep(object sender, MouseButtonEventArgs e) => _go("deep");
}
