using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using R75Tool.Core;
using Wpf.Ui.Controls;

namespace R75Tool.App.Views;

public partial class LightingPage : Page
{
    // Mientras rellenamos los controles con los valores del teclado, los eventos ValueChanged
    // también saltan. Esta bandera evita reenviar al teclado lo que acabamos de leer de él.
    private bool _updatingUi;

    private static readonly (string Name, byte Hue, byte Sat)[] Presets =
    [
        ("Rojo", 0, 255), ("Naranja", 21, 255), ("Amarillo", 43, 255), ("Verde", 85, 255),
        ("Cian", 128, 255), ("Azul", 170, 255), ("Morado", 191, 255), ("Rosa", 223, 255), ("Blanco", 0, 0),
    ];

    public LightingPage()
    {
        InitializeComponent();
        EffectCombo.ItemsSource = App.Keyboard.Definition.Effects;
        BuildHueBar();
        BuildPresets();

        Loaded += async (_, _) =>
        {
            App.Keyboard.ConnectionChanged += OnConnectionChanged;
            ShowState(App.Keyboard.Lighting); // primero lo que ya sabemos (instantáneo)...
            await ReloadAsync();              // ...y luego lo confirmamos con el teclado
        };
        Unloaded += (_, _) => App.Keyboard.ConnectionChanged -= OnConnectionChanged;
    }

    private void OnConnectionChanged(object? sender, EventArgs e) => Dispatcher.Invoke(async () =>
    {
        EffectCombo.ItemsSource = App.Keyboard.Definition.Effects;
        ShowState(App.Keyboard.Lighting);
        if (App.Keyboard.IsConnected) await ReloadAsync();
    });

    // ===================================================================== leer

    private async Task ReloadAsync()
    {
        if (!App.Keyboard.IsConnected) { ShowState(null); return; }
        try
        {
            var state = await App.Keyboard.RunAsync(RgbMatrix.Read);
            App.Keyboard.UpdateLighting(state);
            ShowState(state);
        }
        catch (Exception ex)
        {
            ShowMessage("No se ha podido leer la iluminación", ex.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>Pone en los controles el estado recibido (o los desactiva si no hay teclado).</summary>
    private void ShowState(RgbState? s)
    {
        ControlsPanel.IsEnabled = s != null && App.Keyboard.IsConnected;
        if (!ControlsPanel.IsEnabled)
        {
            ShowMessage("Teclado desconectado", "Conecta el teclado para cambiar la iluminación.", InfoBarSeverity.Warning);
            return;
        }
        if (StatusInfo.Severity == InfoBarSeverity.Warning) StatusInfo.IsOpen = false;

        _updatingUi = true;
        EffectCombo.SelectedItem = App.Keyboard.Definition.Effects.FirstOrDefault(e => e.Id == s!.Effect);
        BrightnessSlider.Value = s!.Brightness;
        SpeedSlider.Value = s.Speed;
        HueSlider.Value = s.Hue;
        SatSlider.Value = s.Saturation;
        _updatingUi = false;

        UpdateLabels();
    }

    // ===================================================================== escribir

    /// <summary>Estado según los controles en este momento.</summary>
    private RgbState CurrentState() => new(
        Brightness: (byte)BrightnessSlider.Value,
        Effect: (byte)((EffectCombo.SelectedItem as EffectOption)?.Id ?? 0),
        Speed: (byte)SpeedSlider.Value,
        Hue: (byte)HueSlider.Value,
        Saturation: (byte)SatSlider.Value);

    /// <summary>
    /// Manda un cambio al teclado. Usamos Post (y no RunAsync) porque al arrastrar un slider
    /// llegan muchos valores seguidos: Post se queda solo con el último de cada tipo.
    /// </summary>
    private void Send(string what, Action<ViaKeyboard> command)
    {
        UpdateLabels();
        if (_updatingUi || !IsLoaded) return;
        App.Keyboard.Post(what, command);
        App.Keyboard.UpdateLighting(CurrentState()); // para que el dibujo de arriba cambie ya
    }

    private void EffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EffectCombo.SelectedItem is EffectOption effect)
            Send("effect", kb => RgbMatrix.SetEffect(kb, (byte)effect.Id));
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        byte v = (byte)e.NewValue;
        Send("brightness", kb => RgbMatrix.SetBrightness(kb, v));
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        byte v = (byte)e.NewValue;
        Send("speed", kb => RgbMatrix.SetSpeed(kb, v));
    }

    private void ColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HueSlider == null || SatSlider == null) return; // durante InitializeComponent
        byte h = (byte)HueSlider.Value, s = (byte)SatSlider.Value;
        Send("color", kb => RgbMatrix.SetColor(kb, h, s));
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            // Va a la misma cola que los sliders, así se guarda después del último cambio.
            await App.Keyboard.Post("save", RgbMatrix.Save);
            ShowMessage("Guardado", "La iluminación se mantendrá aunque desconectes el teclado.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("No se ha podido guardar", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    // ===================================================================== aspecto

    private void UpdateLabels()
    {
        if (BrightnessLabel == null || SatBar == null) return; // durante InitializeComponent
        var s = CurrentState();
        BrightnessLabel.Text = $"{s.Brightness * 100 / 255}%";
        SpeedLabel.Text = $"{s.Speed * 100 / 255}%";

        var (r, g, b) = RgbMatrix.HsvToRgb(s.Hue, s.Saturation, 255);
        ColorPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));

