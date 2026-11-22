namespace R75Tool.Core;

/// <summary>Un keycode que se puede asignar a una tecla.</summary>
/// <param name="Label">Texto corto para el dibujo y el botón ("A", "Vol+").</param>
/// <param name="Description">Explicación para el tooltip.</param>
public sealed record KeycodeInfo(ushort Code, string Label, string Description, string Category);

/// <summary>
/// Catálogo de keycodes de QMK (numeración nueva, protocolo VIA 12) y traducción
/// keycode -> etiqueta. Referencia: quantum/keycodes.h en el repositorio de QMK.
/// </summary>
public static class KeycodeNames
{
    public const string Basic = "Básicas";
    public const string Function = "Funciones";
    public const string Navigation = "Navegación";
    public const string Numpad = "Teclado numérico";
    public const string Modifiers = "Modificadores";
    public const string Media = "Multimedia y sistema";
    public const string Layers = "Capas";
    public const string Lighting = "Iluminación";
    public const string Special = "Especiales";
    public const string Macros = "Macros";

    /// <summary>Teclas que ejecutan las macros M0..M(n-1) (QK_MACRO = 0x7700).</summary>
    public static IEnumerable<KeycodeInfo> MacroKeys(int count) =>
        Enumerable.Range(0, Math.Min(count, 128))
                  .Select(i => new KeycodeInfo((ushort)(0x7700 + i), $"M{i}", $"Ejecuta la macro M{i}", Macros));

    /// <summary>Todas las teclas "fijas" del catálogo (las de capa se generan aparte).</summary>
    public static IReadOnlyList<KeycodeInfo> All { get; } = Build();

    private static readonly Dictionary<ushort, KeycodeInfo> ByCode = All.ToDictionary(k => k.Code);

    /// <summary>Teclas que no se ofrecen para asignar porque pueden dejar el teclado inutilizable.</summary>
    public static bool IsDangerous(ushort code) => code is 0x7C00 or 0x7C01 or 0x7C03;

    /// <summary>
    /// Distribución del sistema con la que se muestran las teclas de símbolos.
    /// La app la elige al arrancar según el idioma de entrada de Windows.
    /// </summary>
    public static HostLayout Layout { get; set; } = HostLayout.English;

    /// <summary>Leyendas completas de la tecla (normal, Mayús, AltGr) para dibujarla.</summary>
    public static KeyLegend GetLegend(ushort code, IReadOnlyList<string>? customKeycodes = null) =>
        Layout.Get(code) ?? new KeyLegend(GetLabel(code, customKeycodes));

    public static string GetLabel(ushort code, IReadOnlyList<string>? customKeycodes = null)
    {
        if (Layout.Get(code) is { } legend) return legend.Base;
        if (ByCode.TryGetValue(code, out var info)) return info.Label;

        switch (code)
        {
            case >= 0x0100 and <= 0x1FFF: // modificador + tecla, p. ej. LCTL(KC_C)
                return $"{ModsPrefix((code >> 8) & 0x1F)}{GetLabel((ushort)(code & 0xFF))}";
            case >= 0x4000 and <= 0x4FFF: // LT(capa, tecla)
                return $"LT{(code >> 8) & 0x0F} {GetLabel((ushort)(code & 0xFF))}";
            case >= 0x5200 and <= 0x52DF:
                return LayerLabel(code);
            case >= 0x7700 and <= 0x777F: return $"M{code - 0x7700}"; // macros de VIA
            case >= 0x7E00 and <= 0x7E3F: // keycodes del fabricante
                int i = code - 0x7E00;
                return customKeycodes != null && i < customKeycodes.Count ? customKeycodes[i] : $"RK{i}";
        }
        return $"0x{code:X4}";
    }

