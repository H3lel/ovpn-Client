using System.Diagnostics;

namespace OvpnClient;

/// <summary>
/// The main window: a sidebar of imported profiles and a detail page for the
/// selected one (status, connect button, details, live log). Built with
/// libadwaita widgets so it follows the GNOME HIG and the system theme.
/// </summary>
public sealed class MainWindow
{
    private readonly Adw.Application _app;
    private readonly Adw.ApplicationWindow _window;
    private readonly ProfileStore _store = new();
    private readonly Dictionary<string, VpnSession> _sessions = new();
    private readonly Dictionary<string, LogBuffer> _logs = new();

    private readonly Adw.ToastOverlay _toasts;
    private readonly Gtk.Stack _rootStack;
    private readonly Adw.NavigationSplitView _split;
    private readonly Gtk.ListBox _list;
    private readonly ProfilePage _page;
    private Profile? _selected;
    private bool _closingConfirmed;

    public MainWindow(Adw.Application app)
    {
        _app = app;
        _window = Adw.ApplicationWindow.New(app);
        _window.SetTitle(Program.AppName);
        _window.SetDefaultSize(860, 640);
        _window.SetSizeRequest(360, 400);
        _window.SetIconName("network-vpn-symbolic");

        AddWindowActions();

        // ---- sidebar
        _list = Gtk.ListBox.New();
        _list.AddCssClass("navigation-sidebar");
        _list.SetSelectionMode(Gtk.SelectionMode.Single);
        _list.OnRowActivated += (_, e) => SelectRow(e.Row);
        _list.OnRowSelected += (_, e) => { if (e.Row != null) SelectRow(e.Row); };

        var sidebarScroll = Gtk.ScrolledWindow.New();
        sidebarScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        sidebarScroll.SetChild(_list);
        sidebarScroll.SetVexpand(true);

        var importButton = Gtk.Button.NewFromIconName("list-add-symbolic");
        importButton.SetTooltipText("Import Config File (Ctrl+O)");
        importButton.SetActionName("win.import");

        var menu = Gio.Menu.New();
        menu.Append("_Import Config File…", "win.import");
        menu.Append("_About OpenVPN Client", "win.about");
        var menuButton = Gtk.MenuButton.New();
        menuButton.SetIconName("open-menu-symbolic");
        menuButton.SetTooltipText("Main Menu");
        menuButton.SetMenuModel(menu);
        menuButton.SetPrimary(true);

        var sidebarHeader = Adw.HeaderBar.New();
        sidebarHeader.PackStart(importButton);
        sidebarHeader.PackEnd(menuButton);

        var sidebarView = Adw.ToolbarView.New();
        sidebarView.AddTopBar(sidebarHeader);
        sidebarView.SetContent(sidebarScroll);

        var sidebarPage = Adw.NavigationPage.New(sidebarView, "Connections");

        // ---- content
        _page = new ProfilePage(this);
        var contentPage = Adw.NavigationPage.New(_page.Widget, "Connection");
        _page.TitleChanged += t => contentPage.SetTitle(t);

        _split = Adw.NavigationSplitView.New();
        _split.SetSidebar(sidebarPage);
        _split.SetContent(contentPage);
        _split.SetMinSidebarWidth(220);
        _split.SetMaxSidebarWidth(300);

        // ---- empty state
        var emptyImport = Gtk.Button.NewWithMnemonic("_Import Config File…");
        emptyImport.AddCssClass("pill");
        emptyImport.AddCssClass("suggested-action");
        emptyImport.SetHalign(Gtk.Align.Center);
        emptyImport.SetActionName("win.import");

        var empty = Adw.StatusPage.New();
        empty.SetIconName("network-vpn-symbolic");
        empty.SetTitle("No VPN Connections");
        empty.SetDescription("Import an OpenVPN config file (.ovpn) to get started.\nYou can also drop one onto this window.");
        empty.SetChild(emptyImport);

        var emptyHeader = Adw.HeaderBar.New();
        var emptyMenu = Gtk.MenuButton.New();
        emptyMenu.SetIconName("open-menu-symbolic");
        emptyMenu.SetTooltipText("Main Menu");
        emptyMenu.SetMenuModel(menu);
        emptyHeader.PackEnd(emptyMenu);
        var emptyView = Adw.ToolbarView.New();
        emptyView.AddTopBar(emptyHeader);
        emptyView.SetContent(empty);

        _rootStack = Gtk.Stack.New();
        _rootStack.SetTransitionType(Gtk.StackTransitionType.Crossfade);
        _rootStack.AddNamed(emptyView, "empty");
        _rootStack.AddNamed(_split, "profiles");

        _toasts = Adw.ToastOverlay.New();
        _toasts.SetChild(_rootStack);
        _window.SetContent(_toasts);

        // Collapse to a single pane on narrow windows (phones, tiled halves).
        var bp = Adw.Breakpoint.New(Adw.BreakpointCondition.Parse("max-width: 560sp"));
        bp.AddSetter(_split, "collapsed", new GObject.Value(true));
        _window.AddBreakpoint(bp);

        SetupDragAndDrop();

        _window.OnCloseRequest += (_, _) => OnCloseRequest();

        RebuildList();
        _ = ReattachRunningAsync();
    }

