using System.Windows;
using System.Windows.Controls;
using R75Tool.App.Audio;
using R75Tool.Core;
using Wpf.Ui.Controls;

namespace R75Tool.App.Views;

public partial class KnobPage : Page
{
    private enum Slot { Left, Right, Press }

    /// <summary>Configuraciones típicas: (nombre, izquierda, derecha, pulsar). null = no tocar.</summary>
    private static readonly (string Name, ushort Left, ushort Right, ushort? Press)[] Presets =
    [
        ("Volumen", 0xAA, 0xA9, 0xA8),             // Vol- / Vol+ / Mute (de fábrica)
        ("Canciones", 0xAC, 0xAB, 0xAE),           // anterior / siguiente / play-pausa
        ("Brillo RGB", 0x7828, 0x7827, 0x7820),    // Bri- / Bri+ / encender-apagar luces
        ("Efecto RGB", 0x7822, 0x7821, 0x7820),    // efecto anterior / siguiente / luces
        ("Brillo pantalla", 0xBE, 0xBD, null),     // solo funciona en algunos portátiles/monitores
        ("Flechas", 0x52, 0x51, null),             // ↑ / ↓ para desplazarse por listas
    ];

    private MainWindow? _window;
    private KeyDef? _knob;          // la tecla de la rueda en la matriz (sale del JSON: "e0")
    private int _layer;
    private KnobActions? _actions;  // lo que hace ahora la rueda en _layer
    private Slot _slot = Slot.Right;

    /// <summary>Estados anteriores, para deshacer (capa + las tres acciones).</summary>
    private readonly Stack<(int Layer, KnobActions Old)> _undo = new();

    public KnobPage()
    {
        InitializeComponent();
        BuildPresets();
        RightSlot.IsChecked = true;

        Loaded += async (_, _) =>
        {
            LoadPerAppOptions();
            VolumeLog.Written += OnVolumeLog;
            _window = Window.GetWindow(this) as MainWindow;
            App.Keyboard.ConnectionChanged += OnConnectionChanged;
            await SetupAsync();
        };
        Unloaded += (_, _) =>
        {
            VolumeLog.Written -= OnVolumeLog;
            App.Keyboard.ConnectionChanged -= OnConnectionChanged;
            _window?.Preview.Select(null);
            if (_window != null) _window.PreviewLayer = null;
        };
    }

    private void OnConnectionChanged(object? sender, EventArgs e) => Dispatcher.Invoke(async () => await SetupAsync());

    private async Task SetupAsync()
    {
        bool connected = App.Keyboard.IsConnected && App.Keyboard.Info != null;
        _knob = Knob.FindKnobKey(App.Keyboard.Definition);
        ContentPanel.IsEnabled = connected && _knob != null;
        if (!connected)
        {
            ShowMessage("Teclado desconectado", "Conecta el teclado para configurar la rueda.", InfoBarSeverity.Warning);
            return;
        }
        if (_knob == null)
        {
            ShowMessage("Sin rueda", "La definición del teclado no tiene ninguna tecla marcada como encoder (e0).", InfoBarSeverity.Warning);
            return;
        }
        if (StatusInfo.Severity == InfoBarSeverity.Warning) StatusInfo.IsOpen = false;

        int layers = App.Keyboard.Info!.LayerCount;
        LayerButtons.Fill(LayerPanel, layers, _layer, async l => await LoadAsync(l));
        Picker.Build(layers);
        await LoadAsync(_layer);
    }

    // ===================================================================== leer