    /// <summary>Como GetLabel, pero sin etiquetas vacías (para botones fuera del dibujo).</summary>
    public static string GetButtonText(ushort code, IReadOnlyList<string>? customKeycodes = null) => code switch
    {
        0x2C => "Space",
        0x00 => "—",
        _ => GetLabel(code, customKeycodes),
    };

    public static string GetDescription(ushort code)
    {
        // Teclas de símbolos: la descripción depende de la distribución
        if (code is (>= 0x2D and <= 0x38) or 0x64 && Layout.Get(code) is { } l)
            return $"Tecla {l.Base}" +
                   (l.Shift != null ? $" · con Mayús: {l.Shift}" : "") +
                   (l.AltGr != null ? $" · con AltGr: {l.AltGr}" : "");
        if (ByCode.TryGetValue(code, out var info)) return info.Description;
        return code switch
        {
            >= 0x0100 and <= 0x1FFF => "Modificador + tecla",
            >= 0x4000 and <= 0x4FFF => "Capa al mantener, tecla al pulsar (LT)",
            >= 0x5200 and <= 0x52DF => LayerDescription(code),
            >= 0x7700 and <= 0x777F => "Macro de VIA",
            >= 0x7E00 and <= 0x7E3F => "Función propia de Royal Kludge (desconocida)",
            _ => "Keycode sin nombre en esta app",
        };
    }

    // ------------------------------------------------------------ capas

    /// <summary>Teclas de capa para un teclado con <paramref name="layerCount"/> capas.</summary>
    public static IEnumerable<KeycodeInfo> LayerKeys(int layerCount)
    {
        foreach (var (baseCode, _, _) in LayerKinds)
            for (int l = 0; l < layerCount; l++)
            {
                ushort code = (ushort)(baseCode + l);
                yield return new KeycodeInfo(code, LayerLabel(code), LayerDescription(code), Layers);
            }
    }

    private static readonly (ushort Base, string Prefix, string Text)[] LayerKinds =
    [
        (0x5220, "MO", "Activa la capa {0} mientras la mantienes pulsada"),
        (0x5260, "TG", "Activa o desactiva la capa {0}"),
        (0x5200, "TO", "Cambia a la capa {0} y se queda en ella"),
        (0x5280, "OSL", "Activa la capa {0} solo para la siguiente tecla"),
        (0x52C0, "TT", "Como MO, pero varias pulsaciones la dejan fija"),
        (0x5240, "DF", "Cambia la capa por defecto a la {0}"),
    ];

    private static (ushort Base, string Prefix, string Text)? LayerKind(ushort code) =>
        LayerKinds.Cast<(ushort Base, string Prefix, string Text)?>()
                  .FirstOrDefault(k => code >= k!.Value.Base && code < k.Value.Base + 0x20);

    private static string LayerLabel(ushort code) =>
        LayerKind(code) is { } k ? $"{k.Prefix} {code - k.Base}" : $"0x{code:X4}";

    private static string LayerDescription(ushort code) =>
        LayerKind(code) is { } k ? string.Format(k.Text, code - k.Base) : "Tecla de capa";

    public static bool IsLayerKey(ushort code) => code is >= 0x5200 and <= 0x52DF || code is >= 0x4000 and <= 0x4FFF;

    // ------------------------------------------------------------

    private static string ModsPrefix(int mods)
    {
        // bit 4 = lado derecho; bits 0-3 = Ctrl, Shift, Alt, Win
        var parts = new List<string>();
        if ((mods & 1) != 0) parts.Add("Ctrl");
        if ((mods & 2) != 0) parts.Add("Shift");
        if ((mods & 4) != 0) parts.Add("Alt");
        if ((mods & 8) != 0) parts.Add("Win");
        return string.Join("+", parts) + "+";
    }

