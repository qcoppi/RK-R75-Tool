using System.IO;
using System.Text.Json;
using R75Tool.Core;

namespace R75Tool.App;

/// <summary>Un perfil: cuando el programa indicado está en primer plano, se aplica esta iluminación.</summary>
public sealed class AppProfile
{
    public string Name { get; set; } = "Nuevo perfil";

    /// <summary>Nombre del proceso sin ".exe" (p. ej. "chrome"), sin distinguir mayúsculas.</summary>
    public string ProcessName { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public RgbState Lighting { get; set; } = new(150, 1, 128, 0, 255);
}

/// <summary>
/// Preferencias de la app. Se guardan como JSON en %AppData%\R75Tool\settings.json
/// (no en el teclado: son cosas del PC, como los perfiles).
/// </summary>
public sealed class AppSettings
{
    /// <summary>"auto" (según Windows), "es" o "en".</summary>
    public string Layout { get; set; } = "auto";

    /// <summary>Al cerrar la ventana, seguir en la bandeja del sistema en vez de salir.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Ya se ha explicado (una vez) que la app sigue en la bandeja al cerrar.</summary>
    public bool TrayTipShown { get; set; }

    public bool ProfilesEnabled { get; set; }

    /// <summary>La rueda (teclas Vol+/Vol-) cambia el volumen del programa activo, no el general.</summary>
    public bool PerAppVolume { get; set; }

    /// <summary>Puntos de volumen (0-100) por cada paso de la rueda. Windows usa 2.</summary>
    public int VolumeStep { get; set; } = 2;

    /// <summary>Pulsar la rueda (Mute) silencia solo el programa activo.</summary>
    public bool PerAppMute { get; set; } = true;

    public bool ShowVolumeOsd { get; set; } = true;

    /// <summary>Iluminación para los programas que no tienen perfil.</summary>
    public RgbState? DefaultLighting { get; set; }

    public List<AppProfile> Profiles { get; set; } = [];

    // ------------------------------------------------------------ guardar / cargar

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "R75Tool");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Los ajustes en uso (se cargan al arrancar).</summary>
    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch
        {
            Current = new(); // archivo corrupto: empezamos de cero en vez de no arrancar
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
    }

    /// <summary>Distribución que corresponde al ajuste "Layout".</summary>
    public HostLayout ResolveLayout(System.Globalization.CultureInfo? windowsInput) => Layout switch
    {
        "es" => HostLayout.Spanish,
        "en" => HostLayout.English,
        _ => HostLayout.ForCulture(windowsInput),
    };
}
