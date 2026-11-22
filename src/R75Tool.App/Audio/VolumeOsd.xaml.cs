using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace R75Tool.App.Audio;

/// <summary>Indicador flotante con el programa y su volumen. Se esconde solo tras un momento.</summary>
public partial class VolumeOsd : Window
{
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    public VolumeOsd()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };

        // Estilos de ventana de Windows: que no robe el foco al juego (NOACTIVATE), que los clics
        // la atraviesen (TRANSPARENT) y que no salga en Alt+Tab (TOOLWINDOW)
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
        };
    }

    public void ShowVolume(AppVolumeResult r)
    {
        AppName.Text = r.AppName;
        int percent = (int)Math.Round(r.Volume * 100);
        Percent.Text = r.Muted ? "Silenciado" : $"{percent} %";
        VolumeIcon.Symbol = r.Muted || percent == 0 ? SymbolRegular.SpeakerMute24 : SymbolRegular.Speaker224;
        Bar.Width = 0;
        Show();
        UpdateLayout();
        // La barra mide lo mismo que su contenedor; la llenamos según el volumen
        if (Bar.Parent is FrameworkElement track) Bar.Width = track.ActualWidth * (r.Muted ? 0 : r.Volume);

        // Arriba en el centro de la pantalla principal (donde no estorba en la mayoría de juegos)
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 40;

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
