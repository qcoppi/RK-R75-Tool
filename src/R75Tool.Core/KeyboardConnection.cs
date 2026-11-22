using HidSharp;

namespace R75Tool.Core;

/// <summary>Datos que leemos del teclado al conectar.</summary>
public sealed record KeyboardInfo(string ProductName, int VendorId, int ProductId, int ProtocolVersion, int LayerCount,
                                  int MacroCount, int MacroBufferSize);

/// <summary>
/// Mantiene la conexión con el teclado durante toda la vida de la app:
/// - se conecta solo al arrancar y cuando se enchufa el teclado;
/// - detecta la desconexión;
/// - ejecuta los comandos en segundo plano para no congelar la interfaz.
///
/// Los eventos se lanzan desde hilos de fondo: la interfaz tiene que pasar al hilo de UI
/// (Dispatcher) antes de tocar controles.
/// </summary>
public sealed class KeyboardConnection : IDisposable
{
    private readonly IReadOnlyList<KeyboardDefinition> _definitions;
    private readonly object _stateLock = new();
    private ViaKeyboard? _keyboard;
    private KeyboardDefinition _definition;

    /// <param name="definitions">Variantes del teclado (ANSI, ISO...). Se elige la del PID conectado.</param>
    public KeyboardConnection(IReadOnlyList<KeyboardDefinition> definitions)
    {
        _definitions = definitions;
        _definition = definitions[0];
    }

    /// <summary>Definición del teclado conectado (o la primera si no hay ninguno).</summary>
    public KeyboardDefinition Definition => _definition;
    public bool IsConnected => _keyboard != null;
    public KeyboardInfo? Info { get; private set; }

    /// <summary>Capa 0 del keymap (keymap[fila * cols + col]), leída al conectar.</summary>
    public ushort[]? BaseLayer { get; private set; }

    /// <summary>Último estado conocido de la iluminación.</summary>
    public RgbState? Lighting { get; private set; }

    public event EventHandler? ConnectionChanged;
    public event EventHandler? LightingChanged;

    /// <summary>Empieza a vigilar los dispositivos USB e intenta conectar.</summary>
    public void Start()
    {
        DeviceList.Local.Changed += (_, _) => Task.Run(Refresh);
        Task.Run(Refresh);
    }

    /// <summary>
    /// Se llama cada vez que Windows avisa de un cambio en los dispositivos HID
    /// (se dispara por cualquier USB, no solo por el teclado).
    /// </summary>
    public void Refresh()
    {
        lock (_stateLock)
        {
            if (_keyboard != null)
            {
                bool sigue = DeviceList.Local.GetHidDevices().Any(d => d.DevicePath == _keyboard.DevicePath);
                if (!sigue) DisconnectLocked();
                return;
            }

            var kb = ViaKeyboard.Open(_definitions[0].VendorId);
            if (kb == null) return;
            try
            {
                _definition = _definitions.FirstOrDefault(d => d.ProductId == kb.ProductId) ?? _definitions[0];
                // Las macros son opcionales en QMK: si el firmware no las tiene, 0
                int macroCount = 0, macroSize = 0;
                try { macroCount = kb.GetMacroCount(); macroSize = kb.GetMacroBufferSize(); }
                catch (ViaUnsupportedException) { }

                Info = new KeyboardInfo(kb.ProductName, kb.VendorId, kb.ProductId,
                                        kb.GetProtocolVersion(), kb.GetLayerCount(), macroCount, macroSize);
                BaseLayer = kb.GetLayer(0, _definition.Rows, _definition.Cols);
                Lighting = RgbMatrix.Read(kb);
                _keyboard = kb;
            }
            catch
            {
                kb.Dispose(); // ha fallado algo al presentarnos: lo intentaremos en el próximo cambio
                return;
            }
        }
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
        LightingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ejecuta una operación con el teclado en un hilo de fondo.
    /// Si el teclado se ha desconectado, actualiza el estado y relanza el error.
    /// </summary>
    public Task<T> RunAsync<T>(Func<ViaKeyboard, T> action) => Task.Run(() =>
    {
        var kb = _keyboard ?? throw new InvalidOperationException("El teclado no está conectado.");
        try
        {
            return action(kb);
        }
        catch (IOException)
        {
            lock (_stateLock) DisconnectLocked();
            throw;
        }
    });

    public Task RunAsync(Action<ViaKeyboard> action) => RunAsync(kb => { action(kb); return true; });

    // ------------------------------------------------------------ envío "último gana"
    // Al arrastrar un slider llegan decenas de valores por segundo. En vez de mandarlos todos,
    // guardamos solo el último de cada tipo ("brillo", "color"...) y un único bucle los envía.

    // OrderedDictionary mantiene el orden de llegada: si se pide "guardar" después de mover
    // el brillo, el brillo se envía antes.
    private readonly OrderedDictionary<string, PendingCommand> _pending = [];
    private bool _draining;

    private sealed record PendingCommand(Action<ViaKeyboard> Action, TaskCompletionSource Done);

    /// <summary>
    /// Encola un comando. Si ya había uno pendiente con la misma clave, lo sustituye.
    /// La Task se completa cuando se ha enviado (o falla si el teclado no responde);
    /// se puede esperar con await o ignorar.
    /// </summary>
    public Task Post(string key, Action<ViaKeyboard> action)
    {
        var cmd = new PendingCommand(action, new TaskCompletionSource());
        lock (_pending)
        {
            if (_pending.TryGetValue(key, out var old))
            {
                old.Done.TrySetResult(); // el antiguo ya no hace falta enviarlo
                _pending[key] = cmd;
            }
            else
            {
                _pending.Add(key, cmd);
            }
            if (!_draining)
            {
                _draining = true;
                _ = Task.Run(DrainAsync);
            }
        }
        return cmd.Done.Task;
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            PendingCommand next;
            lock (_pending)
            {
                if (_pending.Count == 0) { _draining = false; return; }
                next = _pending.GetAt(0).Value;
                _pending.RemoveAt(0);
            }
            try
            {
                await RunAsync(next.Action);
                next.Done.TrySetResult();
            }
            catch (Exception e)
            {
                next.Done.TrySetException(e);
            }
        }
    }

    // ------------------------------------------------------------ iluminación

    /// <summary>Actualiza el estado guardado y avisa (para que el dibujo cambie de color).</summary>
    public void UpdateLighting(RgbState state)
    {
        Lighting = state;
        LightingChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------ keymap

    public event EventHandler? KeymapChanged;

    /// <summary>Avisa de que una tecla ha cambiado (si es de la capa 0, actualiza la copia guardada).</summary>
    public void NotifyKeyChanged(int layer, int row, int col, ushort keycode)
    {
        if (layer == 0 && BaseLayer != null) BaseLayer[row * _definition.Cols + col] = keycode;
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Vuelve a leer la capa 0 entera (p. ej. tras restaurar el keymap de fábrica).</summary>
    public async Task ReloadBaseLayerAsync()
    {
        BaseLayer = await RunAsync(kb => kb.GetLayer(0, _definition.Rows, _definition.Cols));
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------

    private void DisconnectLocked()
    {
        if (_keyboard == null) return;
        _keyboard.Dispose();
        _keyboard = null;
        Info = null;
        BaseLayer = null;
        Task.Run(() => ConnectionChanged?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _keyboard?.Dispose();
            _keyboard = null;
        }
    }
}
