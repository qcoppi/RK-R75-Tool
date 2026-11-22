using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using R75Tool.Core;
using Wpf.Ui.Controls;

namespace R75Tool.App.Views;

public partial class ProfilesPage : Page
{
    // Los cambios se guardan en disco medio segundo después de dejar de tocar (no en cada píxel del slider)
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loading;

    private static AppSettings Settings => AppSettings.Current;

    /// <summary>null = "Resto de programas" (la iluminación por defecto).</summary>
    private AppProfile? Selected => ProfileList.SelectedIndex > 0 ? Settings.Profiles[ProfileList.SelectedIndex - 1] : null;

    public ProfilesPage()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            AppSettings.Save();
            App.Profiles.Reevaluate();
        };

        Loaded += (_, _) =>
        {
            App.Profiles.Changed += OnProfilesChanged;
            Editor.Effects = App.Keyboard.Definition.Effects;
            EnabledSwitch.IsChecked = Settings.ProfilesEnabled;
            TrayHint.IsOpen = !Settings.CloseToTray;
            BuildList(0);
            UpdateForeground();
        };
        Unloaded += (_, _) =>
        {
            App.Profiles.Changed -= OnProfilesChanged;
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); AppSettings.Save(); } // no perder lo último
        };
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => Dispatcher.Invoke(UpdateForeground);

    private void UpdateForeground()
    {
        var p = App.Profiles;
        string active = p.ActiveProfile?.Name ?? "Resto de programas";
        ForegroundText.Text = p.ForegroundProcess == null
            ? "Cambia a otro programa para ver qué perfil se aplica"
            : $"Último programa en primer plano: {p.ForegroundProcess} → {active}";
    }

    // ===================================================================== lista

    private void BuildList(int select)
    {
        _loading = true;
        ProfileList.Items.Clear();
        ProfileList.Items.Add(new ListBoxItem { Content = "Resto de programas" });
        foreach (var p in Settings.Profiles) ProfileList.Items.Add(new ListBoxItem { Content = ListText(p) });
        _loading = false;
        ProfileList.SelectedIndex = Math.Min(select, ProfileList.Items.Count - 1);
    }

    private static string ListText(AppProfile p) =>
        $"{p.Name}{(string.IsNullOrEmpty(p.ProcessName) ? "" : $" ({p.ProcessName})")}{(p.Enabled ? "" : " · desactivado")}";

    private void RefreshSelectedListItem()
    {
        if (Selected is { } p && ProfileList.SelectedItem is ListBoxItem item) item.Content = ListText(p);
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileList.SelectedIndex < 0) return;
        _loading = true;
        var p = Selected;
        bool isDefault = p == null;
        ProgramFields.Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;
        DefaultHint.Visibility = isDefault ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = !isDefault;

        if (p != null)
        {
            NameBox.Text = p.Name;
            ProcessBox.Text = p.ProcessName;
            ActiveCheck.IsChecked = p.Enabled;
        }
        Editor.State = p?.Lighting ?? Settings.DefaultLighting ?? App.Keyboard.Lighting ?? new RgbState(150, 1, 128, 0, 255);
        _loading = false;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        Settings.Profiles.Add(new AppProfile
        {
            Name = $"Perfil {Settings.Profiles.Count + 1}",
            Lighting = App.Keyboard.Lighting ?? new RgbState(150, 1, 128, 0, 255),
        });
        BuildList(Settings.Profiles.Count);
        ScheduleSave();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        int index = ProfileList.SelectedIndex;
        Settings.Profiles.Remove(p);
        BuildList(index - 1);
        ScheduleSave();
    }

    // ===================================================================== edición

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || Selected is not { } p) return;
        p.Name = NameBox.Text;
        RefreshSelectedListItem();
        ScheduleSave();
    }

    private void ProcessBox_Changed(object sender, EventArgs e)
    {
        if (_loading || Selected is not { } p) return;
        string text = (ProcessBox.SelectedItem as string ?? ProcessBox.Text).Trim();
        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
        if (text == p.ProcessName) return;
        p.ProcessName = text;
        // Si el perfil aún tiene el nombre por defecto, le ponemos el del programa
        if (p.Name.StartsWith("Perfil ") && text.Length > 0) { _loading = true; NameBox.Text = p.Name = text; _loading = false; }
        RefreshSelectedListItem();
        ScheduleSave();
    }

    /// <summary>Al desplegar, ofrecemos los programas que tienen alguna ventana abierta.</summary>
    private void ProcessBox_DropDownOpened(object? sender, EventArgs e)
    {
        string text = ProcessBox.Text;
        ProcessBox.ItemsSource = Process.GetProcesses()
            .Where(pr => { try { return pr.MainWindowHandle != IntPtr.Zero; } catch { return false; } })
            .Select(pr => pr.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ProcessBox.Text = text;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Programas (*.exe)|*.exe", Title = "Elige el programa" };
        if (dialog.ShowDialog() != true) return;
        ProcessBox.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        ProcessBox_Changed(ProcessBox, EventArgs.Empty);
    }

    private void ActiveCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        p.Enabled = ActiveCheck.IsChecked == true;
        RefreshSelectedListItem();
        ScheduleSave();
    }

    private void Editor_StateChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        if (Selected is { } p) p.Lighting = Editor.State;
        else Settings.DefaultLighting = Editor.State;
        ScheduleSave();
    }

    private void CopyCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Keyboard.Lighting is not { } current) return;
        Editor.State = current;
        Editor_StateChanged(this, EventArgs.Empty);
    }

    private async void TryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Keyboard.IsConnected)
        {
            ShowMessage("Teclado desconectado", "Conecta el teclado para probar la iluminación.", InfoBarSeverity.Warning);
            return;
        }
        await App.Profiles.ApplyAsync(Editor.State);
    }

    private void EnabledSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.SetProfilesEnabled(EnabledSwitch.IsChecked == true);
        if (ProfileList.SelectedIndex == 0) ProfileList_SelectionChanged(this, null!); // por si se acaba de rellenar el "por defecto"
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        StatusInfo.Title = title;
        StatusInfo.Message = message;
        StatusInfo.Severity = severity;
        StatusInfo.IsOpen = true;
    }
}
