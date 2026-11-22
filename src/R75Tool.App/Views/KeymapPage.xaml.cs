using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using R75Tool.Core;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace R75Tool.App.Views;

public partial class KeymapPage : Page
{
    private MainWindow? _window;
    private int _layer;
    private ushort[]? _keys;        // capa que se está editando: _keys[fila * cols + col]
    private KeyDef? _selected;

    /// <summary>Cambios hechos en esta sesión, para poder deshacerlos.</summary>
    private readonly Stack<(int Layer, KeyDef Key, ushort OldCode)> _undo = new();

    private KeyboardDefinition Def => App.Keyboard.Definition;

    public KeymapPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            // Tomamos prestado el dibujo de la cabecera y lo hacemos clicable
            _window = Window.GetWindow(this) as MainWindow;
            if (_window != null)
            {
                _window.Preview.Interactive = true;
                _window.Preview.KeyClicked += OnKeyClicked;
            }
            App.Keyboard.ConnectionChanged += OnConnectionChanged;
            await SetupAsync();
        };
        Unloaded += (_, _) =>
        {
            // ...y lo devolvemos como estaba al salir de la página
            if (_window != null)
            {
                _window.Preview.KeyClicked -= OnKeyClicked;
                _window.Preview.Interactive = false;
                _window.PreviewLayer = null;
            }
            App.Keyboard.ConnectionChanged -= OnConnectionChanged;
        };
    }

    private void OnConnectionChanged(object? sender, EventArgs e) => Dispatcher.Invoke(async () => await SetupAsync());

    private async Task SetupAsync()
    {
        bool connected = App.Keyboard.IsConnected && App.Keyboard.Info != null;
        ContentPanel.IsEnabled = connected;
        if (!connected)
        {
            ShowMessage("Teclado desconectado", "Conecta el teclado para cambiar sus teclas.", InfoBarSeverity.Warning);
            return;
        }
        if (StatusInfo.Severity == InfoBarSeverity.Warning) StatusInfo.IsOpen = false;

        BuildLayerButtons(App.Keyboard.Info!.LayerCount);
        Picker.Build(App.Keyboard.Info!.LayerCount, App.Keyboard.Info!.MacroCount);
        await LoadLayerAsync(_layer);
    }

    // ===================================================================== capas

    private void BuildLayerButtons(int count) =>
        LayerButtons.Fill(LayerPanel, count, _layer, async layer => await LoadLayerAsync(layer));

    private async Task LoadLayerAsync(int layer)
    {
        try
        {
            _layer = layer;
            _keys = await App.Keyboard.RunAsync(kb => kb.GetLayer(layer, Def.Rows, Def.Cols));
            if (_window != null) _window.PreviewLayer = _keys;
            UpdateSelectionInfo();
        }
        catch (Exception ex)
        {
            ShowMessage("No se ha podido leer la capa", ex.Message, InfoBarSeverity.Error);
        }
    }

    // ===================================================================== selección

    private void OnKeyClicked(object? sender, KeyDef key)
    {
        _selected = key;
        UpdateSelectionInfo();
    }

    private void UpdateSelectionInfo()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        if (_selected is not { } key || _keys == null)
        {
            SelectedLabel.Text = "";
            SelectedTitle.Text = "Ninguna tecla seleccionada";
            SelectedDetail.Text = "Haz clic en una tecla del dibujo de arriba.";
            return;
        }

        ushort code = _keys[key.Row * Def.Cols + key.Col];
        SelectedLabel.Text = KeycodeNames.GetButtonText(code, Def.CustomKeycodes);
        SelectedTitle.Text = $"{KeycodeNames.GetDescription(code)}";
        SelectedDetail.Text = $"Capa {_layer} · fila {key.Row}, columna {key.Col} · código 0x{code:X4}";
    }

    // ===================================================================== selector

    private async void Picker_KeycodeChosen(object? sender, ushort code) => await AssignAsync(code);

    private async void ManualButton_Click(object sender, RoutedEventArgs e)
    {
        string text = ManualCode.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (!ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
        {
            ShowMessage("Código no válido", "Escribe un número hexadecimal de 4 cifras, p. ej. 0x0004.", InfoBarSeverity.Error);
            return;
        }
        await AssignAsync(code);
    }

    // ===================================================================== escribir

    private async Task AssignAsync(ushort code)
    {
        if (_selected is not { } key || _keys == null)
        {
            ShowMessage("Elige una tecla", "Primero haz clic en la tecla que quieres cambiar en el dibujo de arriba.", InfoBarSeverity.Informational);
            return;
        }

        ushort old = _keys[key.Row * Def.Cols + key.Col];
        if (old == code) return;

        // Dos avisos de seguridad: quitar una tecla de capa (p. ej. Fn) y poner una tecla peligrosa
        if (KeycodeNames.IsLayerKey(old) && !await ConfirmAsync("Esta tecla cambia de capa",
                $"Ahora mismo es «{KeycodeNames.GetLabel(old)}»: {KeycodeNames.GetDescription(old)}.\n\n" +
                "Si la cambias, puede que dejes de poder acceder a esa capa desde el teclado " +
                "(siempre podrás arreglarlo desde esta app). ¿Cambiarla?"))
            return;
        if (KeycodeNames.IsDangerous(code) && !await ConfirmAsync("Tecla peligrosa",
                $"«{KeycodeNames.GetLabel(code)}»: {KeycodeNames.GetDescription(code)}\n\n¿Asignarla de todas formas?"))
            return;

        if (await WriteKeyAsync(_layer, key, code))
            _undo.Push((_layer, key, old));
        UpdateSelectionInfo();
    }

    private async Task<bool> WriteKeyAsync(int layer, KeyDef key, ushort code)
    {
        try
        {
            await App.Keyboard.RunAsync(kb => kb.SetKeycode(layer, key.Row, key.Col, code));
            if (layer == _layer && _keys != null) _keys[key.Row * Def.Cols + key.Col] = code;
            App.Keyboard.NotifyKeyChanged(layer, key.Row, key.Col, code); // redibuja la cabecera
            if (StatusInfo.Severity != InfoBarSeverity.Warning) StatusInfo.IsOpen = false;
            return true;
        }
        catch (Exception ex)
        {
            ShowMessage("No se ha podido cambiar la tecla", ex.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var (layer, key, old) = _undo.Pop();
        if (layer != _layer) LayerButtons.Select(LayerPanel, layer); // volvemos a la capa donde estaba el cambio
        await WriteKeyAsync(layer, key, old);
        _selected = key;
        _window?.Preview.Select(key);
        UpdateSelectionInfo();
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Restaurar el keymap de fábrica",
                "Todas las capas volverán a la distribución original del firmware y se perderán tus cambios de teclas. " +
                "La iluminación no se toca. ¿Continuar?"))
            return;
        try
        {
            await App.Keyboard.RunAsync(kb => kb.ResetKeymap());
            _undo.Clear();
            await App.Keyboard.ReloadBaseLayerAsync();
            await LoadLayerAsync(_layer);
            ShowMessage("Keymap restaurado", "El teclado vuelve a tener sus teclas de fábrica.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("No se ha podido restaurar", ex.Message, InfoBarSeverity.Error);
        }
    }

    // ===================================================================== utilidades

    private static async Task<bool> ConfirmAsync(string title, string text)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = "Sí, continuar",
            CloseButtonText = "Cancelar",
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        StatusInfo.Title = title;
        StatusInfo.Message = message;
        StatusInfo.Severity = severity;
        StatusInfo.IsOpen = true;
    }
}