    public Gtk.Window Window => _window;
    public void Present() => _window.Present();
    public void RequestClose() => _window.Close();

    // ------------------------------------------------------------------ actions

    private void AddWindowActions()
    {
        var import = Gio.SimpleAction.New("import", null);
        import.OnActivate += (_, _) => _ = ImportViaDialogAsync();
        _window.AddAction(import);

        var about = Gio.SimpleAction.New("about", null);
        about.OnActivate += (_, _) => ShowAbout();
        _window.AddAction(about);
    }

    private void ShowAbout()
    {
        var about = Adw.AboutDialog.New();
        about.SetApplicationName(Program.AppName);
        about.SetApplicationIcon("network-vpn-symbolic");
        about.SetVersion(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        about.SetComments("A GNOME client for OpenVPN config files.");
        about.SetDevelopers(["OpenVPN Client contributors"]);
        about.SetLicenseType(Gtk.License.MitX11);
        about.Present(_window);
    }

    // ------------------------------------------------------------------ import

    private async Task ImportViaDialogAsync()
    {
        var filter = Gtk.FileFilter.New();
        filter.SetName("OpenVPN configs (*.ovpn, *.conf)");
        filter.AddPattern("*.ovpn");
        filter.AddPattern("*.OVPN");
        filter.AddPattern("*.conf");
        var all = Gtk.FileFilter.New();
        all.SetName("All files");
        all.AddPattern("*");

        var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
        filters.Append(filter);
        filters.Append(all);

        var dialog = Gtk.FileDialog.New();
        dialog.SetTitle("Import OpenVPN Config");
        dialog.SetFilters(filters);
        dialog.SetDefaultFilter(filter);

        Gio.File? file;
        try
        {
            file = await dialog.OpenAsync(_window);
        }
        catch (GLib.GException)
        {
            return; // dismissed
        }
        var path = file?.GetPath();
        if (path != null) ImportFile(path);
    }

    public void ImportFile(string path)
    {
        Profile profile;
        try
        {
            profile = _store.Import(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Could not import {Path.GetFileName(path)}: {e.Message}");
            return;
        }

        RebuildList(select: profile);
        var missing = ProfileStore.MissingFiles(profile);
        if (missing.Count > 0)
            Toast($"Imported “{profile.Name}”, but these files were not found: {string.Join(", ", missing)}");
        else
            Toast($"Imported “{profile.Name}”");
    }

    private void SetupDragAndDrop()
    {
        var drop = Gtk.DropTarget.New(Gdk.FileList.GetGType(), Gdk.DragAction.Copy);
        drop.OnDrop += (_, e) =>
        {
            int imported = 0;
            foreach (var p in Native.FileListPaths(e.Value.GetBoxed()))
            {
                if (File.Exists(p)) { ImportFile(p); imported++; }
            }
            return imported > 0;
        };
        _window.AddController(drop);
    }

    // ------------------------------------------------------------------ list

    private readonly Dictionary<Gtk.ListBoxRow, Profile> _rowProfiles = new();
    private readonly Dictionary<string, Gtk.Image> _rowStatusIcons = new();

    private void RebuildList(Profile? select = null)
    {
        select ??= _selected;
        _list.RemoveAll();
        _rowProfiles.Clear();
        _rowStatusIcons.Clear();

        Gtk.ListBoxRow? toSelect = null;
        foreach (var p in _store.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = Adw.ActionRow.New();
            row.SetTitle(GLib.Functions.MarkupEscapeText(p.Name, -1));
            row.SetSubtitle(GLib.Functions.MarkupEscapeText(SafeServerSummary(p), -1));
            row.SetSubtitleLines(1);

            var icon = Gtk.Image.NewFromIconName(StatusIcon(SessionFor(p).State));
            _rowStatusIcons[p.Id] = icon;
            row.AddPrefix(icon);

            _list.Append(row);
            _rowProfiles[row] = p;
            if (select != null && p.Id == select.Id) toSelect = row;
        }

        _rootStack.SetVisibleChildName(_store.Profiles.Count == 0 ? "empty" : "profiles");

        if (_store.Profiles.Count == 0)
        {
            _selected = null;
            return;
        }
        toSelect ??= _list.GetRowAtIndex(0);
        _list.SelectRow(toSelect);
        if (toSelect != null) ShowProfile(_rowProfiles[toSelect]);
    }

    private void SelectRow(Gtk.ListBoxRow row)
    {
        if (!_rowProfiles.TryGetValue(row, out var p)) return;
        ShowProfile(p);
        _split.SetShowContent(true); // on narrow windows, navigate to the detail page
    }

    private void ShowProfile(Profile p)
    {
        _selected = p;
        _page.Show(p, SessionFor(p), LogFor(p));
    }

    private static string SafeServerSummary(Profile p)
    {
        try { return OvpnConfig.Parse(p.ConfigPath).ServerSummary; }
        catch (IOException) { return "Config file missing"; }
    }

    // ------------------------------------------------------------------ sessions

    internal VpnSession SessionFor(Profile p)
    {
        if (_sessions.TryGetValue(p.Id, out var s)) return s;
        s = new VpnSession(p);
        var log = LogFor(p);
        s.Log += line => log.Append(line);
        s.Changed += () => OnSessionChanged(s);
        s.Ended += () => OnSessionEnded(s);
        s.ProfileChanged += () => _store.Save();
        s.CredentialsRequested += req => CredentialsDialog.AskAsync(_window, p, req, _store);
        s.OpenUrlRequested += url =>
        {
            log.Append("Opening browser for sign-in: " + url);
            OpenUri(url);
        };
        _sessions[p.Id] = s;
        return s;
    }

    internal LogBuffer LogFor(Profile p)
    {
        if (!_logs.TryGetValue(p.Id, out var l)) _logs[p.Id] = l = new LogBuffer();
        return l;
    }

    private VpnState _lastNotified = VpnState.Disconnected;

    private void OnSessionChanged(VpnSession s)
    {
        if (_rowStatusIcons.TryGetValue(s.Profile.Id, out var icon))
            icon.SetFromIconName(StatusIcon(s.State));
        if (_selected?.Id == s.Profile.Id)
            _page.Refresh();

        if (s.State == VpnState.Connected && _lastNotified != VpnState.Connected)
            Toast($"Connected to {s.Profile.Name}");
        _lastNotified = s.State;
    }

    private void OnSessionEnded(VpnSession s)
    {
        if (s.LastError != null)
            Toast($"{s.Profile.Name}: {s.LastError}");
        OnSessionChanged(s);
    }

    internal static string StatusIcon(VpnState state) => state switch
    {
        VpnState.Connected => "network-vpn-symbolic",
        VpnState.Disconnected => "network-vpn-disconnected-symbolic",
        VpnState.Reconnecting => "network-vpn-no-route-symbolic",
        _ => "network-vpn-acquiring-symbolic",
    };

    /// <summary>Picks up OpenVPN processes left running when the app was last closed.</summary>
    private async Task ReattachRunningAsync()
    {
        foreach (var p in _store.Profiles.ToList())
        {
            var s = SessionFor(p);
            if (await s.TryAttachAsync())
                LogFor(p).Append("Reattached to running OpenVPN.");
        }
    }

    // ------------------------------------------------------------------ profile actions (from the page)

    internal async Task RenameAsync(Profile p)
    {
        var name = await Dialogs.AskTextAsync(_window, "Rename Connection", null, "Name", p.Name, "_Rename");
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == p.Name) return;
        _store.Rename(p, name);
        RebuildList(select: p);
    }

    internal async Task RemoveAsync(Profile p)
    {
        if (SessionFor(p).IsActive)
        {
            Toast("Disconnect before removing this connection.");
            return;
        }
        var ok = await Dialogs.ConfirmAsync(_window, $"Remove “{p.Name}”?",
            "The imported config and its certificate files will be deleted from this app. The original file is not touched.",
            "_Remove", destructive: true);
        if (!ok) return;
        _store.Remove(p);
        _ = Keyring.ClearAsync(p.Id);
        _sessions.Remove(p.Id);
        _logs.Remove(p.Id);
        if (_selected?.Id == p.Id) _selected = null;
        RebuildList();
        Toast($"Removed “{p.Name}”");
    }

    internal async Task ForgetPasswordAsync(Profile p)
    {
        await Keyring.ClearAsync(p.Id);
        if (p.SavePassword)
        {
            p.SavePassword = false;
            _store.Save();
        }
        Toast($"Saved password for “{p.Name}” forgotten");
    }

    internal void OpenFolder(Profile p) => OpenUri(new Uri(p.Directory).AbsoluteUri);

    internal void Toast(string message)
    {
        var t = Adw.Toast.New(GLib.Functions.MarkupEscapeText(message, -1));
        t.SetTimeout(4);
        _toasts.AddToast(t);
    }

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", uri) { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    // ------------------------------------------------------------------ closing

    private bool OnCloseRequest()
    {
        if (_closingConfirmed) return false;
        var active = _sessions.Values.Where(s => s.IsActive).ToList();
        if (active.Count == 0) return false;

        _ = ConfirmCloseAsync(active);
        return true; // keep the window open until the user decides
    }

    private async Task ConfirmCloseAsync(List<VpnSession> active)
    {
        var names = string.Join(", ", active.Select(s => s.Profile.Name));
        var choice = await Dialogs.ChooseAsync(_window,
            active.Count == 1 ? "VPN Still Connected" : "VPNs Still Connected",
            $"{names} will stay connected in the background if you keep it running. Reopen this app to manage it.",
            ("cancel", "_Cancel", Adw.ResponseAppearance.Default),
            ("keep", "_Keep Connected", Adw.ResponseAppearance.Default),
            ("disconnect", "_Disconnect", Adw.ResponseAppearance.Destructive));

        if (choice == "cancel" || choice == null) return;
        if (choice == "disconnect")
        {
            await Task.WhenAll(active.Select(s => s.DisconnectAsync()));
            // Give OpenVPN a moment to tear the tunnel down cleanly.
            for (int i = 0; i < 50 && active.Any(s => s.IsActive); i++)
                await Task.Delay(100);
        }
        _closingConfirmed = true;
        _window.Close();
    }
}
