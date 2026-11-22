using HidSharp;

namespace R75Tool.Core;

/// <summary>
/// Comandos del protocolo raw HID de VIA (el primer byte de cada paquete).
/// Lista completa: quantum/via.h en el repositorio de QMK.
/// </summary>
public enum ViaCommand : byte
{
    GetProtocolVersion = 0x01,
    GetKeyboardValue = 0x02,
    SetKeyboardValue = 0x03,
    DynamicKeymapGetKeycode = 0x04,
    DynamicKeymapSetKeycode = 0x05,
    DynamicKeymapReset = 0x06,
    CustomSetValue = 0x07,
    CustomGetValue = 0x08,
    CustomSave = 0x09,
    MacroGetCount = 0x0C,
    MacroGetBufferSize = 0x0D,
    MacroGetBuffer = 0x0E,
    MacroSetBuffer = 0x0F,
    DynamicKeymapGetLayerCount = 0x11,
    DynamicKeymapGetBuffer = 0x12,
    DynamicKeymapSetBuffer = 0x13,
    DynamicKeymapGetEncoder = 0x14,
    DynamicKeymapSetEncoder = 0x15,
    Unhandled = 0xFF,
}

/// <summary>El teclado ha contestado 0xFF: no conoce el comando.</summary>
public sealed class ViaUnsupportedException(ViaCommand cmd)
    : Exception($"El teclado no reconoce el comando 0x{(byte)cmd:X2} ({cmd}).");

/// <summary>
/// Una conexión abierta con la interfaz VIA del teclado.
/// Todo se reduce a <see cref="Send"/>: se escribe un paquete de 32 bytes y se lee la respuesta.
/// Los demás métodos son atajos para comandos concretos.
/// </summary>
public sealed class ViaKeyboard : IDisposable
{
    /// <summary>Usage page 0xFF60 + usage 0x61: así se identifica la interfaz raw HID de VIA.</summary>
    public const uint ViaUsage = 0xFF600061;
    public const int ReportSize = 32;

    private readonly HidDevice _device;
    private readonly HidStream _stream;
    private readonly object _ioLock = new(); // un solo comando a la vez (pregunta -> respuesta)

    public string ProductName { get; }
    public int VendorId => _device.VendorID;
    public int ProductId => _device.ProductID;
    public string DevicePath => _device.DevicePath;

    private ViaKeyboard(HidDevice device, HidStream stream)
    {
        _device = device;
        _stream = stream;
        _stream.ReadTimeout = 1000;
        try { ProductName = device.GetProductName(); } catch { ProductName = "Teclado VIA"; }
    }

    // ---------------------------------------------------------------- búsqueda

    /// <summary>¿Este dispositivo HID es una interfaz VIA? (lo mismo que hacía Program.cs)</summary>
    public static bool IsViaInterface(HidDevice dev)
    {
        try
        {
            foreach (var item in dev.GetReportDescriptor().DeviceItems)
                if (item.Usages.GetAllValues().Contains(ViaUsage)) return true;
        }
        catch { /* algunos dispositivos no dejan leer el descriptor */ }
        return false;
    }

    /// <summary>Devuelve la primera interfaz VIA (opcionalmente filtrando por fabricante).</summary>
    public static HidDevice? FindViaInterface(int? vendorId = null) =>
        DeviceList.Local.GetHidDevices()
            .Where(d => vendorId == null || d.VendorID == vendorId)
            .FirstOrDefault(IsViaInterface);

    /// <summary>Busca y abre el teclado. Devuelve null si no está o no se puede abrir.</summary>
    public static ViaKeyboard? Open(int? vendorId = null)
    {
        var dev = FindViaInterface(vendorId);
        if (dev == null || !dev.TryOpen(out HidStream stream)) return null;
        return new ViaKeyboard(dev, stream);
    }

    // ---------------------------------------------------------------- envío

