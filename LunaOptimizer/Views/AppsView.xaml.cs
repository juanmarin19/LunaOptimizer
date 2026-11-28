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
        if (_cargando) return;
        _cargando = true;
        TxtEmpty.Text = "Leyendo aplicaciones instaladas...";
        TxtEmpty.Visibility = Visibility.Visible;
        _ = CargarAsync();
    }

    private bool _cargando;

    private async Task CargarAsync()
    {
        try
        {
            // 1) Programas del registro (WIN32). Rapido.
            var apps = await Task.Run(AppUninstallerService.GetInstalledApps);
            Dispatcher.Invoke(() =>
            {
                _all = apps;
                ApplyFilter();
                TxtEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (apps.Count == 0) TxtEmpty.Text = "No se encontraron apps.";
            });

            // 2) Apps de la Microsoft Store (MSIX), como hace PC Manager.
            var store = await AppUninstallerService.GetStoreAppsAsync();
            if (store.Count > 0)
            {
                Dispatcher.Invoke(() =>
                {
                    _all = _all.Concat(store)
                        .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                    ApplyFilter();
                });

                // 3) Tamano de las MSIX: en segundo plano y con tope de tiempo.
                await Task.Run(() => AppUninstallerService.MedirTamanoApps(store));
                Dispatcher.Invoke(ApplyFilter);
            }

            // 4) Iconos, al final: un icono lento no puede demorar la lista.
            var foto = _all.ToList();
            await Task.Run(() => AppUninstallerService.CargarIconos(foto));
            Dispatcher.Invoke(ApplyFilter);
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
        finally { _cargando = false; }
    }

    private void CmbOrden_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (List is null) return;
        ApplyFilter();
    }

    private IEnumerable<InstalledApp> Ordenar(IEnumerable<InstalledApp> apps)
    {
        string modo = (CmbOrden?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Nombre";
        return modo == "Tamano"
            ? apps.OrderByDescending(a => a.EstimatedSizeKB).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            : apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private void ApplyFilter()
    {
        string f = (TxtFilter?.Text ?? "").Trim();
        var filtradas = string.IsNullOrWhiteSpace(f)
            ? _all
            : _all.Where(a => a.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                           || a.Publisher.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        var items = Ordenar(filtradas).ToList();
        List.ItemsSource = items;
        TxtCount.Text = $"{items.Count} apps";
    }

    // debounce: el filtro corre sobre la lista ya cargada en memoria (_all),
    // no re-lee el registro; no hace falta timer.
    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtFilterHint is not null)
            TxtFilterHint.Visibility = string.IsNullOrEmpty(TxtFilter.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

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
        _status?.Invoke($"Desinstalando {app.Name}...");

        var (code, err) = app.EsAppx
            ? await AppUninstallerService.UninstallStoreAsync(app)
            : await Task.Run(() => AppUninstallerService.Uninstall(app));

        // 0 = OK; 3010/1641 = OK pero pide reiniciar (Windows lo interpreta como exito).
        bool ok = code is 0 or 3010 or 1641;
        if (ok)
        {
            // borrar entrada huerfana si quedo
            bool regBorrada = await Task.Run(() => AppUninstallerService.RemoveRegistryEntry(app));
            string avisoReinicio = code is 3010 or 1641 ? " (reinicia para terminar)" : "";
            _status?.Invoke($"{app.Name} desinstalado{avisoReinicio}." + (regBorrada ? "" : " (entrada del registro conservada)"));
            _status?.Invoke($"Desinstalado: {app.Name}.");

            // Como PC Manager: si el desinstalador dejo carpetas, se ofrecen.
            var restos = await Task.Run(() => AppUninstallerService.BuscarRestos(app));
            if (restos.Count > 0)
            {
                var dlg = new RestosWindow(app.Name, restos) { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
                if (dlg.Borrados > 0)
                    _status?.Invoke($"Restos de {app.Name}: {dlg.Borrados} carpetas a la papelera " +
                                    $"({TempCleaner.FormatBytes(dlg.BytesLiberados)}).");
            }

            Refresh();
        }
        else
        {
            _status?.Invoke($"No se pudo desinstalar {app.Name}. {err}".Trim());
            MessageBox.Show($"El desinstalador de {app.Name} devolvió un error.\n\n" +
                $"Código: {code}\n{err}\n\n" +
                "Puede que requiera ejecutarse con permisos o de forma interactiva.",
                "Desinstalar", MessageBoxButton.OK, MessageBoxImage.Warning);
            btn.IsEnabled = true;
        }
    }
}
