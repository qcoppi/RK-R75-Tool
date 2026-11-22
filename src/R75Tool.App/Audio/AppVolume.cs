using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace R75Tool.App.Audio;

/// <summary>Resultado de cambiar el volumen de un programa (para el indicador en pantalla).</summary>
public sealed record AppVolumeResult(string AppName, float Volume, bool Muted);

/// <summary>
/// Volumen por aplicación, igual que el "Mezclador de volumen" de Windows.
///
/// Windows crea una "sesión de audio" por cada programa que emite sonido y por cada salida
/// (auriculares, altavoces...). Cada sesión tiene su propio volumen (ISimpleAudioVolume).
/// Aquí buscamos las sesiones del programa que está en primer plano y cambiamos su volumen.
/// </summary>
/// <remarks>
/// Los objetos de audio de Windows (COM) deben usarse desde el mismo hilo que los creó:
/// crea y usa esta clase siempre desde el mismo hilo.
/// </remarks>
public sealed class AppVolume : IDisposable
{
    private readonly MMDeviceEnumerator _devices = new();

    // Caché: buscar las sesiones recorre todas las salidas de audio y los procesos, y eso
    // tarda. Mientras sigas en el mismo programa reutilizamos lo encontrado.
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(5);
    private int _cachedPid;
    private DateTime _cachedAt;
    private (List<AudioSessionControl> Sessions, string Name) _cached = ([], "");
    private readonly Dictionary<int, string> _names = [];

    /// <summary>Sube o baja el volumen del programa (en puntos de 0 a 100). null si no tiene sonido.</summary>
    public AppVolumeResult? ChangeVolume(int pid, int deltaPercent)
    {
        try { return ChangeVolumeCore(pid, deltaPercent); }
        catch (COMException) // una sesión de la caché ya no existe (p. ej. el juego reinició el audio)
        {
            _cachedPid = 0;
            return ChangeVolumeCore(pid, deltaPercent);
        }
    }

    private AppVolumeResult? ChangeVolumeCore(int pid, int deltaPercent)
    {
        var target = FindSessionsCached(pid);
        if (target.Sessions.Count == 0) return null;

        // Tomamos como referencia la sesión que está sonando (si hay), y ponemos todas igual
        var reference = target.Sessions.FirstOrDefault(s => s.State == AudioSessionState.AudioSessionStateActive)
                        ?? target.Sessions[0];
        int current = (int)Math.Round(reference.SimpleAudioVolume.Volume * 100);
        float level = Math.Clamp(current + deltaPercent, 0, 100) / 100f;
        foreach (var s in target.Sessions)
        {
            s.SimpleAudioVolume.Volume = level;
            if (deltaPercent > 0) s.SimpleAudioVolume.Mute = false; // subir el volumen quita el silencio
        }
        return new AppVolumeResult(target.Name, level, reference.SimpleAudioVolume.Mute);
    }

    /// <summary>Silencia o quita el silencio del programa. null si no tiene sonido.</summary>
    public AppVolumeResult? ToggleMute(int pid)
    {
        var target = FindSessionsCached(pid);
        if (target.Sessions.Count == 0) return null;
        bool mute = !target.Sessions.All(s => s.SimpleAudioVolume.Mute);
        foreach (var s in target.Sessions) s.SimpleAudioVolume.Mute = mute;
        return new AppVolumeResult(target.Name, target.Sessions[0].SimpleAudioVolume.Volume, mute);
    }

    // ------------------------------------------------------------ buscar las sesiones

    private (List<AudioSessionControl> Sessions, string Name) FindSessionsCached(int pid)
    {
        if (pid == _cachedPid && DateTime.UtcNow - _cachedAt < CacheTime && _cached.Sessions.Count > 0)
            return _cached;
        _cached = FindSessions(pid);
        _cachedPid = pid;
        _cachedAt = DateTime.UtcNow;
        return _cached;
    }

