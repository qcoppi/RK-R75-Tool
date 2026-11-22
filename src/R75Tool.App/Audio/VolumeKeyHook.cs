using System.Runtime.InteropServices;

namespace R75Tool.App.Audio;

public enum VolumeKey { Up, Down, Mute }

/// <summary>Una pulsación capturada y el programa que estaba en primer plano en ese instante.</summary>
public readonly record struct VolumePress(VolumeKey Key, int? Pid);

/// <summary>
/// Intercepta las teclas multimedia de volumen (las que envía la rueda: Vol+, Vol-, Mute)
/// ANTES de que lleguen a Windows, con un "hook" de teclado de bajo nivel (WH_KEYBOARD_LL).
///
/// - Si <see cref="ShouldCapture"/> dice que sí, la tecla se "traga" (Windows no la ve) y se
///   lanza <see cref="Pressed"/>.
/// - Cuando devolvemos una tecla a Windows (<see cref="SendToWindows"/>) la "firmamos" con
///   <see cref="Signature"/> en dwExtraInfo, y el hook deja pasar solo las que llevan esa firma.
///   OJO: no vale con mirar el flag INJECTED, porque las teclas multimedia de la rueda llegan
///   ya marcadas como inyectadas (Windows las genera a partir del "control de consumo" USB).
///
/// El hook vive en SU PROPIO HILO, que no hace nada más: Windows llama al hook a través de la
/// cola de mensajes de ese hilo, y si el hilo está ocupado las teclas se quedan esperando
/// (y si tarda demasiado, Windows quita el hook). Por eso aquí solo se decide y se apunta
/// qué programa estaba delante; el trabajo de verdad lo hace otro hilo.
/// </summary>
public sealed class VolumeKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10;
    private const byte VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

    /// <summary>Marca de nuestras propias pulsaciones ("R75T" en ASCII).</summary>
    private static readonly IntPtr Signature = new(0x52373554);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Hay que guardar el delegado en un campo: si el recolector de basura lo libera,
    // Windows llamaría a memoria inválida y la app se cerraría.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;

    /// <summary>Decide al momento si una tecla se captura (debe ser rapidísimo).</summary>
    public Func<VolumeKey, bool> ShouldCapture { get; set; } = _ => false;

    /// <summary>Tecla capturada. Se lanza desde el hilo del hook: hay que salir enseguida.</summary>
    public event Action<VolumePress>? Pressed;

    /// <summary>Para diagnosticar: cada tecla de volumen que ve el hook. También desde el hilo del hook.</summary>
    public event Action<string>? Seen;

    public VolumeKeyHook()
    {
        _proc = Callback;
        using var ready = new ManualResetEventSlim();
        Exception? error = null;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) error = new InvalidOperationException("No se ha podido instalar el hook de teclado.");
            ready.Set();
            if (_hook == IntPtr.Zero) return;

            // Bucle de mensajes: mientras corre, Windows puede llamar a Callback
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(_hook);
        })
        { IsBackground = true, Name = "Hook teclas de volumen" };
        _thread.Start();
        ready.Wait();
        if (error != null) throw error;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            VolumeKey? key = info.vkCode switch
            {
                VK_VOLUME_UP => VolumeKey.Up,
                VK_VOLUME_DOWN => VolumeKey.Down,
                VK_VOLUME_MUTE => VolumeKey.Mute,
                _ => null,
            };
            if (key is { } k)
            {
                bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
                bool ours = info.dwExtraInfo == Signature;
                if (down)
                    Seen?.Invoke($"tecla vista: {k} flags=0x{info.flags:X2}" +
                                 $"{((info.flags & LLKHF_INJECTED) != 0 ? " (inyectada)" : "")}{(ours ? " (nuestra)" : "")}");
                if (!ours && ShouldCapture(k))
                {
                    // Apuntamos qué programa está delante AHORA (no cuando se procese)
                    if (down) Pressed?.Invoke(new VolumePress(k, ForegroundProcessId()));
                    return 1; // != 0 = "tecla consumida" (también al soltar, para que Windows no vea medio evento)
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Devuelve la tecla a Windows (volumen general y su indicador de siempre).</summary>
    public static void SendToWindows(VolumeKey key)
    {
        byte vk = key switch { VolumeKey.Up => VK_VOLUME_UP, VolumeKey.Down => VK_VOLUME_DOWN, _ => VK_VOLUME_MUTE };
        const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, (UIntPtr)(ulong)Signature);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, (UIntPtr)(ulong)Signature);
    }

    public static int? ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 ? null : (int)pid;
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); // termina el bucle y quita el hook
        _threadId = 0;
    }

    // ------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