        // Barra de saturación: de blanco al color puro del tono elegido
        var (pr, pg, pb) = RgbMatrix.HsvToRgb(s.Hue, 255, 255);
        SatBar.Background = new LinearGradientBrush(Colors.White, Color.FromRgb(pr, pg, pb), 0);

        // Igual que en VIA: la velocidad no se usa con las luces apagadas
        // y algunos efectos ignoran el color (lo dice el "showIf" del JSON).
        var def = App.Keyboard.Definition;
        SpeedCard.IsEnabled = s.Effect != 0;
        bool colorUsed = s.Effect != 0 && !def.EffectsWithoutColor.Contains(s.Effect);
        ColorCard.IsEnabled = colorUsed;
        ColorCard.Opacity = colorUsed ? 1 : 0.5;
        ColorHint.Text = colorUsed
            ? "Tono y saturación (el brillo se ajusta arriba)"
            : "Este efecto no usa el color elegido";
    }

    private void BuildHueBar()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 12; i++)
        {
            var (r, g, b) = RgbMatrix.HsvToRgb((byte)Math.Min(255, i * 255 / 12), 255, 255);
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), i / 12.0));
        }
        HueBar.Background = brush;
    }

    private void BuildPresets()
    {
        foreach (var p in Presets)
        {
            var (r, g, b) = RgbMatrix.HsvToRgb(p.Hue, p.Sat, 255);
            var button = new System.Windows.Controls.Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                ToolTip = p.Name, Cursor = System.Windows.Input.Cursors.Hand,
                // Plantilla mínima: un círculo del color, sin el estilo de botón
                Template = CircleTemplate(Color.FromRgb(r, g, b)),
            };
            button.Click += (_, _) =>
            {
                // Cambiamos los dos sliders a la vez sin enviar dos veces
                _updatingUi = true;
                HueSlider.Value = p.Hue;
                SatSlider.Value = p.Sat;
                _updatingUi = false;
                Send("color", kb => RgbMatrix.SetColor(kb, p.Hue, p.Sat));
            };
            PresetPanel.Children.Add(button);
        }
    }

    private static ControlTemplate CircleTemplate(Color color)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(color));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        return new ControlTemplate(typeof(System.Windows.Controls.Button)) { VisualTree = border };
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        StatusInfo.Title = title;
        StatusInfo.Message = message;
        StatusInfo.Severity = severity;
        StatusInfo.IsOpen = true;
    }
}
