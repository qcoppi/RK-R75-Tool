using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using R75Tool.Core;
using Wpf.Ui.Controls;
using TextBox = Wpf.Ui.Controls.TextBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using Button = Wpf.Ui.Controls.Button;

namespace R75Tool.App.Views;

public partial class MacrosPage : Page
{
    private static readonly (string Name, byte Bit)[] Modifiers =
        [("Ctrl", 1 << 0), ("Shift", 1 << 1), ("Alt", 1 << 2), ("Win", 1 << 3), ("AltGr", 1 << 6)];

    private List<List<MacroStep>> _macros = [];   // copia de trabajo (lo que ves y editas)
    private byte[] _saved = [];                    // el buffer tal como está en el teclado
    private int _bufferSize;
    private int _current;
    private bool _dirty;

    public MacrosPage()
    {
        InitializeComponent();
        foreach (var (name, bit) in Modifiers)
            ModsPanel.Children.Add(new CheckBox { Content = name, Tag = bit, Margin = new Thickness(0, 0, 12, 0) });

        Loaded += async (_, _) =>
        {
            App.Keyboard.ConnectionChanged += OnConnectionChanged;
            await LoadAsync();
        };
        Unloaded += (_, _) => App.Keyboard.ConnectionChanged -= OnConnectionChanged;
    }

    private void OnConnectionChanged(object? sender, EventArgs e) => Dispatcher.Invoke(async () => await LoadAsync());

    // ===================================================================== leer

