using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace R75Tool.App.Views;

/// <summary>Fila de botones "0 · Base", "1 · Fn"... para elegir capa (la usan varias páginas).</summary>
public static class LayerButtons
{
    // Nombres orientativos de las capas del R75, deducidos de su contenido:
    // la 0/1 son Windows y su Fn, la 2/3 lo mismo con Alt y Win cambiados (modo Mac).
    private static readonly string[] Names = ["Base", "Fn", "Base Mac", "Fn Mac", "Extra"];

    public static string Name(int layer) => layer < Names.Length ? $"{layer} · {Names[layer]}" : $"Capa {layer}";

    /// <summary>Rellena <paramref name="panel"/> con un botón por capa.</summary>
    public static void Fill(Panel panel, int count, int selected, Action<int> onSelected)
    {
        panel.Children.Clear();
        for (int l = 0; l < count; l++)
        {
            int layer = l;
            var button = new RadioButton
            {
                Content = Name(l),
                GroupName = $"layers{panel.GetHashCode()}",
                IsChecked = l == selected,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(12, 6, 12, 6),
                // Un RadioButton es un ToggleButton: le damos ese aspecto (botón que se queda pulsado)
                Style = (Style)panel.FindResource(typeof(ToggleButton)),
            };
            button.Checked += (_, _) => onSelected(layer);
            panel.Children.Add(button);
        }
    }

    /// <summary>Marca una capa desde código (dispara el mismo evento que un clic).</summary>
    public static void Select(Panel panel, int layer)
    {
        if (layer < panel.Children.Count && panel.Children[layer] is RadioButton rb) rb.IsChecked = true;
    }
}
