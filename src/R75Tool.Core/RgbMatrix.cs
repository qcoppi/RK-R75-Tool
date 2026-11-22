namespace R75Tool.Core;

/// <summary>Estado de la iluminación tal como lo guarda QMK (todo en 0-255).</summary>
/// <param name="Hue">Tono: 0 = rojo, 85 = verde, 170 = azul (la rueda de color en 0-255 en vez de 0-360).</param>
/// <param name="Saturation">Saturación: 0 = blanco, 255 = color puro.</param>
public sealed record RgbState(byte Brightness, byte Effect, byte Speed, byte Hue, byte Saturation);

/// <summary>
/// Control de la iluminación "RGB Matrix" de QMK a través de VIA.
/// Los números salen del JSON del teclado: "content": ["id_qmk_rgb_matrix_brightness", 3, 1]
/// significa canal 3, valor 1.
/// </summary>
public static class RgbMatrix
{
    public const byte Channel = 3;      // id_qmk_rgb_matrix_channel
    public const byte BrightnessId = 1;
    public const byte EffectId = 2;
    public const byte SpeedId = 3;
    public const byte ColorId = 4;      // 2 bytes: tono, saturación

    public static RgbState Read(ViaKeyboard kb)
    {
        var color = kb.GetCustomValue(Channel, ColorId, 2);
        return new RgbState(
            Brightness: kb.GetCustomValue(Channel, BrightnessId, 1)[0],
            Effect: kb.GetCustomValue(Channel, EffectId, 1)[0],
            Speed: kb.GetCustomValue(Channel, SpeedId, 1)[0],
            Hue: color[0],
            Saturation: color[1]);
    }

    public static void SetBrightness(ViaKeyboard kb, byte value) => kb.SetCustomValue(Channel, BrightnessId, value);
    /// <summary>
    /// QMK guarda el brillo en otra escala y al leerlo se redondea (pides 153, lees 151).
    /// Esto ajusta el valor enviado hasta que lo que se lee coincide (o se acerca lo más posible).
    /// </summary>
    public static void SetBrightnessExact(ViaKeyboard kb, byte target)
    {
        int send = target;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            SetBrightness(kb, (byte)Math.Clamp(send, 0, 255));
            int read = kb.GetCustomValue(Channel, BrightnessId, 1)[0];
            if (read == target || send is <= 0 or >= 255) return;
            send += target > read ? 1 : -1;
        }
    }

    public static void SetEffect(ViaKeyboard kb, byte effect) => kb.SetCustomValue(Channel, EffectId, effect);
    public static void SetSpeed(ViaKeyboard kb, byte value) => kb.SetCustomValue(Channel, SpeedId, value);
    public static void SetColor(ViaKeyboard kb, byte hue, byte sat) => kb.SetCustomValue(Channel, ColorId, hue, sat);

    /// <summary>Guarda la iluminación en el teclado para que se mantenga al desconectarlo.</summary>
    public static void Save(ViaKeyboard kb) => kb.SaveCustom(Channel);

    /// <summary>Convierte HSV de QMK (0-255) a RGB normal, para pintar la vista previa.</summary>
    public static (byte R, byte G, byte B) HsvToRgb(byte hue, byte sat, byte val)
    {
        double h = hue / 255.0 * 360.0, s = sat / 255.0, v = val / 255.0;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
