using System.Windows;
using Forms = System.Windows.Forms;

namespace R75Tool.App;

/// <summary>
/// Icono en la bandeja del sistema (junto al reloj). Permite que la app siga funcionando
/// con la ventana cerrada, que es lo que necesitan los perfiles por programa.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _profilesItem;

    public TrayIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/R75Tool;component/Assets/app.ico")).Stream;
        _profilesItem = new Forms.ToolStripMenuItem("Perfiles por programa") { CheckOnClick = true };
        _profilesItem.Click += (_, _) => App.SetProfilesEnabled(_profilesItem.Checked);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir R75 Tool", null, (_, _) => App.ShowMainWindow());
        menu.Items.Add(_profilesItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => App.ExitApp());
        menu.Opening += (_, _) => _profilesItem.Checked = AppSettings.Current.ProfilesEnabled;

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "R75 Tool",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => App.ShowMainWindow();
    }

    /// <summary>Muestra un aviso junto al icono (p. ej. la primera vez que se cierra a la bandeja).</summary>
    public void ShowTip(string title, string text) => _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
