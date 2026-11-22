using System.Globalization;
using System.Text;

namespace R75Tool.Core;

// ============================================================================ pasos

/// <summary>Un paso de una macro.</summary>
public abstract record MacroStep;

/// <summary>Escribir un texto.</summary>
public sealed record TextStep(string Text) : MacroStep;

/// <summary>Pulsar una tecla con modificadores (Mods: bit i = keycode 0xE0 + i).</summary>
public sealed record KeyStep(byte Mods, byte Key) : MacroStep;

/// <summary>Mantener pulsada una tecla (hasta un KeyUpStep).</summary>
public sealed record KeyDownStep(byte Key) : MacroStep;

/// <summary>Soltar una tecla mantenida.</summary>
public sealed record KeyUpStep(byte Key) : MacroStep;

/// <summary>Esperar unos milisegundos.</summary>
public sealed record DelayStep(int Milliseconds) : MacroStep;

public sealed class MacroEncodeException(string message) : Exception(message);

// ============================================================================ formato

/// <summary>
/// Convierte macros entre "pasos" y los bytes que guarda QMK.
///
/// Todas las macros van seguidas en un buffer, cada una terminada en 0. Dentro de una macro:
///   - un byte ASCII normal se escribe como texto (QMK lo traduce suponiendo teclado inglés);
///   - 0x01 0x01 tecla = pulsar, 0x01 0x02 tecla = mantener, 0x01 0x03 tecla = soltar;
///   - 0x01 0x04 "150" '|' = esperar 150 ms.
///
/// Como QMK supone teclado inglés, un "@" enviado como ASCII saldría mal en español.
/// Por eso, salvo letras, números y espacios, el texto se guarda como pulsaciones de teclas
/// calculadas con la distribución (HostLayout), incluidas las tildes (´ + a = á).
/// </summary>
public static class MacroCodec
{
    private const byte Prefix = 0x01, Tap = 0x01, Down = 0x02, Up = 0x03, Delay = 0x04;

    private static readonly string[] ModNames = ["Ctrl", "Shift", "Alt", "Win", "RCtrl", "RShift", "AltGr", "RWin"];

    // Marcas de tilde de Unicode -> tecla muerta que las escribe
    private static readonly Dictionary<char, string> Accents = new()
    {
        ['́'] = "´", ['̀'] = "`", ['̂'] = "^", ['̈'] = "¨",
    };

    // ------------------------------------------------------------------ buffer completo

    public static List<List<MacroStep>> DecodeBuffer(byte[] buffer, int count, HostLayout layout)
    {
        var macros = new List<List<MacroStep>>();
        int start = 0;
        for (int m = 0; m < count; m++)
        {
            int end = start < buffer.Length ? Array.IndexOf(buffer, (byte)0, start) : -1;
            if (end < 0) end = buffer.Length;
            macros.Add(start < buffer.Length ? Decode(buffer.AsSpan(start, end - start), layout) : []);
            start = end + 1;
        }
        return macros;
    }

    /// <summary>Junta todas las macros en un buffer del tamaño exacto del teclado (relleno con ceros).</summary>
    public static byte[] EncodeBuffer(IReadOnlyList<IReadOnlyList<MacroStep>> macros, int size, HostLayout layout)
    {
        var bytes = new List<byte>();
        foreach (var macro in macros)
        {
            bytes.AddRange(Encode(macro, layout));
            bytes.Add(0);
        }
        if (bytes.Count > size)
            throw new MacroEncodeException($"Las macros ocupan {bytes.Count} bytes y el teclado solo tiene {size}.");
        var buffer = new byte[size];
        bytes.CopyTo(buffer);
        return buffer;
    }

    // ------------------------------------------------------------------ una macro

    public static byte[] Encode(IReadOnlyList<MacroStep> steps, HostLayout layout)
    {
        var o = new List<byte>();
        foreach (var step in steps)
        {
            switch (step)
            {
                case TextStep t: EncodeText(t.Text, layout, o); break;
                case KeyStep k: EncodeCombo(k.Mods, k.Key, o); break;
                case KeyDownStep d: o.AddRange([Prefix, Down, d.Key]); break;
                case KeyUpStep u: o.AddRange([Prefix, Up, u.Key]); break;
                case DelayStep w:
                    o.AddRange([Prefix, Delay]);
                    o.AddRange(Encoding.ASCII.GetBytes(Math.Clamp(w.Milliseconds, 1, 9999).ToString(CultureInfo.InvariantCulture)));
                    o.Add((byte)'|');
                    break;
            }
        }
        return [.. o];
    }

