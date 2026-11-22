using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using R75Tool.Core;

namespace R75Tool.App;

/// <summary>
/// Perfiles por programa: cada poco tiempo mira qué programa está en primer plano y,
/// si cambia, aplica la iluminación de su perfil (o la de "resto de programas").
///
/// La iluminación se cambia SIN guardar (sin RgbMatrix.Save): va a la RAM del teclado, así
/// que cambiar de perfil cientos de veces al día no desgasta su memoria EEPROM.
/// </summary>
public sealed class ProfileService
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly string _ownProcess = Process.GetCurrentProcess().ProcessName;
    private string? _lastProcess;
    private RgbState? _lastApplied;

    /// <summary>Programa en primer plano (sin .exe), o null si no se sabe.</summary>
    public string? ForegroundProcess { get; private set; }

    /// <summary>Perfil aplicado ahora mismo (null = "resto de programas" o ninguno).</summary>
    public AppProfile? ActiveProfile { get; private set; }

    /// <summary>Se lanza cuando cambia el programa en primer plano o el perfil activo.</summary>
    public event EventHandler? Changed;

    public ProfileService() => _timer.Tick += (_, _) => Check();

    public void Start() => _timer.Start();

    /// <summary>Olvida lo último aplicado para que la próxima comprobación vuelva a aplicar (p. ej. tras editar un perfil).</summary>
    public void Reevaluate()
    {
        _lastProcess = null;
        _lastApplied = null;
        Check();
    }

    private void Check()
    {
        var settings = AppSettings.Current;
        string? process = GetForegroundProcessName();

        // Si el foco está en esta misma app no cambiamos nada: así puedes editar un perfil
        // sin que la iluminación salte al de "resto de programas".
        if (process == null || process.Equals(_ownProcess, StringComparison.OrdinalIgnoreCase)) return;
        if (process == _lastProcess) return;
        _lastProcess = process;
        ForegroundProcess = process;

        ActiveProfile = settings.Profiles.FirstOrDefault(p =>
            p.Enabled && p.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase));
        Changed?.Invoke(this, EventArgs.Empty);

        if (!settings.ProfilesEnabled || !App.Keyboard.IsConnected) return;
        var target = ActiveProfile?.Lighting ?? settings.DefaultLighting;
        if (target != null && target != _lastApplied) _ = ApplyAsync(target);
    }

    /// <summary>Envía una iluminación completa al teclado (sin guardarla).</summary>
    public async Task ApplyAsync(RgbState state)
    {
        _lastApplied = state;
        try
        {
            await App.Keyboard.RunAsync(kb =>
            {
                RgbMatrix.SetEffect(kb, state.Effect);
                RgbMatrix.SetBrightnessExact(kb, state.Brightness);
                RgbMatrix.SetSpeed(kb, state.Speed);
                RgbMatrix.SetColor(kb, state.Hue, state.Saturation);
            });
            App.Keyboard.UpdateLighting(state); // para que el dibujo y las páginas se enteren
        }
        catch
        {
            _lastApplied = null; // desconectado: se volverá a intentar en el próximo cambio
        }
    }

    // ------------------------------------------------------------ Win32

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static string? GetForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null; // el proceso acaba de cerrarse, o es de otro usuario
        }
    }
}
