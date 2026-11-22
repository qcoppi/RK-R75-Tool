namespace R75Tool.Core;

/// <summary>Las tres acciones de la rueda en una capa.</summary>
public sealed record KnobActions(ushort Left, ushort Right, ushort Press);

/// <summary>
/// La rueda del R75 = encoder 0 (para girar) + la tecla de la matriz donde está (para pulsar).
/// Ambas cosas salen del JSON: la tecla con "e0" en su etiqueta.
/// </summary>
public static class Knob
{
    /// <summary>
    /// En el R75 el indicador "clockwise" de VIA está invertido respecto al giro físico:
    /// de fábrica, clockwise = 0 es Vol+ y clockwise = 1 es Vol-, y girando a la derecha
    /// sube el volumen (comprobado en un R75 ISO, PID E483).
    /// (Probablemente los pines del encoder están cruzados en la placa.)
    /// Si en otro teclado sale al revés, basta con cambiar esta constante.
    /// </summary>
    public const bool RightTurnIsViaClockwise = false;

    public static KeyDef? FindKnobKey(KeyboardDefinition def, int encoder = 0) =>
        def.Keys.FirstOrDefault(k => k.Encoder == encoder);

    public static KnobActions Read(ViaKeyboard kb, KeyDef knobKey, int layer) => new(
        Left: kb.GetEncoder(layer, knobKey.Encoder!.Value, !RightTurnIsViaClockwise),
        Right: kb.GetEncoder(layer, knobKey.Encoder!.Value, RightTurnIsViaClockwise),
        Press: kb.GetKeycode(layer, knobKey.Row, knobKey.Col));

    public static void SetLeft(ViaKeyboard kb, KeyDef knobKey, int layer, ushort code) =>
        kb.SetEncoder(layer, knobKey.Encoder!.Value, !RightTurnIsViaClockwise, code);

    public static void SetRight(ViaKeyboard kb, KeyDef knobKey, int layer, ushort code) =>
        kb.SetEncoder(layer, knobKey.Encoder!.Value, RightTurnIsViaClockwise, code);

    public static void SetPress(ViaKeyboard kb, KeyDef knobKey, int layer, ushort code) =>
        kb.SetKeycode(layer, knobKey.Row, knobKey.Col, code);
}
