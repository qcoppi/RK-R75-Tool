// RkDiag - Diagnóstico por consola del Royal Kludge R75 (QMK/VIA)
// Usa la misma librería (R75Tool.Core) que la app WPF, así sirve para probarla sin interfaz.
//
// Uso:
//   RkDiag                      -> detecta el teclado y lee información (no cambia nada)
//   RkDiag brillo 0-255         -> cambia el brillo        (temporal, no se guarda)
//   RkDiag efecto N             -> cambia el efecto        (temporal)
//   RkDiag velocidad 0-255      -> cambia la velocidad     (temporal)
//   RkDiag color TONO SAT       -> cambia el color (0-255) (temporal)

using HidSharp;
using R75Tool.Core;

KeycodeNames.Layout = HostLayout.ForCulture(System.Globalization.CultureInfo.CurrentCulture);
var defs = KeyboardDefinition.LoadAll(Path.Combine(AppContext.BaseDirectory, "Assets"));
var def = defs[0];

// --- 1. Listar dispositivos HID ---
Console.WriteLine("\nDispositivos HID del fabricante:");
foreach (var dev in DeviceList.Local.GetHidDevices().Where(d => d.VendorID == def.VendorId))
{
    string name;
    try { name = dev.GetProductName(); } catch { name = "(sin nombre)"; }
    Console.WriteLine($"  VID={dev.VendorID:X4} PID={dev.ProductID:X4}  {name}{(ViaKeyboard.IsViaInterface(dev) ? "   <-- interfaz VIA" : "")}");
}

// --- 2. Abrir ---
using var kb = ViaKeyboard.Open(def.VendorId);
if (kb == null)
{
    Console.WriteLine("\nNo se ha encontrado (o no se puede abrir) la interfaz VIA.");
    Console.WriteLine("- ¿Está conectado por CABLE (y el interruptor en modo cable)?");
    Console.WriteLine("- ¿Está abierto el software de RK? Ciérralo y vuelve a probar.");
    Pausa();
    return;
}

Console.WriteLine($"\nUsando: {kb.ProductName} (VID={kb.VendorId:X4} PID={kb.ProductId:X4})");

// Elegir la definición (ANSI/ISO) cuyo PID coincide con el teclado conectado
def = defs.FirstOrDefault(d => d.ProductId == kb.ProductId) ?? def;
Console.WriteLine($"Definición: {def.Name}, matriz {def.Rows}x{def.Cols}, {def.Keys.Count} teclas, {def.Effects.Count} efectos");
Console.WriteLine($"Protocolo VIA: {kb.GetProtocolVersion()}");
Console.WriteLine($"Capas: {kb.GetLayerCount()}");

// --- 3. Keymap de la capa 0 dibujado en texto ---
var layer = kb.GetLayer(0, def.Rows, def.Cols);
Console.WriteLine("\nCapa 0:");
foreach (var fila in def.Keys.GroupBy(k => (int)Math.Floor(k.Y)))
    Console.WriteLine("  " + string.Join(" ", fila.Select(k =>
        $"[{KeycodeNames.GetLabel(layer[k.Row * def.Cols + k.Col], def.CustomKeycodes)}]")));

// RkDiag matriz -> toda la matriz en bruto (también las posiciones que el JSON no dibuja)
if (args.Length == 1 && args[0] == "matriz")
{
    Console.WriteLine("\nMatriz completa (fila,col = keycode):");
    for (int r = 0; r < def.Rows; r++)
        Console.WriteLine("  " + string.Join(" ", Enumerable.Range(0, def.Cols).Select(c =>
            $"{r},{c}={KeycodeNames.GetLabel(layer[r * def.Cols + c], def.CustomKeycodes)}" +
            (def.Keys.Any(k => k.Row == r && k.Col == c) ? "" : "*"))));
    Console.WriteLine("  (* = posición que no aparece en el JSON)");
}

// --- 4. Iluminación ---
var rgb = RgbMatrix.Read(kb);
string efecto = def.Effects.FirstOrDefault(e => e.Id == rgb.Effect)?.Name ?? "?";
Console.WriteLine($"\nRGB: efecto {rgb.Effect} ({efecto}), brillo {rgb.Brightness}, velocidad {rgb.Speed}, tono {rgb.Hue}, saturación {rgb.Saturation}");

// --- 5. Cambios (temporales: no se llama a RgbMatrix.Save) ---
if (args.Length >= 2 && byte.TryParse(args[1], out byte v))
{
    switch (args[0])
    {
        case "brillo": RgbMatrix.SetBrightness(kb, v); break;
        case "efecto": RgbMatrix.SetEffect(kb, v); break;
        case "velocidad": RgbMatrix.SetSpeed(kb, v); break;
        case "color" when args.Length >= 3 && byte.TryParse(args[2], out byte sat): RgbMatrix.SetColor(kb, v, sat); break;
        default: Console.WriteLine("Comando no reconocido."); break;
    }
    Console.WriteLine($"Ahora: {RgbMatrix.Read(kb)}");
}

Pausa();

static void Pausa()
{
    if (Console.IsInputRedirected) return; // ejecutado desde otro programa: no esperar
    Console.WriteLine("\nPulsa una tecla para salir...");
    Console.ReadKey();
}
