using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;

namespace R75Tool.App.Audio;

/// <summary>
/// "La rueda controla el volumen del programa activo". Tres hilos colaboran:
///   1. el hilo del hook (VolumeKeyHook) se queda con Vol+/Vol-/Mute y apunta qué programa
///      estaba delante; no hace nada más, para que la rueda nunca se quede esperando;
///   2. este hilo de trabajo cambia el volumen (AppVolume). Si llegan varias pulsaciones
///      seguidas las junta: girar 5 pasos rápido = un único cambio de 5 × paso;
///   3. el hilo de la interfaz enseña el indicador (VolumeOsd).
/// Si el programa no emite sonido (escritorio, explorador...), la tecla se devuelve a
/// Windows y funciona como siempre (volumen general).
/// </summary>
public sealed class PerAppVolumeService : IDisposable
{
    private readonly BlockingCollection<VolumePress> _queue = new();
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private VolumeKeyHook? _hook;
    private VolumeOsd? _osd;

    public bool IsActive => _hook != null;

    public PerAppVolumeService()
    {
        new Thread(WorkerLoop) { IsBackground = true, Name = "Volumen por aplicación" }.Start();
    }

    /// <summary>Instala o quita el hook según los ajustes (llamar al arrancar y al cambiarlos).</summary>
    public void ApplySettings()
    {
        bool enabled = AppSettings.Current.PerAppVolume;
        if (enabled && _hook == null)
        {
            try
            {
                _hook = new VolumeKeyHook
                {
                    ShouldCapture = key => AppSettings.Current.PerAppVolume &&
                                           (key != VolumeKey.Mute || AppSettings.Current.PerAppMute),
                };
                _hook.Pressed += press => _queue.Add(press); // rapidísimo: solo encolar
                VolumeLog.Write("Activado: esperando la rueda (teclas Vol+/Vol-/Mute)");
            }
            catch (Exception ex)
            {
                _hook = null;
                VolumeLog.Write($"ERROR al instalar el hook de teclado: {ex.Message}");
            }
        }
        else if (!enabled && _hook != null)
        {
            _hook.Dispose();
            _hook = null;
            VolumeLog.Write("Desactivado: la rueda vuelve a controlar el volumen general");
        }
    }

    // ===================================================================== hilo de trabajo

    private void WorkerLoop()
    {
        // AppVolume usa objetos COM: se crea y se usa siempre en este mismo hilo
        using var volume = new AppVolume();
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            // Todo lo que haya llegado mientras tanto se procesa de una vez
            var batch = new List<VolumePress> { first };
            while (_queue.TryTake(out var more)) batch.Add(more);

            // Grupos seguidos con el mismo programa y el mismo tipo (volumen o silencio)
            for (int i = 0; i < batch.Count;)
            {
                int j = i;
                bool mute = batch[i].Key == VolumeKey.Mute;
                while (j < batch.Count && batch[j].Pid == batch[i].Pid && (batch[j].Key == VolumeKey.Mute) == mute) j++;
                Handle(volume, batch.GetRange(i, j - i));
                i = j;
            }
        }
    }

    private void Handle(AppVolume volume, List<VolumePress> group)
    {
        int? pid = group[0].Pid;
        bool mute = group[0].Key == VolumeKey.Mute;
        int steps = group.Sum(p => p.Key switch { VolumeKey.Up => 1, VolumeKey.Down => -1, _ => 0 });
        string what = mute ? $"Mute ×{group.Count}" : $"{(steps >= 0 ? "Vol+" : "Vol-")} ×{Math.Abs(steps)}";
        var sw = Stopwatch.StartNew();

        AppVolumeResult? result = null;
        try
        {
            if (pid is { } id)
            {
                if (mute) result = group.Count % 2 == 1 ? volume.ToggleMute(id) : null; // pulsar 2 veces = nada
                else if (steps != 0) result = volume.ChangeVolume(id, steps * AppSettings.Current.VolumeStep);
                if (result == null && (mute ? group.Count % 2 == 0 : steps == 0)) return; // se anulan entre sí
            }
        }
        catch (Exception ex)
        {
            VolumeLog.Write($"{what} en {ProcessName(pid)}: ERROR {ex.GetType().Name}: {ex.Message}");
            result = null;
        }

        if (result == null)
        {
            // Sin sonido propio: devolvemos a Windows las pulsaciones (volumen general)
            VolumeLog.Write($"{what} en {ProcessName(pid)}: no tiene sonido propio → volumen general de Windows");
            if (mute) { if (group.Count % 2 == 1) VolumeKeyHook.SendToWindows(VolumeKey.Mute); }
            else for (int n = 0; n < Math.Abs(steps); n++) VolumeKeyHook.SendToWindows(steps > 0 ? VolumeKey.Up : VolumeKey.Down);
            return;
        }

        VolumeLog.Write($"{what} en {ProcessName(pid)}: {result.AppName} → " +
                        $"{(result.Muted ? "silenciado" : $"{Math.Round(result.Volume * 100)} %")} ({sw.ElapsedMilliseconds} ms)");
        if (AppSettings.Current.ShowVolumeOsd)
            _ui.BeginInvoke(() => (_osd ??= new VolumeOsd()).ShowVolume(result));
    }

    private static string ProcessName(int? pid)
    {
        if (pid == null) return "(ninguna ventana)";
        try { using var p = System.Diagnostics.Process.GetProcessById(pid.Value); return $"{p.ProcessName} (pid {pid})"; }
        catch { return $"pid {pid}"; }
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _queue.CompleteAdding();
        _osd?.Close();
    }
}
