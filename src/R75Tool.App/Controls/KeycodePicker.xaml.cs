using System.Windows;
using System.Windows.Controls;
using R75Tool.Core;

namespace R75Tool.App.Controls;

/// <summary>Muestra el catálogo de keycodes por categorías y avisa cuando se elige uno.</summary>
public partial class KeycodePicker : UserControl
{
    public KeycodePicker() => InitializeComponent();

    /// <summary>Se lanza al pulsar un keycode.</summary>
    public event EventHandler<ushort>? KeycodeChosen;

    /// <summary>
    /// Rellena las categorías. Hace falta el número de capas para generar MO 0..n, TG 0..n...
    /// Las teclas peligrosas (Boot, borrar EEPROM) no se ofrecen.
    /// </summary>
    /// <param name="macroCount">Cuántas macros tiene el teclado (añade la categoría "Macros").</param>
    /// <param name="filter">Para mostrar solo algunos keycodes (p. ej. las macros solo admiten 0x00-0xFF).</param>
    public void Build(int layerCount, int macroCount = 0, Func<KeycodeInfo, bool>? filter = null)
    {
        var categories = KeycodeNames.All
            .Where(k => !KeycodeNames.IsDangerous(k.Code))
            .Concat(KeycodeNames.LayerKeys(layerCount))
            .Concat(KeycodeNames.MacroKeys(macroCount))
            .Where(k => filter == null || filter(k))
            .GroupBy(k => k.Category)
            .ToList();

        // Si ya había una categoría elegida, la mantenemos (p. ej. al reconectar)
        var previous = (CategoryList.SelectedItem as IGrouping<string, KeycodeInfo>)?.Key;
        CategoryList.ItemsSource = categories;
        CategoryList.SelectedItem = categories.FirstOrDefault(c => c.Key == previous) ?? categories[0];
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        KeycodePanel.Children.Clear();
        if (CategoryList.SelectedItem is not IGrouping<string, KeycodeInfo> group) return;

        foreach (var info in group)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Content = KeycodeNames.GetButtonText(info.Code),
                ToolTip = $"{info.Description}  (0x{info.Code:X4})",
                MinWidth = 56,
                Height = 40,
                Margin = new Thickness(0, 0, 6, 6),
            };
            button.Click += (_, _) => KeycodeChosen?.Invoke(this, info.Code);
            KeycodePanel.Children.Add(button);
        }
    }
}
