using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using R75Tool.Core;

namespace R75Tool.App.Views;

public partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();

        // Nos suscribimos al mostrarse la página y nos damos de baja al salir;
        // si no, cada página creada seguiría recibiendo eventos para siempre.
        Loaded += (_, _) =>
        {
            App.Keyboard.ConnectionChanged += OnKeyboardChanged;
            App.Keyboard.LightingChanged += OnKeyboardChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            App.Keyboard.ConnectionChanged -= OnKeyboardChanged;
            App.Keyboard.LightingChanged -= OnKeyboardChanged;
        };
    }

    private void OnKeyboardChanged(object? sender, EventArgs e) => Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        var kb = App.Keyboard;
        bool connected = kb.IsConnected && kb.Info != null;

        DisconnectedInfo.IsOpen = !connected;
        LightingCard.IsEnabled = connected;
        StateBadge.Text = connected ? "Conectado" : "Desconectado";
        StateBadge.Foreground = new SolidColorBrush(connected ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0xFF, 0x99, 0xA4));

        if (!connected)
        {
            StateText.Text = "Esperando a que se conecte el teclado…";
            IdsText.Text = ProtocolText.Text = LayersText.Text = DefinitionText.Text = LightingText.Text = "—";
            LightingSwatch.Background = Brushes.Transparent;
            return;
        }

        var info = kb.Info!;
        StateText.Text = $"{info.ProductName} listo por cable (raw HID)";
        IdsText.Text = $"{info.VendorId:X4} / {info.ProductId:X4}";
        ProtocolText.Text = info.ProtocolVersion.ToString();
        LayersText.Text = info.LayerCount.ToString();
        DefinitionText.Text = $"{kb.Definition.Name} · {kb.Definition.Keys.Count} teclas";
        LayoutText.Text = $"{KeycodeNames.Layout.Name} (según el idioma de entrada de Windows)";

        if (kb.Lighting is { } l)
        {
            string effect = kb.Definition.Effects.FirstOrDefault(e => e.Id == l.Effect)?.Name ?? $"Efecto {l.Effect}";
            LightingText.Text = $"{effect} · brillo {l.Brightness * 100 / 255}% · velocidad {l.Speed * 100 / 255}%";
            var (r, g, b) = RgbMatrix.HsvToRgb(l.Hue, l.Saturation, 255);
            LightingSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }

    private void LightingCard_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.Navigate(typeof(LightingPage));
}