    /// <summary>
    /// Sesiones que pertenecen al programa. No basta con el proceso de la ventana: muchos
    /// juegos y navegadores emiten el sonido desde un proceso hijo (p. ej. LeagueClientUx ->
    /// LeagueClientUxRender). Por eso buscamos, en este orden:
    ///   1) el proceso de la ventana y todos sus descendientes;
    ///   2) si no hay ninguno, cualquier proceso con el mismo nombre (.exe).
    /// </summary>
    private (List<AudioSessionControl> Sessions, string Name) FindSessions(int pid)
    {
        var processes = ProcessTree.Snapshot();
        string exeName = processes.TryGetValue(pid, out var p) ? p.Name : "";

        // Solo cuentan los descendientes SIN ventana propia (procesos auxiliares de sonido).
        // Si no, con Steam delante se bajaría también el juego que lanzó, y con el escritorio
        // (explorer) todos los programas abiertos desde él.
        var family = exeName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            ? new HashSet<int> { pid }
            : ProcessTree.WithDescendants(pid, processes)
                         .Where(x => x == pid || !ProcessTree.HasWindow(x))
                         .ToHashSet();

        var byFamily = new List<AudioSessionControl>();
        var byName = new List<AudioSessionControl>();
        foreach (var device in _devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions(); // si no, no aparecen las sesiones creadas desde la última vez
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.IsSystemSoundsSession) continue;
                int spid = (int)s.GetProcessID;
                if (family.Contains(spid)) byFamily.Add(s);
                else if (exeName.Length > 0 && processes.TryGetValue(spid, out var sp) &&
                         sp.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase)) byName.Add(s);
            }
        }
        var found = byFamily.Count > 0 ? byFamily : byName;
        return (found, FriendlyName(pid, exeName));
    }

    /// <summary>
    /// Nombre bonito: la descripción del .exe ("League of Legends", "Discord") o, si no, el nombre del proceso.
    ///
    /// No usamos Process.MainModule: con juegos protegidos por antitrampas (Vanguard, EAC...)
    /// Windows tarda casi un segundo en denegar el acceso. En su lugar pedimos solo la ruta del
    /// .exe con el permiso mínimo y leemos la descripción del ARCHIVO, que es instantáneo.
    /// </summary>
    private string FriendlyName(int pid, string exeName)
    {
        if (_names.TryGetValue(pid, out var cached)) return cached;
        string name = exeName.Length > 0 ? exeName : "Programa";
        if (ImagePath(pid) is { } path)
        {
            try
            {
                string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
            }
            catch { /* archivo sin información de versión */ }
        }
        _names[pid] = name;
        return name;
    }

    private static string? ImagePath(int pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, char[] buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public void Dispose() => _devices.Dispose();
}

/// <summary>Árbol de procesos (quién es hijo de quién), con la API Toolhelp de Windows.</summary>
internal static class ProcessTree
{
    public sealed record Info(int Pid, int ParentPid, string Name);

    public static Dictionary<int, Info> Snapshot()
    {
        var result = new Dictionary<int, Info>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return result;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
            {
                string name = e.szExeFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? e.szExeFile[..^4] : e.szExeFile;
                result[(int)e.th32ProcessID] = new Info((int)e.th32ProcessID, (int)e.th32ParentProcessID, name);
            }
        }
        finally { CloseHandle(snap); }
        return result;
    }

    /// <summary>El proceso y todos sus hijos, nietos...</summary>
    public static HashSet<int> WithDescendants(int pid, Dictionary<int, Info> all)
    {
        var family = new HashSet<int> { pid };
        bool added = true;
        while (added) // se repite hasta que no aparecen más descendientes
        {
            added = false;
            foreach (var p in all.Values)
                if (p.Pid != 0 && family.Contains(p.ParentPid) && family.Add(p.Pid)) added = true;
        }
        return family;
    }

    /// <summary>¿El proceso tiene una ventana principal propia (es un programa "de verdad")?</summary>
    public static bool HasWindow(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowHandle != IntPtr.Zero;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
