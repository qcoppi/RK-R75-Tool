using System.Globalization;

namespace R75Tool.Core;

/// <summary>
/// Lo que pone en la tecla física: carácter normal, con Mayús y con AltGr.
/// Shift/AltGr son null si no hay que dibujarlos.
/// </summary>
public sealed record KeyLegend(string Base, string? Shift = null, string? AltGr = null);

/// <summary>
/// Distribución de teclado del sistema operativo.
///
/// El teclado solo envía keycodes (p. ej. 0x33); es WINDOWS quien decide qué carácter es,
/// según la distribución elegida: 0x33 es ";" en inglés (US) y "Ñ" en español.
/// Esta clase traduce keycode -> lo que pone la tecla en cada distribución.
/// Las teclas que no están en la tabla (F1, Esc, flechas...) son iguales en todas.
/// </summary>
public sealed class HostLayout
{
    public required string Name { get; init; }
    public required IReadOnlyDictionary<ushort, KeyLegend> Legends { get; init; }

    /// <summary>
    /// Teclas muertas: no escriben nada al pulsarlas, sino que esperan a la siguiente
    /// (´ + a = á). Hacen falta para escribir tildes en las macros.
    /// </summary>
    public IReadOnlySet<string> DeadKeys { get; init; } = new HashSet<string>();

    public KeyLegend? Get(ushort code) => Legends.TryGetValue(code, out var l) ? l : null;

    /// <summary>Modificadores como bits: bit i = keycode 0xE0 + i (Ctrl, Shift, Alt, Win, RCtrl, RShift, AltGr, RWin).</summary>
    public const byte ShiftBit = 1 << 1, AltGrBit = 1 << 6;

    /// <summary>
    /// Qué tecla y modificadores hay que pulsar para escribir <paramref name="c"/> con esta
    /// distribución (solo teclas de la tabla). null si no se puede con una sola pulsación.
    /// </summary>
    public (byte Key, byte Mods)? FindChar(string c)
    {
        foreach (var (code, l) in Legends)
        {
            // En la tabla las letras van en mayúscula (Ñ, Ç, E): minúscula = sin Mayús
            if (l.Base.Length == 1 && char.IsLetter(l.Base[0]) && l.Shift == null)
            {
                if (c == l.Base.ToLowerInvariant()) return ((byte)code, 0);
                if (c == l.Base) return ((byte)code, ShiftBit);
            }
            else if (c == l.Base) return ((byte)code, 0);
            if (c == l.Shift) return ((byte)code, ShiftBit);
            if (c == l.AltGr) return ((byte)code, AltGrBit);
        }
        return null;
    }

    // ------------------------------------------------------------ distribuciones

    /// <summary>Inglés (Estados Unidos), la que usa QMK para nombrar los keycodes.</summary>
    public static HostLayout English { get; } = new()
    {
        Name = "Inglés (EE. UU.)",
        Legends = Build(new()
        {
            [0x35] = new("`", "~"),
            [0x1E] = new("1", "!"), [0x1F] = new("2", "@"), [0x20] = new("3", "#"), [0x21] = new("4", "$"),
            [0x22] = new("5", "%"), [0x23] = new("6", "^"), [0x24] = new("7", "&"), [0x25] = new("8", "*"),
            [0x26] = new("9", "("), [0x27] = new("0", ")"),
            [0x2D] = new("-", "_"), [0x2E] = new("=", "+"),
            [0x2F] = new("[", "{"), [0x30] = new("]", "}"), [0x31] = new("\\", "|"), [0x32] = new("#", "~"),
            [0x33] = new(";", ":"), [0x34] = new("'", "\""),
            [0x36] = new(",", "<"), [0x37] = new(".", ">"), [0x38] = new("/", "?"),
            [0x64] = new("\\", "|"),
        }),
    };

    /// <summary>Español (España), la de un R75 ISO español.</summary>
    public static HostLayout Spanish { get; } = new()
    {
        Name = "Español (España)",
        Legends = Build(new()
        {
            [0x35] = new("º", "ª", "\\"),
            [0x1E] = new("1", "!", "|"), [0x1F] = new("2", "\"", "@"), [0x20] = new("3", "·", "#"),
            [0x21] = new("4", "$", "~"), [0x22] = new("5", "%", "€"), [0x23] = new("6", "&", "¬"),
            [0x24] = new("7", "/"), [0x25] = new("8", "("), [0x26] = new("9", ")"), [0x27] = new("0", "="),
            [0x2D] = new("'", "?"), [0x2E] = new("¡", "¿"),
            [0x08] = new("E", null, "€"),
            [0x2F] = new("`", "^", "["), [0x30] = new("+", "*", "]"),
            [0x33] = new("Ñ"), [0x34] = new("´", "¨", "{"),
            [0x31] = new("Ç", null, "}"), [0x32] = new("Ç", null, "}"), // Windows trata igual las dos
            [0x36] = new(",", ";"), [0x37] = new(".", ":"), [0x38] = new("-", "_"),
            [0x64] = new("<", ">"),
        }),
        DeadKeys = new HashSet<string> { "´", "`", "^", "¨" },
    };

    public static IReadOnlyList<HostLayout> All { get; } = [Spanish, English];

    /// <summary>
    /// Elige la distribución a partir del idioma de entrada de Windows.
    /// Solo es-ES usa la española: el español de Latinoamérica tiene otra distribución.
    /// </summary>
    public static HostLayout ForCulture(CultureInfo? culture) =>
        culture?.Name.Equals("es-ES", StringComparison.OrdinalIgnoreCase) == true ? Spanish : English;

    private static Dictionary<ushort, KeyLegend> Build(Dictionary<int, KeyLegend> d) =>
        d.ToDictionary(kv => (ushort)kv.Key, kv => kv.Value);
}