    private static void EncodeCombo(byte mods, byte key, List<byte> o)
    {
        for (int i = 0; i < 8; i++) if ((mods & (1 << i)) != 0) o.AddRange([Prefix, Down, (byte)(0xE0 + i)]);
        o.AddRange([Prefix, Tap, key]);
        for (int i = 7; i >= 0; i--) if ((mods & (1 << i)) != 0) o.AddRange([Prefix, Up, (byte)(0xE0 + i)]);
    }

    private static void EncodeText(string text, HostLayout layout, List<byte> o)
    {
        bool english = ReferenceEquals(layout, HostLayout.English);
        foreach (var element in EnumerateTextElements(text.Replace("\r\n", "\n")))
        {
            char c0 = element[0];
            // Letras, números, espacio y saltos de línea: ASCII (igual en cualquier distribución).
            // En inglés, cualquier símbolo ASCII también vale tal cual.
            if (element.Length == 1 && c0 < 128 &&
                (char.IsAsciiLetterOrDigit(c0) || c0 is ' ' or '\n' or '\t' || (english && !char.IsControl(c0))))
            {
                o.Add((byte)c0);
                continue;
            }

            // Un carácter que está en la tabla de la distribución (Ñ, @, €...)
            if (layout.FindChar(element) is { } k)
            {
                EncodeCombo(k.Mods, k.Key, o);
                if (layout.DeadKeys.Contains(element)) o.Add((byte)' '); // tecla muerta + espacio = el símbolo solo
                continue;
            }

            // Letra con tilde: se separa en letra + marca (á = a + ´) y se escribe con la tecla muerta
            string decomposed = element.Normalize(NormalizationForm.FormD);
            if (decomposed.Length == 2 && Accents.TryGetValue(decomposed[1], out var dead) &&
                layout.DeadKeys.Contains(dead) && layout.FindChar(dead) is { } dk && char.IsAsciiLetter(decomposed[0]))
            {
                EncodeCombo(dk.Mods, dk.Key, o);
                o.Add((byte)decomposed[0]);
                continue;
            }

            throw new MacroEncodeException($"No se puede escribir «{element}» con la distribución {layout.Name}.");
        }
    }

