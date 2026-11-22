using System.Text.Json;

namespace R75Tool.Core;

/// <summary>
/// Copia de seguridad de todo lo que se guarda en el teclado: capas, rueda, macros e iluminación.
/// Se guarda como JSON legible.
/// </summary>
public sealed class KeyboardBackup
{
    public string Keyboard { get; set; } = "";
    public int VendorId { get; set; }
    public int ProductId { get; set; }
    public DateTime Created { get; set; }

    /// <summary>Layers[capa][fila * cols + col] = keycode.</summary>
    public List<ushort[]> Layers { get; set; } = [];

    /// <summary>Encoders[capa][encoder] = [keycode con clockwise=0, keycode con clockwise=1] (valores VIA tal cual).</summary>
    public List<List<ushort[]>> Encoders { get; set; } = [];

    /// <summary>Buffer de macros en Base64 (null si el teclado no tiene macros).</summary>
    public string? Macros { get; set; }

    public RgbState? Lighting { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static KeyboardBackup FromJson(string json) =>
        JsonSerializer.Deserialize<KeyboardBackup>(json, Options) ?? throw new InvalidDataException("Archivo vacío.");

    // ------------------------------------------------------------ leer del teclado

    public static KeyboardBackup Read(ViaKeyboard kb, KeyboardDefinition def, KeyboardInfo info)
    {
        var encoders = def.Keys.Where(k => k.Encoder != null).Select(k => k.Encoder!.Value).Distinct().Order().ToList();
        var backup = new KeyboardBackup
        {
            Keyboard = def.Name,
            VendorId = info.VendorId,
            ProductId = info.ProductId,
            Created = DateTime.Now,
        };
        for (int l = 0; l < info.LayerCount; l++)
        {
            backup.Layers.Add(kb.GetLayer(l, def.Rows, def.Cols));
            backup.Encoders.Add(encoders.Select(e => new[] { kb.GetEncoder(l, e, false), kb.GetEncoder(l, e, true) }).ToList());
        }
        if (info.MacroBufferSize > 0)
            backup.Macros = Convert.ToBase64String(kb.GetMacroBuffer(info.MacroBufferSize));
        backup.Lighting = RgbMatrix.Read(kb);
        return backup;
    }

    // ------------------------------------------------------------ escribir en el teclado

    /// <summary>Comprueba que la copia es de este teclado. Devuelve el problema, o null si vale.</summary>
    public string? Validate(KeyboardDefinition def, KeyboardInfo info)
    {
        if (VendorId != info.VendorId || ProductId != info.ProductId)
            return $"La copia es de otro teclado ({VendorId:X4}:{ProductId:X4}) y este es {info.VendorId:X4}:{info.ProductId:X4}.";
        if (Layers.Count != info.LayerCount || Layers.Any(l => l.Length != def.Rows * def.Cols))
            return "El número de capas o el tamaño del keymap no coincide.";
        if (Macros != null && Convert.FromBase64String(Macros).Length != info.MacroBufferSize)
            return "El tamaño de la memoria de macros no coincide.";
        return null;
    }

    public void Write(ViaKeyboard kb, KeyboardDefinition def, KeyboardInfo info)
    {
        if (Validate(def, info) is { } problem) throw new InvalidDataException(problem);

        var encoders = def.Keys.Where(k => k.Encoder != null).Select(k => k.Encoder!.Value).Distinct().Order().ToList();
        for (int l = 0; l < Layers.Count; l++)
        {
            kb.SetLayer(l, def.Rows, def.Cols, Layers[l]);
            for (int e = 0; e < encoders.Count && l < Encoders.Count && e < Encoders[l].Count; e++)
            {
                kb.SetEncoder(l, encoders[e], false, Encoders[l][e][0]);
                kb.SetEncoder(l, encoders[e], true, Encoders[l][e][1]);
            }
        }
        if (Macros != null)
            kb.SetMacroBuffer(Convert.FromBase64String(Macros), kb.GetMacroBuffer(info.MacroBufferSize));
        if (Lighting is { } rgb)
        {
            RgbMatrix.SetEffect(kb, rgb.Effect);
            RgbMatrix.SetBrightnessExact(kb, rgb.Brightness);
            RgbMatrix.SetSpeed(kb, rgb.Speed);
            RgbMatrix.SetColor(kb, rgb.Hue, rgb.Saturation);
            RgbMatrix.Save(kb);
        }
    }
}
