using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using R75Tool.Core;

namespace R75Tool.App.Controls;

/// <summary>Edita un RgbState. No habla con el teclado: solo avisa con StateChanged.</summary>
public partial class LightingEditor : UserControl
{
    private bool _updating;

    public LightingEditor()
    {
        InitializeComponent();
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 12; i++)
        {
            var (r, g, b) = RgbMatrix.HsvToRgb((byte)Math.Min(255, i * 255 / 12), 255, 255);
            rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), i / 12.0));
        }
        HueBar.Background = rainbow;
    }

    public event EventHandler? StateChanged;

    /// <summary>Lista de efectos (sale del JSON del teclado).</summary>
    public IReadOnlyList<EffectOption> Effects
    {
        set => EffectCombo.ItemsSource = value;
    }

    public RgbState State
    {
        get => new(
            Brightness: (byte)BrightnessSlider.Value,
            Effect: (byte)((EffectCombo.SelectedItem as EffectOption)?.Id ?? 0),
            Speed: (byte)SpeedSlider.Value,
            Hue: (byte)HueSlider.Value,
            Saturation: (byte)SatSlider.Value);
        set
        {
            // Rellenar los controles sin lanzar StateChanged
            _updating = true;
            EffectCombo.SelectedItem = (EffectCombo.ItemsSource as IEnumerable<EffectOption>)?.FirstOrDefault(e => e.Id == value.Effect);
            BrightnessSlider.Value = value.Brightness;
            SpeedSlider.Value = value.Speed;
            HueSlider.Value = value.Hue;
            SatSlider.Value = value.Saturation;
            _updating = false;
            UpdatePreview();
        }
    }

    private void Any_Changed(object sender, EventArgs e)
    {
        if (Preview == null) return; // durante InitializeComponent
        UpdatePreview();
        if (!_updating) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePreview()
    {
        var s = State;
        var (r, g, b) = RgbMatrix.HsvToRgb(s.Hue, s.Saturation, 255);
        Preview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        Preview.Opacity = s.Effect == 0 ? 0.2 : 0.35 + 0.65 * s.Brightness / 255.0;
    }
}