    private async Task LoadAsync(int layer)
    {
        try
        {
            _layer = layer;
            var knob = _knob!;
            _actions = await App.Keyboard.RunAsync(kb => Knob.Read(kb, knob, layer));

            // Mostramos esa capa en el dibujo de arriba y resaltamos la rueda
            if (_window != null)
            {
                _window.PreviewLayer = await App.Keyboard.RunAsync(kb =>
                    kb.GetLayer(layer, App.Keyboard.Definition.Rows, App.Keyboard.Definition.Cols));
                _window.Preview.Select(knob);
            }
            UpdateUi();
        }
        catch (ViaUnsupportedException)
        {
            ContentPanel.IsEnabled = false;
            ShowMessage("La rueda no se puede configurar",
                "El firmware no admite cambiar el giro de la rueda por VIA (le falta ENCODER_MAP_ENABLE).",
                InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowMessage("No se ha podido leer la rueda", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void UpdateUi()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        if (_actions is not { } a) return;
        var custom = App.Keyboard.Definition.CustomKeycodes;
        LeftLabel.Text = KeycodeNames.GetButtonText(a.Left, custom);
        LeftDesc.Text = KeycodeNames.GetDescription(a.Left);
        RightLabel.Text = KeycodeNames.GetButtonText(a.Right, custom);
        RightDesc.Text = KeycodeNames.GetDescription(a.Right);
        PressLabel.Text = KeycodeNames.GetButtonText(a.Press, custom);
        PressDesc.Text = KeycodeNames.GetDescription(a.Press);

        string slotName = _slot switch
        {
            Slot.Left => "girar a la izquierda",
            Slot.Right => "girar a la derecha",
            _ => "pulsar",
        };
        PickerTitle.Text = $"Nueva función para «{slotName}» en la capa {LayerButtons.Name(_layer)}";
    }

    private void Slot_Checked(object sender, RoutedEventArgs e)
    {
        _slot = sender == LeftSlot ? Slot.Left : sender == RightSlot ? Slot.Right : Slot.Press;
        if (IsLoaded) UpdateUi();
    }

    // ===================================================================== escribir

    private async void Picker_KeycodeChosen(object? sender, ushort code)
    {
        if (_actions is not { } a) return;
        var updated = _slot switch
        {
            Slot.Left => a with { Left = code },
            Slot.Right => a with { Right = code },
            _ => a with { Press = code },
        };
        await ApplyAsync(updated, remember: true);
    }

    /// <summary>Envía al teclado solo lo que ha cambiado respecto a _actions.</summary>
    private async Task ApplyAsync(KnobActions updated, bool remember)
    {
        if (_actions is not { } old || _knob is not { } knob || updated == old) return;
        int layer = _layer;
        try
        {
            await App.Keyboard.RunAsync(kb =>
            {
                if (updated.Left != old.Left) Knob.SetLeft(kb, knob, layer, updated.Left);
                if (updated.Right != old.Right) Knob.SetRight(kb, knob, layer, updated.Right);
                if (updated.Press != old.Press) Knob.SetPress(kb, knob, layer, updated.Press);
            });
            if (remember) _undo.Push((layer, old));
            _actions = updated;

            // La pulsación es una tecla normal: actualizamos también el dibujo
            if (updated.Press != old.Press)
            {
                if (_window?.PreviewLayer is { } shown) shown[knob.Row * App.Keyboard.Definition.Cols + knob.Col] = updated.Press;
                App.Keyboard.NotifyKeyChanged(layer, knob.Row, knob.Col, updated.Press);
            }
            if (StatusInfo.Severity != InfoBarSeverity.Warning) StatusInfo.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowMessage("No se ha podido cambiar la rueda", ex.Message, InfoBarSeverity.Error);
        }
        UpdateUi();
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var (layer, old) = _undo.Pop();
        if (layer != _layer)
        {
            LayerButtons.Select(LayerPanel, layer); // esto recarga la capa...
            await LoadAsync(layer);                 // ...pero esperamos a tenerla antes de escribir
        }
        await ApplyAsync(old, remember: false);
    }

    private void BuildPresets()
    {
        foreach (var p in Presets)
        {
            string tip = $"Izquierda: {KeycodeNames.GetDescription(p.Left)}\nDerecha: {KeycodeNames.GetDescription(p.Right)}" +
                         (p.Press is { } press ? $"\nPulsar: {KeycodeNames.GetDescription(press)}" : "\nPulsar: sin cambios");
            var button = new Wpf.Ui.Controls.Button { Content = p.Name, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0) };
            button.Click += async (_, _) =>
            {
                if (_actions is not { } a) return;
                await ApplyAsync(new KnobActions(p.Left, p.Right, p.Press ?? a.Press), remember: true);
            };
            PresetPanel.Children.Add(button);
        }
    }

    // ===================================================================== volumen por aplicación

    private bool _loadingOptions;

    private void LoadPerAppOptions()
    {
        _loadingOptions = true;
        var s = AppSettings.Current;
        PerAppSwitch.IsChecked = s.PerAppVolume;
        StepCombo.SelectedItem = StepCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.VolumeStep.ToString())
                                 ?? StepCombo.Items[1];
        MuteCheck.IsChecked = s.PerAppMute;
        OsdCheck.IsChecked = s.ShowVolumeOsd;
        PerAppOptions.IsEnabled = s.PerAppVolume;
        UpdatePerAppStatus();
        _loadingOptions = false;
    }

    private void PerAppSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions) return;
        var s = AppSettings.Current;
        s.PerAppVolume = PerAppSwitch.IsChecked == true;
        // Sin la bandeja, cerrar la ventana cerraría la app y la rueda dejaría de funcionar
        if (s.PerAppVolume) s.CloseToTray = true;
        PerAppOptions.IsEnabled = s.PerAppVolume;
        AppSettings.Save();
        App.PerAppVolume.ApplySettings(); // instala o quita el hook de teclado
        UpdatePerAppStatus();
    }

    private void UpdatePerAppStatus()
    {
        PerAppStatus.Text = !AppSettings.Current.PerAppVolume
            ? "Desactivado: la rueda controla el volumen general de Windows."
            : !App.PerAppVolume.IsActive
                ? "No se ha podido activar (mira el registro en la carpeta de ajustes)."
                : VolumeLog.Last is { } last && !last.StartsWith("Activado")
                    ? $"Última vez: {last}"
                    : "Activado. Gira la rueda con un juego o programa delante para probarlo.";
    }

    private void OnVolumeLog(string _) => Dispatcher.BeginInvoke(UpdatePerAppStatus);

    private void PerAppOption_Changed(object sender, EventArgs e)
    {
        if (_loadingOptions || StepCombo == null || MuteCheck == null || OsdCheck == null) return;
        var s = AppSettings.Current;
        if (StepCombo.SelectedItem is ComboBoxItem { Tag: string tag }) s.VolumeStep = int.Parse(tag);
        s.PerAppMute = MuteCheck.IsChecked == true;
        s.ShowVolumeOsd = OsdCheck.IsChecked == true;
        AppSettings.Save();
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        StatusInfo.Title = title;
        StatusInfo.Message = message;
        StatusInfo.Severity = severity;
        StatusInfo.IsOpen = true;
    }
}
