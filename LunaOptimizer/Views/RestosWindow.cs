using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

/// <summary>
/// Dialogo de "restos": lo que dejo el desinstalador y PC Manager tambien ofrece
/// limpiar. Se listan las carpetas con su tamano, se pueden destildar, y lo que
/// se borra va a la PAPELERA (recuperable), nunca definitivo.
/// </summary>
public class RestosWindow : Window
{
    private readonly List<Resto> _restos;
    private readonly List<CheckBox> _checks = new();
    public int Borrados { get; private set; }
    public long BytesLiberados { get; private set; }

    public RestosWindow(string nombreApp, List<Resto> restos)
    {
        _restos = restos;
        Title = "Restos de " + nombreApp;
        Width = 430;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        Background = (Brush)Application.Current.FindResource("B_Card");
        Foreground = (Brush)Application.Current.FindResource("B_Text");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var raiz = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        var titulo = new TextBlock
        {
            Text = $"{nombreApp} se desinstaló, pero quedaron {restos.Count} carpetas",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        titulo.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
        raiz.Children.Add(titulo);

        long total = restos.Sum(r => r.Bytes);
        var sub = new TextBlock
        {
            Text = $"{TempCleaner.FormatBytes(total)} de restos. Se envían a la papelera, así que se pueden recuperar.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10),
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "B_Sub");
        raiz.Children.Add(sub);

        var lista = new StackPanel();
        foreach (var r in restos)
        {
            var fila = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chk = new CheckBox
            {
                IsChecked = r.Seleccionado,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = r,
            };
            fila.Children.Add(chk);
            _checks.Add(chk);

            var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var n = new TextBlock { Text = r.Nombre, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
            n.SetResourceReference(TextBlock.ForegroundProperty, "B_Text");
            textos.Children.Add(n);
            var p = new TextBlock
            {
                Text = r.Ruta,
                FontSize = 9.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            p.SetResourceReference(TextBlock.ForegroundProperty, "B_Muted");
            textos.Children.Add(p);
            Grid.SetColumn(textos, 1);
            fila.Children.Add(textos);

            var tam = new TextBlock
            {
                Text = r.Tamano,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tam.SetResourceReference(TextBlock.ForegroundProperty, "B_Sub");
            Grid.SetColumn(tam, 2);
            fila.Children.Add(tam);

            lista.Children.Add(fila);
        }

        var scroll = new ScrollViewer
        {
            Content = lista,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        raiz.Children.Add(scroll);

        var botones = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var cerrar = new Button
        {
            Content = "Conservar",
            Width = 100,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
        };
        cerrar.SetResourceReference(StyleProperty, "SecondaryBtn");
        cerrar.Click += (_, _) => Close();
        botones.Children.Add(cerrar);

        var borrar = new Button
        {
            Content = "Enviar a la papelera",
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
        };
        borrar.SetResourceReference(StyleProperty, "PrimaryBtn");
        borrar.Click += (_, _) =>
        {
            foreach (var c in _checks)
                if (c.Tag is Resto r) r.Seleccionado = c.IsChecked == true;

            var (n2, bytes) = AppUninstallerService.EliminarRestos(_restos);
            Borrados = n2;
            BytesLiberados = bytes;
            Close();
        };
        botones.Children.Add(borrar);

        raiz.Children.Add(botones);
        Content = raiz;
    }
}
