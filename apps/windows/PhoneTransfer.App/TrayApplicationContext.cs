namespace PhoneTransfer.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu = new();

    public TrayApplicationContext()
    {
        menu.Items.Add("状態", null, (_, _) => MessageBox.Show(
            "開発中: ペアリング設定前のため、サーバーは停止しています。\nApp 0.1.0 / Build 1 / Protocol 1",
            "Phone Transfer"));
        menu.Items.Add("終了", null, (_, _) => ExitThread());
        icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Phone Transfer — 未設定",
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
