using System.IO;

namespace R75Tool.App.Audio;

/// <summary>
/// Registro de lo que hace el volumen por aplicación (%AppData%\R75Tool\volume.log).
/// Sirve para averiguar por qué algo no funciona en un juego concreto.
/// Solo guarda las últimas ~200 líneas.
/// </summary>
public static class VolumeLog
{
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "volume.log");
    private static readonly object Lock = new();

    /// <summary>Última línea escrita (la muestra la página de la rueda).</summary>
    public static string? Last { get; private set; }

    public static event Action<string>? Written;

    public static void Write(string message)
    {
        Last = message;
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
                var info = new FileInfo(FilePath);
                if (info.Length > 64 * 1024) // recortar: nos quedamos con las últimas 200 líneas
                    File.WriteAllLines(FilePath, File.ReadAllLines(FilePath).TakeLast(200));
            }
        }
        catch { /* el registro nunca debe romper la app */ }
        Written?.Invoke(message);
    }
}