    /// <summary>
    /// Envía un comando VIA y devuelve los 32 bytes de la respuesta.
    /// El teclado responde repitiendo el byte de comando; si responde 0xFF, no lo soporta.
    /// Lanza IOException si el teclado se ha desconectado y TimeoutException si no contesta.
    /// </summary>
    public byte[] Send(ViaCommand cmd, params byte[] args)
    {
        var buf = new byte[ReportSize + 1]; // buf[0] = report ID (0), luego los 32 bytes
        buf[1] = (byte)cmd;
        Array.Copy(args, 0, buf, 2, args.Length);

        lock (_ioLock)
        {
            _stream.Write(buf);
            // Leemos hasta encontrar la respuesta a NUESTRO comando (por si quedaba alguna antigua).
            for (int intento = 0; intento < 4; intento++)
            {
                var resp = _stream.Read();
                var data = resp.Length > ReportSize ? resp[1..] : resp; // quitar report ID
                if (data[0] == (byte)ViaCommand.Unhandled) throw new ViaUnsupportedException(cmd);
                if (data[0] == (byte)cmd) return data;
            }
        }
        throw new TimeoutException($"Respuesta inesperada al comando {cmd}.");
    }

    // ---------------------------------------------------------------- atajos

    public int GetProtocolVersion()
    {
        var r = Send(ViaCommand.GetProtocolVersion);
        return (r[1] << 8) | r[2];
    }

    public int GetLayerCount() => Send(ViaCommand.DynamicKeymapGetLayerCount)[1];

    /// <summary>Keycode (16 bits) de una tecla concreta.</summary>
    public ushort GetKeycode(int layer, int row, int col)
    {
        var r = Send(ViaCommand.DynamicKeymapGetKeycode, (byte)layer, (byte)row, (byte)col);
        return (ushort)((r[4] << 8) | r[5]);
    }

    /// <summary>
    /// Cambia una tecla: [0x05, capa, fila, col, keycode alto, keycode bajo].
    /// OJO: QMK lo guarda en la EEPROM al momento (no hace falta "guardar").
    /// </summary>
    public void SetKeycode(int layer, int row, int col, ushort keycode) =>
        Send(ViaCommand.DynamicKeymapSetKeycode, (byte)layer, (byte)row, (byte)col, (byte)(keycode >> 8), (byte)keycode);

    /// <summary>
    /// Keycode que envía una rueda (encoder) al girar: [0x14, capa, encoder, sentido]
    /// -> [0x14, capa, encoder, sentido, keycode alto, keycode bajo].
    /// Solo funciona si el firmware tiene ENCODER_MAP_ENABLE.
    /// </summary>
    /// <param name="clockwise">El indicador "sentido horario" de VIA. Ojo: en el R75 va al revés
    /// que el giro físico (ver <see cref="Knob"/>).</param>
    public ushort GetEncoder(int layer, int encoder, bool clockwise)
    {
        var r = Send(ViaCommand.DynamicKeymapGetEncoder, (byte)layer, (byte)encoder, (byte)(clockwise ? 1 : 0));
        return (ushort)((r[4] << 8) | r[5]);
    }

    /// <summary>Cambia el keycode de un sentido de giro. Se guarda al momento, como las teclas.</summary>
    public void SetEncoder(int layer, int encoder, bool clockwise, ushort keycode) =>
        Send(ViaCommand.DynamicKeymapSetEncoder, (byte)layer, (byte)encoder, (byte)(clockwise ? 1 : 0),
             (byte)(keycode >> 8), (byte)keycode);

    /// <summary>Devuelve TODAS las capas al keymap con el que se compiló el firmware.</summary>
    public void ResetKeymap() => Send(ViaCommand.DynamicKeymapReset);