    private static List<KeycodeInfo> Build()
    {
        var list = new List<KeycodeInfo>();
        void Add(int code, string label, string desc, string cat) => list.Add(new((ushort)code, label, desc, cat));

        // --- Básicas. Las etiquetas de aquí son las de QMK (inglés US); para las teclas de
        //     símbolos, GetLabel/GetDescription usan antes la distribución elegida (HostLayout).
        for (int i = 0; i < 26; i++) Add(0x04 + i, ((char)('A' + i)).ToString(), $"Letra {(char)('A' + i)}", Basic);
        string[] digits = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];
        for (int i = 0; i < 10; i++) Add(0x1E + i, digits[i], $"Número {digits[i]}", Basic);
        Add(0x28, "Enter", "Intro", Basic);
        Add(0x29, "Esc", "Escape", Basic);
        Add(0x2A, "⌫", "Retroceso (borrar)", Basic);
        Add(0x2B, "Tab", "Tabulador", Basic);
        Add(0x2C, "", "Espacio", Basic);
        Add(0x2D, "-", "Tecla - en US ( ' en español)", Basic);
        Add(0x2E, "=", "Tecla = en US ( ¡ en español)", Basic);
        Add(0x2F, "[", "Tecla [ en US ( ` en español)", Basic);
        Add(0x30, "]", "Tecla ] en US ( + en español)", Basic);
        Add(0x31, "\\", "Tecla \\ en US", Basic);
        Add(0x32, "#", "Tecla ISO junto al Enter ( ç en español)", Basic);
        Add(0x33, ";", "Tecla ; en US ( ñ en español)", Basic);
        Add(0x34, "'", "Tecla ' en US ( ´ en español)", Basic);
        Add(0x35, "`", "Tecla ` en US ( º en español)", Basic);
        Add(0x36, ",", "Coma", Basic);
        Add(0x37, ".", "Punto", Basic);
        Add(0x38, "/", "Tecla / en US ( - en español)", Basic);
        Add(0x64, "<>", "Tecla ISO junto al Shift izquierdo", Basic);
        Add(0x39, "Caps", "Bloq Mayús", Basic);

        // --- Funciones
        for (int i = 0; i < 12; i++) Add(0x3A + i, $"F{i + 1}", $"F{i + 1}", Function);
        for (int i = 0; i < 12; i++) Add(0x68 + i, $"F{i + 13}", $"F{i + 13} (no está en el teclado; útil para atajos)", Function);

        // --- Navegación
        Add(0x52, "↑", "Flecha arriba", Navigation);
        Add(0x51, "↓", "Flecha abajo", Navigation);
        Add(0x50, "←", "Flecha izquierda", Navigation);
        Add(0x4F, "→", "Flecha derecha", Navigation);
        Add(0x4A, "Home", "Inicio", Navigation);
        Add(0x4D, "End", "Fin", Navigation);
        Add(0x4B, "PgUp", "Re Pág", Navigation);
        Add(0x4E, "PgDn", "Av Pág", Navigation);
        Add(0x49, "Ins", "Insertar", Navigation);
        Add(0x4C, "Del", "Suprimir", Navigation);
        Add(0x46, "PrtSc", "Impr Pant", Navigation);
        Add(0x47, "ScrLk", "Bloq Despl", Navigation);
        Add(0x48, "Pause", "Pausa", Navigation);
        Add(0x65, "Menu", "Tecla de menú contextual", Navigation);

        // --- Teclado numérico
        Add(0x53, "NumLk", "Bloq Num", Numpad);
        Add(0x54, "N/", "/ del teclado numérico", Numpad);
        Add(0x55, "N*", "* del teclado numérico", Numpad);
        Add(0x56, "N-", "- del teclado numérico", Numpad);
        Add(0x57, "N+", "+ del teclado numérico", Numpad);
        Add(0x58, "NEnt", "Intro del teclado numérico", Numpad);
        for (int i = 1; i <= 9; i++) Add(0x58 + i, $"N{i}", $"{i} del teclado numérico", Numpad);
        Add(0x62, "N0", "0 del teclado numérico", Numpad);
        Add(0x63, "N.", ". del teclado numérico", Numpad);

