using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyAltium.Host;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Altium Toolbar'dan "--show" ile çağrıldığında WPF çatısını hiç yüklemeden
        // doğrudan mevcut çalışan örneğin penceresini uyandır (< 5 ms).
        if (EventWaitHandle.TryOpenExisting(App.SingleInstanceEventName, out EventWaitHandle? existingEvent))
        {
            existingEvent.Set();
            existingEvent.Dispose();
            return;
        }

        RunWpfApplication();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunWpfApplication()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
