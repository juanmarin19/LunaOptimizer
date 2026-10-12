using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

/// <summary>
/// Apps instaladas + desinstalar (estilo PC Manager / Geek / BCUninstaller).
/// Lista lo que hay en el registro, filtra y corre el desinstalador registrado
/// (msiexec /x GUID /qn para MSI, UninstallString para el resto). Tras un
/// exit code 0 borra la entrada del registro si quedo huerfana.
/// </summary>
public partial class AppsView : UserControl
{
    private readonly Action<string>? _status;
    private List<InstalledApp> _all = new();

    public AppsView(Action<string>? status = null)
    {
        InitializeComponent();
        _status = status;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        TxtEmpty.Text = "Leyendo apps instaladas...";
        TxtEmpty.Visibility = Visibility.Visible;
        _ = Task.Run(() =>
        {
            try
            {
                var apps = AppUninstallerService.GetInstalledApps();
                Dispatcher.Invoke(() =>
                {
                    _all = apps;
                    ApplyFilter();
                    TxtEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (apps.Count == 0) TxtEmpty.Text = "No se encontraron apps.";
                });
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Listando apps instaladas");
                Dispatcher.Invoke(() =>
                {
                    TxtEmpty.Text = "No se pudieron leer las apps instaladas.";
                    TxtEmpty.Visibility = Visibility.Visible;
                });
            }
        });
    }

    private void ApplyFilter()
    {
        string f = (TxtFilter?.Text ?? "").Trim();
        var items = string.IsNullOrWhiteSpace(f)
            ? _all
            : _all.Where(a => a.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                           || a.Publisher.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        List.ItemsSource = items;
        TxtCount.Text = $"{items.Count} apps";
    }

    // debounce: el filtro corre sobre la lista ya cargada en memoria (_all),
    // no re-lee el registro; no hace falta timer.
    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not InstalledApp app) return;

        var confirm = MessageBox.Show(
            $"Desinstalar {app.Name}?\n\n" +
            (app.IsMsi ? "Se ejecutara el desinstalador de Windows (msiexec).\n"
                       : "Se ejecutara el desinstalador del programa.\n") +
            "Si el desinstalador muestra ventanas, siguelas hasta terminar.",
            "Desinstalar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        btn.IsEnabled = false;
        TxtStatus.Text = $"Desinstalando {app.Name}...";

        var (code, err) = await Task.Run(() => AppUninstallerService.Uninstall(app));

        // 0 = OK; 3010/1641 = OK pero pide reiniciar (Windows lo interpreta como exito).
        bool ok = code is 0 or 3010 or 1641;
        if (ok)
        {
            // borrar entrada huerfana si quedo
            bool regBorrada = await Task.Run(() => AppUninstallerService.RemoveRegistryEntry(app));
            string avisoReinicio = code is 3010 or 1641 ? " (reinicia para terminar)" : "";
            TxtStatus.Text = $"{app.Name} desinstalado{avisoReinicio}." + (regBorrada ? "" : " (entrada del registro conservada)");
            _status?.Invoke($"Desinstalado: {app.Name}.");
            Refresh();
        }
        else
        {
            TxtStatus.Text = $"No se pudo desinstalar {app.Name}. {err}".Trim();
            MessageBox.Show($"El desinstalador de {app.Name} devolvio un error.\n\n" +
                $"Codigo: {code}\n{err}\n\n" +
                "Puede que requiera ejecutarse con permisos o de forma interactiva.",
                "Desinstalar", MessageBoxButton.OK, MessageBoxImage.Warning);
            btn.IsEnabled = true;
        }
    }
}
