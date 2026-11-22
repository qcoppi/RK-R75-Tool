using System.Windows;
using System.Windows.Media;
using R75Tool.App.Views;

namespace R75Tool.App;

public partial class MainWindow
{
    private bool _toTray;

    public MainWindow()
    {
        InitializeComponent();

        // Los eventos llegan desde hilos de fondo: Dispatcher.BeginInvoke los pasa al hilo de
        // la UI, que es el único que puede tocar controles en WPF.
        App.Keyboard.ConnectionChanged += OnConnectionChanged;
        App.Keyboard.LightingChanged += OnLightingChanged;
        App.Keyboard.KeymapChanged += OnKeymapChanged;

        Loaded += (_, _) =>
        {
            UpdateHeader();
            RootNavigation.Navigate(typeof(HomePage));
        };

        // Al pulsar la X la ventana se cierra de verdad (se libera su memoria). Según los
        // ajustes, la app sigue en la bandeja o se termina.
        Closing += (_, _) => _toTray = App.ShouldHideInsteadOfClose();
        Closed += (_, _) =>
        {
            // Darse de baja es imprescindible: mientras App.Keyboard tenga apuntados estos
            // métodos, la ventana (y toda su memoria) seguiría viva aunque esté cerrada.
            App.Keyboard.ConnectionChanged -= OnConnectionChanged;
            App.Keyboard.LightingChanged -= OnLightingChanged;
            App.Keyboard.KeymapChanged -= OnKeymapChanged;
            App.OnMainWindowClosed(_toTray);
        };
    }

    private void OnConnectionChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(UpdateHeader);
    private void OnLightingChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(UpdateLighting);
    private void OnKeymapChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(RefreshPreview);

    /// <summary>Para que otras páginas puedan saltar a una sección (p. ej. Inicio -> Iluminación).</summary>
    public void Navigate(Type page) => RootNavigation.Navigate(page);

    /// <summary>
    /// El dibujo de la cabecera. La página de asignación de teclas lo usa para editar
    /// (lo pone en modo interactivo y le cambia la capa que muestra).
    /// </summary>
    public Controls.KeyboardView Preview => KeyboardPreview;

    /// <summary>Capa que muestra el dibujo. null = la capa 0 (lo normal).</summary>
    public ushort[]? PreviewLayer
    {
        get => _previewLayer;
        set { _previewLayer = value; RefreshPreview(); }
    }
    private ushort[]? _previewLayer;

    public void RefreshPreview() =>
        KeyboardPreview.Render(App.Keyboard.Definition, _previewLayer ?? App.Keyboard.BaseLayer);

    private void UpdateHeader()
    {
        var kb = App.Keyboard;
        var def = kb.Definition;
        RefreshPreview();
        UpdateLighting();

        if (kb.IsConnected && kb.Info is { } info)
        {
            KeyboardName.Text = $"Royal Kludge {info.ProductName.Replace(" QMK", "")}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F));
            StatusText.Text = "Conectado";
            StatusDetail.Text = $"{def.Name} · VIA {info.ProtocolVersion} · {info.LayerCount} capas";
        }
        else
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0xA4));
            StatusText.Text = "Desconectado";
            StatusDetail.Text = "Conéctalo por cable y cierra el software de RK.";
        }
    }

    private void UpdateLighting() =>
        KeyboardPreview.SetLighting(App.Keyboard.IsConnected ? App.Keyboard.Lighting : null, App.Keyboard.Definition);
}
