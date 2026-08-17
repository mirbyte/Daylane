using System.Threading;
using Avalonia;

namespace Daylane;

internal static class Program
{
    private const string MutexName = @"Local\Daylane.SingleInstance";
    private const string ActivateEventName = @"Local\Daylane.Activate";

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: false, name: MutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
                    existing.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                }

                return;
            }

            using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            App.RegisterActivationSignal(activateEvent);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
