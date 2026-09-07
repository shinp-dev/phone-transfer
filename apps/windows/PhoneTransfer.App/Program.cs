namespace PhoneTransfer.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext();
        System.Windows.Forms.Application.Run(context);
    }
}