    /// <summary>
    /// Lee una capa entera de golpe con el comando "get buffer" (0x12), que es mucho más
    /// rápido que pedir tecla a tecla. El keymap está guardado como
    /// [capa][fila][columna], 2 bytes por tecla (big endian), y cada lectura trae hasta 28 bytes.
    /// Resultado: keymap[fila * cols + col].
    /// </summary>
    public ushort[] GetLayer(int layer, int rows, int cols)
    {
        int totalBytes = rows * cols * 2;
        int start = layer * totalBytes;
        var bytes = new byte[totalBytes];
        for (int done = 0; done < totalBytes; done += 28)
        {
            int size = Math.Min(28, totalBytes - done);
            int offset = start + done;
            var r = Send(ViaCommand.DynamicKeymapGetBuffer, (byte)(offset >> 8), (byte)offset, (byte)size);
            Array.Copy(r, 4, bytes, done, size); // r = [0x12, off_hi, off_lo, size, datos...]
        }
        var keys = new ushort[rows * cols];
        for (int i = 0; i < keys.Length; i++) keys[i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
        return keys;
    }

    /// <summary>Escribe una capa entera de golpe (0x13, el inverso de GetLayer). Se guarda al momento.</summary>
    public void SetLayer(int layer, int rows, int cols, ushort[] keys)
    {
        var bytes = new byte[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++) { bytes[i * 2] = (byte)(keys[i] >> 8); bytes[i * 2 + 1] = (byte)keys[i]; }
        WriteBuffer(ViaCommand.DynamicKeymapSetBuffer, layer * rows * cols * 2, bytes);
    }

    // ---------------------------------------------------------------- macros
    // Todas las macros comparten un único "buffer" de bytes en la EEPROM, separadas por un 0.
    // Ver MacroCodec para el formato de cada una.

    public int GetMacroCount() => Send(ViaCommand.MacroGetCount)[1];

    public int GetMacroBufferSize()
    {
        var r = Send(ViaCommand.MacroGetBufferSize);
        return (r[1] << 8) | r[2];
    }

    public byte[] GetMacroBuffer(int size)
    {
        var buffer = new byte[size];
        for (int done = 0; done < size; done += 28)
        {
            int n = Math.Min(28, size - done);
            var r = Send(ViaCommand.MacroGetBuffer, (byte)(done >> 8), (byte)done, (byte)n);
            Array.Copy(r, 4, buffer, done, n);
        }
        return buffer;
    }

    /// <summary>
    /// Escribe el buffer de macros. Si se pasa el contenido anterior, solo se envían los trozos
    /// que han cambiado (menos escrituras en la EEPROM y más rápido).
    /// </summary>
    public void SetMacroBuffer(byte[] buffer, byte[]? previous = null)
    {
        for (int done = 0; done < buffer.Length; done += 28)
        {
            int n = Math.Min(28, buffer.Length - done);
            if (previous != null && previous.Length == buffer.Length &&
                buffer.AsSpan(done, n).SequenceEqual(previous.AsSpan(done, n)))
                continue;
            WriteBuffer(ViaCommand.MacroSetBuffer, done, buffer[done..(done + n)]);
        }
    }

    /// <summary>Envía bytes en trozos de 28: [cmd, offset alto, offset bajo, tamaño, datos...].</summary>
    private void WriteBuffer(ViaCommand cmd, int offset, byte[] data)
    {
        for (int done = 0; done < data.Length; done += 28)
        {
            int n = Math.Min(28, data.Length - done);
            int off = offset + done;
            Send(cmd, [(byte)(off >> 8), (byte)off, (byte)n, .. data.AsSpan(done, n)]);
        }
    }

    /// <summary>
    /// Protocolo VIA 12 o superior: valores "custom" agrupados en canales.
    /// get: [0x08, canal, id] -> respuesta [0x08, canal, id, valor...]
    /// </summary>
    public byte[] GetCustomValue(byte channel, byte valueId, int length)
    {
        var r = Send(ViaCommand.CustomGetValue, channel, valueId);
        return r[3..(3 + length)];
    }

    /// <summary>set: [0x07, canal, id, valor...]. El cambio es inmediato pero NO se guarda.</summary>
    public void SetCustomValue(byte channel, byte valueId, params byte[] value) =>
        Send(ViaCommand.CustomSetValue, [channel, valueId, .. value]);

    /// <summary>save: [0x09, canal]. Guarda los valores del canal en la EEPROM del teclado.</summary>
    public void SaveCustom(byte channel) => Send(ViaCommand.CustomSave, channel);

    public void Dispose() => _stream.Dispose();
}
