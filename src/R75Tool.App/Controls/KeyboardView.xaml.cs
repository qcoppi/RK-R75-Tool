using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using R75Tool.Core;

namespace R75Tool.App.Controls;

/// <summary>
/// Dibuja el teclado a partir de la definición VIA (posiciones del JSON) y, si se le pasa,
/// el keymap leído del teclado (para poner la etiqueta de cada tecla).
/// </summary>
public partial class KeyboardView : UserControl
{
    private const double Unit = 56;   // píxeles por "unidad de tecla"
    private const double Gap = 6;     // separación entre teclas
    private const double CasePadding = 14; // margen de la carcasa

    private static readonly Brush KeyFill = Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 0x2A, 0x2A, 0x2F)));
    private static readonly Brush KeyStroke = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)));

    // Nombres de efectos que mezclan varios colores (para pintar un arcoíris en vez de un color fijo)
    private static readonly string[] MulticolorWords =
        ["Cycle", "Rainbow", "Gradient", "Pinwheel", "Spiral", "Raindrops", "Pixel", "Hue Wave", "Hue Pendulum", "Beacon", "Digital Rain"];

    private static readonly Brush HoverStroke = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush SelectedStroke = Frozen(new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)));
    private static readonly Brush SelectedFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0x1E, 0x3A, 0x4A)));

    private readonly Dictionary<KeyDef, Path> _paths = [];
    private KeyDef? _selected;

    public KeyboardView() => InitializeComponent();

    /// <summary>Si es true, las teclas reaccionan al ratón y lanzan <see cref="KeyClicked"/>.</summary>
    public bool Interactive
    {
        get => _interactive;
        set
        {
            _interactive = value;
            foreach (var p in _paths.Values) p.Cursor = value ? System.Windows.Input.Cursors.Hand : null;
            if (!value) Select(null);
        }
    }
    private bool _interactive;

    /// <summary>Se lanza al hacer clic en una tecla (solo en modo <see cref="Interactive"/>).</summary>
    public event EventHandler<KeyDef>? KeyClicked;

    /// <summary>Marca una tecla como seleccionada (null = ninguna).</summary>
    public void Select(KeyDef? key)
    {
        _selected = key;
        foreach (var (k, p) in _paths) Paint(k, p, hover: false);
    }

    private void Paint(KeyDef key, Path path, bool hover)
    {
        bool selected = key == _selected;
        path.Stroke = selected ? SelectedStroke : hover ? HoverStroke : KeyStroke;
        path.StrokeThickness = selected ? 3 : 1.2;
        path.Fill = selected ? SelectedFill : KeyFill;
    }

    /// <summary>Vuelve a crear todas las teclas.</summary>
    public void Render(KeyboardDefinition def, ushort[]? layer)
    {
        KeyCanvas.Children.Clear();
        _paths.Clear();
        double width = def.Width * Unit + CasePadding * 2;
        double height = def.Height * Unit + CasePadding * 2;
        Root.Width = width;
        Root.Height = height;
        Glow.Margin = new Thickness(-10);

        foreach (var key in def.Keys)
        {
            var legend = layer == null
                ? new KeyLegend("")
                : KeycodeNames.GetLegend(layer[key.Row * def.Cols + key.Col], def.CustomKeycodes);
            AddKey(key, legend);
        }
    }

    private void AddKey(KeyDef key, KeyLegend legend)
    {
        double x = CasePadding + key.X * Unit + Gap / 2;
        double y = CasePadding + key.Y * Unit + Gap / 2;

        // Forma de la tecla, en coordenadas relativas a (x, y)
        Geometry shape;
        if (key.Encoder != null)
        {
            double d = Math.Min(key.W, key.H) * Unit - Gap;
            shape = new EllipseGeometry(new Rect(0, 0, d, d));
        }
        else
        {
            var main = new RectangleGeometry(new Rect(0, 0, key.W * Unit - Gap, key.H * Unit - Gap), 7, 7);
            shape = main;
            if (key.W2 > 0) // Enter ISO: unimos el segundo rectángulo
            {
                var second = new RectangleGeometry(
                    new Rect(key.X2 * Unit, key.Y2 * Unit, key.W2 * Unit - Gap, key.H2 * Unit - Gap), 7, 7);
                shape = new CombinedGeometry(GeometryCombineMode.Union, main, second);
            }
        }

        var path = new Path { Data = shape };
        Canvas.SetLeft(path, x);
        Canvas.SetTop(path, y);
        KeyCanvas.Children.Add(path);
        _paths[key] = path;
        Paint(key, path, hover: false);
        if (_interactive) path.Cursor = System.Windows.Input.Cursors.Hand;

        path.MouseEnter += (_, _) => { if (_interactive) Paint(key, path, hover: true); };
        path.MouseLeave += (_, _) => Paint(key, path, hover: false);
        path.MouseLeftButtonUp += (_, _) =>
        {
            if (!_interactive) return;
            Select(key);
            KeyClicked?.Invoke(this, key);
        };

        if (string.IsNullOrEmpty(legend.Base)) return;

        if (key.Encoder != null)
        {
            // Rueda: etiqueta centrada
            double d = Math.Min(key.W, key.H) * Unit - Gap;
            var text = Label(legend.Base, 10);
            text.Width = d;
            text.TextAlignment = TextAlignment.Center;
            AddLabel(text, x, y + d / 2 - 7);
            return;
        }

        // Como en las teclas reales: en el Enter ISO usamos el rectángulo superior (el ancho)
        double lx = (key.W2 > 0 ? x + Math.Min(0, key.X2 * Unit) : x) + 7;
        double keyHeight = Unit - Gap; // las leyendas van en la primera fila de la tecla

        if (legend.Shift == null && legend.AltGr == null)
        {
            // Una sola leyenda: arriba a la izquierda
            AddLabel(Label(legend.Base, legend.Base.Length > 4 ? 11 : 13), lx, y + 5);
            return;
        }

        // Varias leyendas, igual que la serigrafía:
        //   Mayús arriba a la izquierda, normal abajo a la izquierda, AltGr abajo a la derecha.
        //   Si no hay Mayús (E €, Ç }), la normal va arriba como en el resto de letras.
        if (legend.Shift != null)
        {
            AddLabel(Label(legend.Shift, 12), lx, y + 3);
            AddLabel(Label(legend.Base, 12), lx, y + keyHeight - 21);
        }
        else
        {
            AddLabel(Label(legend.Base, 13), lx, y + 5);
        }
        if (legend.AltGr != null)
        {
            var altGr = Label(legend.AltGr, 11);
            altGr.Width = key.W * Unit - Gap - 14;
            altGr.TextAlignment = TextAlignment.Right;
            altGr.Opacity = 0.7; // en las teclas reales suele ir más discreta
            AddLabel(altGr, lx, y + keyHeight - 20);
        }
    }

    private static TextBlock Label(string text, double size) => new()
    {
        Text = text,
        Foreground = LabelBrush,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        IsHitTestVisible = false, // que el clic llegue a la tecla, no al texto
    };

    private void AddLabel(TextBlock text, double x, double y)
    {
        Canvas.SetLeft(text, x);
        Canvas.SetTop(text, y);
        KeyCanvas.Children.Add(text);
    }

    /// <summary>
    /// Colorea la luz de fondo según la iluminación actual del teclado.
    /// Es una aproximación: no animamos los efectos, solo damos una idea del color.
    /// </summary>
    public void SetLighting(RgbState? state, KeyboardDefinition def)
    {
        if (state == null || state.Effect == 0 || state.Brightness == 0)
        {
            Glow.Opacity = 0;
            return;
        }

        string effectName = def.Effects.FirstOrDefault(e => e.Id == state.Effect)?.Name ?? "";
        bool multicolor = def.EffectsWithoutColor.Contains(state.Effect) ||
                          MulticolorWords.Any(w => effectName.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (multicolor)
        {
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i <= 6; i++)
            {
                var (r, g, b) = RgbMatrix.HsvToRgb((byte)(i * 255 / 6 % 256), 255, 255);
                rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), i / 6.0));
            }
            Glow.Fill = rainbow;
        }
        else
        {
            var (r, g, b) = RgbMatrix.HsvToRgb(state.Hue, state.Saturation, 255);
            Glow.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        // Más brillo = más opacidad (sin llegar a 0 para que siempre se note algo)
        Glow.Opacity = 0.25 + 0.75 * state.Brightness / 255.0;
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
}
