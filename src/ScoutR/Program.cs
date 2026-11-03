using Velopack;

namespace ScoutR;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/update/uninstall hooks, then returns.
        VelopackApp.Build().Run();

        // A Copy design runs its writing part in a separate ScoutR process (see CopyJob).
        if (args.Length == 2 && args[0] == CopyJob.Arg)
        {
            CopyJob.RunChild(args[1]);
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
