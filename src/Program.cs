namespace OvpnClient;

public static class Program
{
    public const string AppId = "io.github.ovpnclient.OvpnClient";
    public const string AppName = "OpenVPN Client";

    public static int Main(string[] args)
    {
        var app = Adw.Application.New(AppId, Gio.ApplicationFlags.HandlesOpen);
        MainWindow? window = null;

        app.OnActivate += (_, _) =>
        {
            // Single instance: a second launch just raises the existing window.
            window ??= new MainWindow(app);
            window.Present();
        };

        // `ovpn-client some.ovpn` or "Open With" in the file manager imports the file.
        app.OnOpen += (_, e) =>
        {
            window ??= new MainWindow(app);
            window.Present();
            foreach (var f in e.Files)
                if (f.GetPath() is { } path) window.ImportFile(path);
        };

        var quit = Gio.SimpleAction.New("quit", null);
        quit.OnActivate += (_, _) => window?.RequestClose();
        app.AddAction(quit);
        app.SetAccelsForAction("app.quit", ["<Control>q"]);
        app.SetAccelsForAction("win.import", ["<Control>o"]);

        // Await continuations run on the GLib main loop, so async code can touch widgets.
        // GLib expects argv[0] to be the program name; .NET leaves it out.
        return app.RunWithSynchronizationContext([Environment.GetCommandLineArgs()[0], .. args]);
    }
}
