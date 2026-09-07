namespace PhoneTransfer.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu = new();
    private readonly ServerWindow window = new();

    public TrayApplicationContext()
    {
        MainForm = window;
        menu.Items.Add("開く", null, (_, _) => { window.Show(); window.Activate(); });
        menu.Items.Add("終了", null, async (_, _) => await window.CloseApplicationAsync());
        icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Phone Transfer",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => { window.Show(); window.Activate(); };
        window.Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            window.Dispose();
        }
        base.Dispose(disposing);
    }
}
