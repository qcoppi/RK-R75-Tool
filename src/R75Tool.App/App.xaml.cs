using System.Windows;
using R75Tool.Core;

namespace R75Tool.App;

public partial class App : Application
{
    /// <summary>
    /// La conexión con el teclado es única para toda la app, así que la dejamos accesible
    /// desde cualquier página como App.Keyboard. (Más adelante se puede cambiar por
    /// inyección de dependencias si el proyecto crece.)
    /// </summary>
    public static KeyboardConnection Keyboard { get; private set; } = null!;

    /// <summary>Perfiles por programa (vigila qué programa está en primer plano).</summary>
    public static ProfileService Profiles { get; private set; } = null!;

    /// <summary>Volumen del programa activo con la rueda.</summary>
    public static Audio.PerAppVolumeService PerAppVolume { get; private set; } = null!;

    private static TrayIcon? _tray;
    private static bool _exiting;

    /// <summary>Argumento que usa "Iniciar con Windows": arrancar solo en la bandeja.</summary>
    public const string MinimizedArg = "--minimized";

    // Instancia única: un "mutex" con nombre que solo puede tener un proceso a la vez,
    // y un evento con nombre para decirle a la instancia abierta que muestre su ventana.
    private const string InstanceName = "R75Tool.SingleInstance";
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Variable de entorno solo para las pruebas automáticas (abren la app mientras la tuya sigue abierta)
        if (Environment.GetEnvironmentVariable("R75TOOL_ALLOW_MULTI") != "1")
        {
            _instanceMutex = new Mutex(true, InstanceName, out bool first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(InstanceName + ".Show").Set(); } catch { }
                Shutdown(); // ya hay una abierta: le pedimos que se muestre y salimos
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
            ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        // La app no termina al cerrar la ventana (puede seguir en la bandeja): salimos a mano
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppSettings.Load();
        // Las teclas de símbolos se dibujan según la distribución del idioma de entrada de
        // Windows (el teclado solo manda códigos; Windows decide qué carácter es cada uno).
        ApplyLayoutSetting();

        var definitions = KeyboardDefinition.LoadAll(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets"));
        Keyboard = new KeyboardConnection(definitions);
        Profiles = new ProfileService();

        _tray = new TrayIcon();

        Keyboard.Start();  // empieza a buscar el teclado en segundo plano
        Profiles.Start();
        PerAppVolume = new Audio.PerAppVolumeService();
        PerAppVolume.ApplySettings();

        // Arrancando con Windows (--minimized) ni siquiera creamos la ventana: se crea al abrirla
        if (e.Args.Contains(MinimizedArg)) TrimMemorySoon(TimeSpan.FromSeconds(5));
        else ShowMainWindow();
    }

    /// <summary>Aplica el ajuste de distribución (automática, española o inglesa).</summary>
    public static void ApplyLayoutSetting()
    {
        System.Globalization.CultureInfo? inputLanguage;
        try { inputLanguage = System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage; }
        catch { inputLanguage = System.Globalization.CultureInfo.CurrentCulture; } // por si no se puede leer
        KeycodeNames.Layout = AppSettings.Current.ResolveLayout(inputLanguage);
    }

    // ------------------------------------------------------------ ventana y bandeja

    // La ventana principal solo existe mientras está abierta. Al mandarla a la bandeja se
    // destruye para liberar su memoria (la rueda, los perfiles y el teclado no dependen de ella).
    // Usamos un campo propio y no solo Application.MainWindow, porque WPF le asignaría
    // cualquier otra ventana que se cree (p. ej. el indicador de volumen).
    private static MainWindow? _window;

    public static void ShowMainWindow()
    {
        if (_exiting) return;
        if (_window == null)
        {
            _window = new MainWindow();
            Current.MainWindow = _window;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>La ventana avisa de que se ha cerrado: seguimos en la bandeja o salimos.</summary>
    public static void OnMainWindowClosed(bool toTray)
    {
        _window = null;
        if (Current.MainWindow is MainWindow) Current.MainWindow = null;
        if (!toTray) { ExitApp(); return; }
        TrimMemorySoon(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Libera memoria cuando la app se queda solo en la bandeja:
    ///  1. el recolector de basura de .NET recoge lo que usaba la ventana;
    ///  2. le decimos a Windows que puede quitarnos de la RAM las páginas que no usamos
    ///     (si luego hacen falta, Windows las vuelve a cargar solas).
    /// </summary>
    private static void TrimMemorySoon(TimeSpan delay)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_window != null) return; // la han vuelto a abrir mientras tanto
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
        };
        timer.Start();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    /// <summary>
    /// La ventana llama a esto al cerrarse. Devuelve true si la app debe seguir en la bandeja
    /// (ajuste "seguir en la bandeja" activado) en vez de terminar.
    /// </summary>
    public static bool ShouldHideInsteadOfClose()
    {
        if (_exiting || !AppSettings.Current.CloseToTray) return false;
        if (!AppSettings.Current.TrayTipShown)
        {
            _tray?.ShowTip("R75 Tool sigue funcionando", "Está aquí, en la bandeja. Clic derecho → Salir para cerrarla.");
            AppSettings.Current.TrayTipShown = true;
            AppSettings.Save();
        }
        return true;
    }

    public static void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Current.Shutdown();
    }

    public static void SetProfilesEnabled(bool enabled)
    {
        var s = AppSettings.Current;
        s.ProfilesEnabled = enabled;
        // La primera vez, "resto de programas" = la iluminación que tenga ahora el teclado
        if (enabled && s.DefaultLighting == null) s.DefaultLighting = Keyboard.Lighting;
        AppSettings.Save();
        Profiles.Reevaluate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        PerAppVolume?.Dispose();
        Keyboard?.Dispose(); // puede ser null si salimos por ser la segunda instancia
        base.OnExit(e);
    }
}
