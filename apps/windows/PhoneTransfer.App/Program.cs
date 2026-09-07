namespace PhoneTransfer.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(false, "Local\\PhoneTransfer.App");
        bool ownsInstance;
        try { ownsInstance = instance.WaitOne(0); }
        catch (AbandonedMutexException) { ownsInstance = true; }
        if (!ownsInstance) return;
        try
        {
            ApplicationConfiguration.Initialize();
            using var context = new TrayApplicationContext();
            System.Windows.Forms.Application.Run(context);
        }
        finally { instance.ReleaseMutex(); }
    }
}