        // --- Modificadores
        Add(0xE0, "Ctrl", "Control izquierdo", Modifiers);
        Add(0xE1, "Shift", "Mayúsculas izquierda", Modifiers);
        Add(0xE2, "Alt", "Alt izquierdo", Modifiers);
        Add(0xE3, "Win", "Windows izquierdo", Modifiers);
        Add(0xE4, "RCtrl", "Control derecho", Modifiers);
        Add(0xE5, "Shift", "Mayúsculas derecha", Modifiers);
        Add(0xE6, "AltGr", "Alt Gr (Alt derecho)", Modifiers);
        Add(0xE7, "RWin", "Windows derecho", Modifiers);

        // --- Multimedia y sistema
        Add(0xA8, "Mute", "Silenciar", Media);
        Add(0xAA, "Vol-", "Bajar volumen", Media);
        Add(0xA9, "Vol+", "Subir volumen", Media);
        // Texto en vez de ⏯⏮⏭: esos símbolos salen como emoji y apenas se ven en pequeño
        Add(0xAE, "Play", "Reproducir / pausa", Media);
        Add(0xAD, "Stop", "Detener", Media);
        Add(0xAC, "Prev", "Pista anterior", Media);
        Add(0xAB, "Next", "Pista siguiente", Media);
        Add(0xAF, "Player", "Abrir reproductor multimedia", Media);
        Add(0xB1, "Mail", "Abrir correo", Media);
        Add(0xB2, "Calc", "Abrir calculadora", Media);
        Add(0xB3, "MyPC", "Abrir Este equipo", Media);
        Add(0xB4, "Search", "Buscar", Media);
        Add(0xB5, "WWW", "Abrir navegador (página de inicio)", Media);
        Add(0xB6, "Back", "Navegador: atrás", Media);
        Add(0xB7, "Fwd", "Navegador: adelante", Media);
        Add(0xB9, "Reload", "Navegador: recargar", Media);
        Add(0xBD, "Scr+", "Subir brillo de la pantalla", Media);
        Add(0xBE, "Scr-", "Bajar brillo de la pantalla", Media);
        Add(0xA6, "Sleep", "Suspender el PC", Media);

        // --- Iluminación (atajos de RGB que procesa el propio teclado)
        Add(0x7820, "RGB", "Encender / apagar la iluminación", Lighting);
        Add(0x7821, "Eff+", "Siguiente efecto", Lighting);
        Add(0x7822, "Eff-", "Efecto anterior", Lighting);
        Add(0x7823, "Hue+", "Tono +", Lighting);
        Add(0x7824, "Hue-", "Tono -", Lighting);
        Add(0x7825, "Sat+", "Saturación +", Lighting);
        Add(0x7826, "Sat-", "Saturación -", Lighting);
        Add(0x7827, "Bri+", "Brillo +", Lighting);
        Add(0x7828, "Bri-", "Brillo -", Lighting);
        Add(0x7829, "Spd+", "Velocidad +", Lighting);
        Add(0x782A, "Spd-", "Velocidad -", Lighting);

        // --- Especiales
        Add(0x0000, "", "Ninguna: la tecla no hace nada", Special);
        Add(0x0001, "▽", "Transparente: usa lo que haya en la capa de debajo", Special);
        Add(0x700B, "WinLk", "Bloquear / desbloquear la tecla Windows", Special);
        Add(0x7013, "NKRO", "Activar / desactivar N-key rollover", Special);
        Add(0x7C00, "Boot", "¡Modo bootloader (para flashear firmware)!", Special);
        Add(0x7C01, "Reboot", "Reinicia el teclado", Special);
        Add(0x7C02, "Debug", "Activa el modo debug de QMK", Special);
        Add(0x7C03, "EEClr", "¡Borra la configuración guardada en el teclado!", Special);

        return list;
    }
}