    private static IEnumerable<string> EnumerateTextElements(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) yield return (string)e.Current;
    }

    // ------------------------------------------------------------------ leer

    private abstract record Ev;
    private sealed record CharEv(char C) : Ev;
    private sealed record KeyEv(byte Type, byte Key) : Ev;
    private sealed record DelayEv(int Ms) : Ev;

    public static List<MacroStep> Decode(ReadOnlySpan<byte> data, HostLayout layout)
    {
        // 1) bytes -> eventos sueltos
        var events = new List<Ev>();
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != Prefix) { events.Add(new CharEv((char)data[i])); continue; }
            if (i + 1 >= data.Length) break;
            byte type = data[i + 1];
            if (type is Tap or Down or Up && i + 2 < data.Length)
            {
                events.Add(new KeyEv(type, data[i + 2]));
                i += 2;
            }
            else if (type == Delay)
            {
                int j = i + 2, ms = 0;
                while (j < data.Length && data[j] != '|') { if (char.IsAsciiDigit((char)data[j])) ms = ms * 10 + data[j] - '0'; j++; }
                events.Add(new DelayEv(ms));
                i = j;
            }
            else i++; // acción desconocida: la saltamos
        }

        // 2) eventos -> pasos, reconociendo los patrones que genera Encode
        var steps = new List<MacroStep>();
        var text = new StringBuilder();
        void FlushText() { if (text.Length > 0) { steps.Add(new TextStep(text.ToString())); text.Clear(); } }
        void AddStep(MacroStep s) { FlushText(); steps.Add(s); }

        for (int i = 0; i < events.Count; i++)
        {
            switch (events[i])
            {
                case CharEv c:
                    text.Append(c.C);
                    break;
                case DelayEv d:
                    AddStep(new DelayStep(d.Ms));
                    break;
                case KeyEv { Type: Up } u:
                    AddStep(new KeyUpStep(u.Key));
                    break;
                case KeyEv k:
                    if (!TryReadCombo(events, ref i, out byte mods, out byte key))
                    {
                        AddStep(new KeyDownStep(k.Key)); // un "mantener" que no sigue el patrón
                        break;
                    }
                    // ¿Es un carácter de la distribución? Entonces lo mostramos como texto.
                    if (ComboToChar(mods, key, layout) is { } ch)
                    {
                        if (layout.DeadKeys.Contains(ch) && i + 1 < events.Count && events[i + 1] is CharEv next)
                        {
                            i++;
                            text.Append(next.C == ' ' ? ch : ComposeAccent(next.C, ch));
                        }
                        else text.Append(ch);
                    }
                    else AddStep(new KeyStep(mods, key));
                    break;
            }
        }
        FlushText();
        return steps;
    }

    /// <summary>Reconoce "mantener mods, pulsar tecla, soltar mods" o un simple "pulsar".</summary>
    private static bool TryReadCombo(List<Ev> ev, ref int i, out byte mods, out byte key)
    {
        mods = 0; key = 0;
        int j = i;
        while (j < ev.Count && ev[j] is KeyEv { Type: Down } d && d.Key is >= 0xE0 and <= 0xE7)
        {
            mods |= (byte)(1 << (d.Key - 0xE0));
            j++;
        }
        if (j >= ev.Count || ev[j] is not KeyEv { Type: Tap } tap) return false;
        key = tap.Key;
        j++;
        byte released = 0;
        while (released != mods && j < ev.Count && ev[j] is KeyEv { Type: Up } u && u.Key is >= 0xE0 and <= 0xE7)
        {
            released |= (byte)(1 << (u.Key - 0xE0));
            j++;
        }
        if (released != mods) return false;
        i = j - 1;
        return true;
    }

    private static string? ComboToChar(byte mods, byte key, HostLayout layout)
    {
        if (layout.Get(key) is not { } l) return null;
        bool letter = l.Base.Length == 1 && char.IsLetter(l.Base[0]) && l.Shift == null;
        return mods switch
        {
            0 => letter ? l.Base.ToLowerInvariant() : l.Base,
            HostLayout.ShiftBit => letter ? l.Base : l.Shift,
            HostLayout.AltGrBit => l.AltGr,
            _ => null,
        };
    }

    private static string ComposeAccent(char letter, string dead)
    {
        char mark = Accents.FirstOrDefault(kv => kv.Value == dead).Key;
        return (letter + mark.ToString()).Normalize(NormalizationForm.FormC);
    }

    // ------------------------------------------------------------------ textos para la interfaz

    public static string ModsText(byte mods) =>
        string.Join(" + ", Enumerable.Range(0, 8).Where(i => (mods & (1 << i)) != 0).Select(i => ModNames[i]));

    public static string Describe(MacroStep step) => step switch
    {
        TextStep t => $"Escribir «{t.Text.Replace("\n", "⏎")}»",
        KeyStep k => k.Mods == 0
            ? $"Pulsar {KeycodeNames.GetButtonText(k.Key)}"
            : $"Pulsar {ModsText(k.Mods)} + {KeycodeNames.GetButtonText(k.Key)}",
        KeyDownStep d => $"Mantener {KeycodeNames.GetButtonText(d.Key)}",
        KeyUpStep u => $"Soltar {KeycodeNames.GetButtonText(u.Key)}",
        DelayStep w => $"Esperar {w.Milliseconds} ms",
        _ => step.ToString() ?? "",
    };

    /// <summary>Resumen corto para la lista de macros.</summary>
    public static string Summary(IReadOnlyList<MacroStep> steps) =>
        steps.Count == 0 ? "Vacía" : string.Join(", ", steps.Take(3).Select(Describe)) + (steps.Count > 3 ? "…" : "");
}