    private async Task LoadAsync()
    {
        var info = App.Keyboard.IsConnected ? App.Keyboard.Info : null;
        ContentPanel.IsEnabled = info is { MacroCount: > 0 };
        if (info == null)
        {
            ShowMessage("Teclado desconectado", "Conecta el teclado para editar las macros.", InfoBarSeverity.Warning);
            return;
        }
        if (info.MacroCount == 0)
        {
            ShowMessage("Sin macros", "El firmware de este teclado no admite macros de VIA.", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            _bufferSize = info.MacroBufferSize;
            _saved = await App.Keyboard.RunAsync(kb => kb.GetMacroBuffer(_bufferSize));
            _macros = MacroCodec.DecodeBuffer(_saved, info.MacroCount, KeycodeNames.Layout);
            _dirty = false;
            StatusInfo.IsOpen = false;

            // Las macros solo pueden pulsar keycodes básicos (0x00-0xFF): filtramos el selector
            Picker.Build(info.LayerCount, 0, k => k.Code is > 0x01 and < 0x100);
            BuildList();
            MacroList.SelectedIndex = Math.Min(_current, _macros.Count - 1);
        }
        catch (Exception ex)
        {
            ShowMessage("No se han podido leer las macros", ex.Message, InfoBarSeverity.Error);
        }
    }

    // ===================================================================== lista

    private void BuildList()
    {
        int selected = MacroList.SelectedIndex;
        MacroList.Items.Clear();
        for (int i = 0; i < _macros.Count; i++)
            MacroList.Items.Add(new ListBoxItem { Content = ListText(i) });
        MacroList.SelectedIndex = selected;
    }

    private string ListText(int i) => $"M{i} · {MacroCodec.Summary(_macros[i])}";

    private void MacroList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MacroList.SelectedIndex < 0) return;
        _current = MacroList.SelectedIndex;
        KeyPanel.Visibility = Visibility.Collapsed;
        BuildSteps();
    }

    /// <summary>Llamar tras cualquier cambio: actualiza la lista, el contador y los botones.</summary>
    private void Changed(bool rebuildSteps)
    {
        _dirty = true;
        if (MacroList.Items[_current] is ListBoxItem item) item.Content = ListText(_current);
        if (rebuildSteps) BuildSteps();
        else UpdateStatus();
    }

    // ===================================================================== pasos

    private List<MacroStep> Steps => _macros[_current];

    private void BuildSteps()
    {
        StepsPanel.Children.Clear();
        for (int i = 0; i < Steps.Count; i++) StepsPanel.Children.Add(StepRow(i));
        EmptyText.Visibility = Steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
    }

    private FrameworkElement StepRow(int index)
    {
        var step = Steps[index];
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new SymbolIcon
        {
            Symbol = step switch
            {
                TextStep => SymbolRegular.TextT24,
                DelayStep => SymbolRegular.Timer24,
                KeyDownStep => SymbolRegular.ArrowDown24,
                KeyUpStep => SymbolRegular.ArrowUp24,
                _ => SymbolRegular.Keyboard24,
            },
            FontSize = 20,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(icon);

        FrameworkElement editor = step switch
        {
            TextStep t => TextEditor(index, t),
            DelayStep d => DelayEditor(index, d),
            _ => new TextBlock { Text = MacroCodec.Describe(step), VerticalAlignment = VerticalAlignment.Center },
        };
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(SmallButton(SymbolRegular.ArrowUp24, "Subir", index > 0, () => Move(index, -1)));
        buttons.Children.Add(SmallButton(SymbolRegular.ArrowDown24, "Bajar", index < Steps.Count - 1, () => Move(index, +1)));
        buttons.Children.Add(SmallButton(SymbolRegular.Delete24, "Quitar", true, () => { Steps.RemoveAt(index); Changed(true); }));
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(12, 8, 8, 8),
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    private FrameworkElement TextEditor(int index, TextStep t)
    {
        var box = new TextBox
        {
            Text = t.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "Texto que escribirá la macro",
        };
        // No reconstruimos la fila al escribir (perderíamos el foco): solo actualizamos el paso
        box.TextChanged += (_, _) => { Steps[index] = new TextStep(box.Text); Changed(false); };
        return box;
    }

    private FrameworkElement DelayEditor(int index, DelayStep d)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = "Esperar", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var box = new TextBox { Text = d.Milliseconds.ToString(), Width = 90 };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, out int ms) && ms is >= 1 and <= 9999)
            {
                Steps[index] = new DelayStep(ms);
                Changed(false);
            }
        };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "ms (1-9999)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        return panel;
    }

    private static Button SmallButton(SymbolRegular symbol, string tip, bool enabled, Action onClick)
    {
        var b = new Button
        {
            Icon = new SymbolIcon { Symbol = symbol },
            ToolTip = tip,
            IsEnabled = enabled,
            Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(6),
            Margin = new Thickness(2, 0, 0, 0),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Move(int index, int delta)
    {
        (Steps[index], Steps[index + delta]) = (Steps[index + delta], Steps[index]);
        Changed(true);
    }

    // ===================================================================== añadir

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        Steps.Add(new TextStep(""));
        Changed(true);
        // Ponemos el cursor en el cuadro nuevo
        if (StepsPanel.Children[^1] is Border { Child: Grid g } && g.Children[1] is TextBox box) box.Focus();
    }

    private void AddDelay_Click(object sender, RoutedEventArgs e)
    {
        Steps.Add(new DelayStep(100));
        Changed(true);
    }

    private void AddKey_Click(object sender, RoutedEventArgs e) =>
        KeyPanel.Visibility = KeyPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void Picker_KeycodeChosen(object? sender, ushort code)
    {
        byte mods = 0;
        foreach (var cb in ModsPanel.Children.OfType<CheckBox>())
        {
            if (cb.IsChecked == true) mods |= (byte)cb.Tag;
            cb.IsChecked = false;
        }
        Steps.Add(new KeyStep(mods, (byte)code));
        KeyPanel.Visibility = Visibility.Collapsed;
        Changed(true);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Steps.Clear();
        Changed(true);
    }

    // ===================================================================== guardar

    /// <summary>Codifica todo: devuelve el buffer o null (y explica el error).</summary>
    private byte[]? Encode(out string error)
    {
        error = "";
        try
        {
            return MacroCodec.EncodeBuffer(_macros, _bufferSize, KeycodeNames.Layout);
        }
        catch (MacroEncodeException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void UpdateStatus()
    {
        MacroTitle.Text = $"M{_current}";
        SaveButton.IsEnabled = DiscardButton.IsEnabled = _dirty;

        var buffer = Encode(out string error);
        if (buffer == null)
        {
            UsageText.Text = error;
            UsageText.Foreground = Brushes.IndianRed;
            SaveButton.IsEnabled = false;
            return;
        }
        int used = MacroCodec.Encode(Steps, KeycodeNames.Layout).Length;
        int total = _macros.Sum(m => MacroCodec.Encode(m, KeycodeNames.Layout).Length + 1);
        UsageText.Text = $"Esta macro ocupa {used} bytes · todas juntas {total} de {_bufferSize}" +
                         (_dirty ? " · cambios sin guardar" : "");
        UsageText.ClearValue(ForegroundProperty);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var buffer = Encode(out string error);
        if (buffer == null) { ShowMessage("No se puede guardar", error, InfoBarSeverity.Error); return; }
        SaveButton.IsEnabled = false;
        try
        {
            var previous = _saved;
            await App.Keyboard.RunAsync(kb => kb.SetMacroBuffer(buffer, previous));
            _saved = buffer;
            _dirty = false;
            UpdateStatus();
            ShowMessage("Macros guardadas", "Asígnalas a una tecla en «Asignación de teclas» → Macros.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("No se han podido guardar", ex.Message, InfoBarSeverity.Error);
            UpdateStatus();
        }
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        _macros = MacroCodec.DecodeBuffer(_saved, _macros.Count, KeycodeNames.Layout);
        _dirty = false;
        BuildList();
        BuildSteps();
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        StatusInfo.Title = title;
        StatusInfo.Message = message;
        StatusInfo.Severity = severity;
        StatusInfo.IsOpen = true;
    }
}
