using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using R75Tool.Core;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace R75Tool.App.Views;

public partial class SettingsPage : Page
{
    // "Iniciar con Windows" = un valor en esta clave del registro del usuario (no hace falta ser administrador)
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "R75Tool";

    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loading = true;
            var s = AppSettings.Current;
            AutoLayoutItem.Content = $"Automática (ahora: {HostLayout.ForCulture(CurrentInputLanguage()).Name})";
            LayoutCombo.SelectedItem = LayoutCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Layout)
                                       ?? AutoLayoutItem;
            TraySwitch.IsChecked = s.CloseToTray;
            StartupSwitch.IsChecked = IsStartupEnabled();
            VersionText.Text = $"R75 Tool {typeof(App).Assembly.GetName().Version?.ToString(3)}";
            SettingsPathText.Text = $"Los ajustes y perfiles se guardan en {AppSettings.Folder}";
            _loading = false;
        };
    }

    private static System.Globalization.CultureInfo? CurrentInputLanguage()
    {
        try { return System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage; }
        catch { return null; }
    }

    // ===================================================================== aplicación

    private void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LayoutCombo.SelectedItem is not ComboBoxItem item) return;
        AppSettings.Current.Layout = (string)item.Tag;
        AppSettings.Save();
        App.ApplyLayoutSetting();
        (Window.GetWindow(this) as MainWindow)?.RefreshPreview(); // redibujar el teclado de arriba
    }

    private void TraySwitch_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.CloseToTray = TraySwitch.IsChecked == true;
        AppSettings.Save();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartupSwitch.IsChecked == true)
            {
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" {App.MinimizedArg}");
                // Arrancar en la bandeja no tiene sentido si cerrar la ventana sale de la app
                if (!AppSettings.Current.CloseToTray)
                {
                    AppSettings.Current.CloseToTray = true;
                    TraySwitch.IsChecked = true;
                    AppSettings.Save();
                }
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("No se ha podido cambiar", ex.Message, InfoBarSeverity.Error);
            StartupSwitch.IsChecked = IsStartupEnabled();
        }
    }

    // ===================================================================== copia de seguridad

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckConnected(out var info)) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Copia de R75 Tool (*.json)|*.json",
            FileName = $"R75-copia-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dialog.ShowDialog() != true) return;

        ExportButton.IsEnabled = false;
        try
        {
            var def = App.Keyboard.Definition;
            var backup = await App.Keyboard.RunAsync(kb => KeyboardBackup.Read(kb, def, info));
            await File.WriteAllTextAsync(dialog.FileName, backup.ToJson());
            ShowMessage("Copia guardada", dialog.FileName, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("No se ha podido exportar", ex.Message, InfoBarSeverity.Error);
        }
        finally { ExportButton.IsEnabled = true; }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckConnected(out var info)) return;
        var dialog = new OpenFileDialog { Filter = "Copia de R75 Tool (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var backup = KeyboardBackup.FromJson(await File.ReadAllTextAsync(dialog.FileName));
            var def = App.Keyboard.Definition;
            if (backup.Validate(def, info) is { } problem)
            {
                ShowMessage("Esta copia no vale para este teclado", problem, InfoBarSeverity.Error);
                return;
            }

            var box = new MessageBox
            {
                Title = "Restaurar copia de seguridad",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = $"Se sustituirán las capas, la rueda, las macros y la iluminación del teclado por las de la copia del {backup.Created:g}.\n\n¿Continuar?",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                },
                PrimaryButtonText = "Restaurar",
                CloseButtonText = "Cancelar",
            };
            if (await box.ShowDialogAsync() != MessageBoxResult.Primary) return;

            ImportButton.IsEnabled = false;
            await App.Keyboard.RunAsync(kb => backup.Write(kb, def, info));
            await App.Keyboard.ReloadBaseLayerAsync();
            if (backup.Lighting != null) App.Keyboard.UpdateLighting(backup.Lighting);
            ShowMessage("Copia restaurada", "El teclado tiene ahora la configuración de la copia.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("No se ha podido importar", ex.Message, InfoBarSeverity.Error);
        }
        finally { ImportButton.IsEnabled = true; }
    }

    private bool CheckConnected(out KeyboardInfo info)
    {
        info = App.Keyboard.Info!;
        if (App.Keyboard.IsConnected && App.Keyboard.Info != null) return true;
        ShowMessage("Teclado desconectado", "Conecta el teclado para hacer o restaurar copias.", InfoBarSeverity.Warning);
        return false;
    }

    // ===================================================================== acerca de

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Folder}\"") { UseShellExecute = true });
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        StatusInfo.Title = title;
        StatusInfo.Message = message;
        StatusInfo.Severity = severity;
        StatusInfo.IsOpen = true;
    }
}
