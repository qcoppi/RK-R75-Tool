using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace R75Tool.Core;

/// <summary>Una tecla del dibujo. X, Y, W, H en "unidades de tecla" (1 = una tecla normal).</summary>
/// <param name="Encoder">Si la tecla es una rueda (encoder), su número; si no, null.</param>
public sealed record KeyDef(int Row, int Col, double X, double Y, double W, double H, int? Encoder)
{
    /// <summary>
    /// Segundo rectángulo, para teclas con forma de L como el Enter ISO.
    /// X2/Y2 son relativos a la esquina de la tecla. W2 = 0 significa que no hay.
    /// </summary>
    public double X2 { get; init; }
    public double Y2 { get; init; }
    public double W2 { get; init; }
    public double H2 { get; init; }

    public double Right => Math.Max(X + W, W2 > 0 ? X + X2 + W2 : 0);
    public double Bottom => Math.Max(Y + H, W2 > 0 ? Y + Y2 + H2 : 0);
}

public sealed record EffectOption(string Name, int Id);

/// <summary>
/// Lo que nos interesa de la definición VIA (el JSON del teclado):
/// nombre, matriz, posición de cada tecla, lista de efectos y teclas personalizadas.
/// </summary>
public sealed partial class KeyboardDefinition
{
    public required string Name { get; init; }
    public required int VendorId { get; init; }
    public required int ProductId { get; init; }
    public required int Rows { get; init; }
    public required int Cols { get; init; }
    public required IReadOnlyList<KeyDef> Keys { get; init; }
    public required IReadOnlyList<EffectOption> Effects { get; init; }

    /// <summary>Efectos en los que el color no se usa (sale de la condición "showIf" del JSON).</summary>
    public required IReadOnlySet<int> EffectsWithoutColor { get; init; }

    /// <summary>Nombres cortos de los keycodes personalizados (0x7E00, 0x7E01...).</summary>
    public required IReadOnlyList<string> CustomKeycodes { get; init; }

    public double Width => Keys.Count == 0 ? 0 : Keys.Max(k => k.Right);
    public double Height => Keys.Count == 0 ? 0 : Keys.Max(k => k.Bottom);

    /// <summary>Carga todos los .json de una carpeta (p. ej. la variante ANSI y la ISO).</summary>
    public static List<KeyboardDefinition> LoadAll(string folder) =>
        Directory.GetFiles(folder, "*.json").Order().Select(Load).ToList();

    public static KeyboardDefinition Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var effects = new List<EffectOption>();
        var noColor = new HashSet<int>();
        foreach (var item in FindMenuItems(root))
        {
            if (!item.TryGetProperty("content", out var c) || c.GetArrayLength() == 0) continue;
            string id = c[0].GetString() ?? "";

            if (id == "id_qmk_rgb_matrix_effect")
                foreach (var opt in item.GetProperty("options").EnumerateArray())
                    effects.Add(new EffectOption(opt[0].GetString()!, opt[1].GetInt32()));

            // "showIf": "{id_qmk_rgb_matrix_effect} != 0 && {id_qmk_rgb_matrix_effect} != 24 ..."
            if (id == "id_qmk_rgb_matrix_color" && item.TryGetProperty("showIf", out var showIf))
                foreach (Match m in NotEqualRegex().Matches(showIf.GetString() ?? ""))
                    noColor.Add(int.Parse(m.Groups[1].Value));
        }

        var custom = new List<string>();
        if (root.TryGetProperty("customKeycodes", out var ck))
            foreach (var k in ck.EnumerateArray())
                custom.Add(k.TryGetProperty("shortName", out var s) ? s.GetString()! : k.GetProperty("name").GetString()!);

        var matrix = root.GetProperty("matrix");
        return new KeyboardDefinition
        {
            Name = root.GetProperty("name").GetString() ?? "Teclado",
            VendorId = ParseHex(root.GetProperty("vendorId").GetString()),
            ProductId = ParseHex(root.GetProperty("productId").GetString()),
            Rows = matrix.GetProperty("rows").GetInt32(),
            Cols = matrix.GetProperty("cols").GetInt32(),
            Keys = ParseKle(root.GetProperty("layouts").GetProperty("keymap")),
            Effects = effects,
            EffectsWithoutColor = noColor,
            CustomKeycodes = custom,
        };
    }

    /// <summary>
    /// El layout viene en formato KLE (keyboard-layout-editor.com):
    /// - cada fila es un array;
    /// - un texto es una tecla, y su primera línea es "fila,columna" en la matriz;
    /// - un objeto cambia la siguiente tecla: x/y = saltar hueco, w/h = ancho/alto;
    /// - tras cada tecla avanzamos su ancho, y tras cada fila bajamos 1.
    /// VIA añade "e0" en la línea 10 de la etiqueta para marcar la rueda (encoder 0).
    /// </summary>
    private static List<KeyDef> ParseKle(JsonElement rows)
    {
        var keys = new List<KeyDef>();
        double y = 0;
        foreach (var row in rows.EnumerateArray())
        {
            double x = 0, w = 1, h = 1, x2 = 0, y2 = 0, w2 = 0, h2 = 0;
            foreach (var item in row.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    double Get(string name, double fallback) =>
                        item.TryGetProperty(name, out var p) ? p.GetDouble() : fallback;
                    x += Get("x", 0);
                    y += Get("y", 0);
                    w = Get("w", w);
                    h = Get("h", h);
                    x2 = Get("x2", x2); y2 = Get("y2", y2); w2 = Get("w2", w2); h2 = Get("h2", h2);
                    continue;
                }

                var labels = item.GetString()!.Split('\n');
                var rc = labels[0].Split(',');
                int? encoder = labels.Length > 9 && labels[9].StartsWith('e')
                    ? int.Parse(labels[9][1..], CultureInfo.InvariantCulture)
                    : null;
                keys.Add(new KeyDef(int.Parse(rc[0]), int.Parse(rc[1]), x, y, w, h, encoder)
                {
                    X2 = x2, Y2 = y2, W2 = w2, H2 = h2,
                });

                x += w;
                w = 1; h = 1; x2 = 0; y2 = 0; w2 = 0; h2 = 0;
            }
            y += 1;
        }
        return keys;
    }

    /// <summary>Recorre recursivamente "menus" y devuelve todos los objetos que tienen "type".</summary>
    private static IEnumerable<JsonElement> FindMenuItems(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("type", out _)) yield return el;
            foreach (var p in el.EnumerateObject())
                if (p.Name is "menus" or "content")
                    foreach (var child in FindMenuItems(p.Value)) yield return child;
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
                foreach (var c in FindMenuItems(child)) yield return c;
        }
    }

    private static int ParseHex(string? s) =>
        int.Parse((s ?? "0").Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber);

    [GeneratedRegex(@"!=\s*(\d+)")]
    private static partial Regex NotEqualRegex();
}
