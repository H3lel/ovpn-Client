namespace OvpnClient;

/// <summary>Detail page for one profile: status, connect/disconnect, details and log.</summary>
public sealed class ProfilePage
{
    private readonly MainWindow _owner;
    public Gtk.Widget Widget { get; }
    public event Action<string>? TitleChanged;

    private Profile? _profile;
    private VpnSession? _session;
    private LogBuffer? _log;
    private OvpnConfig? _config;

    private readonly Gtk.Image _icon;
    private readonly Gtk.Label _title;
    private readonly Gtk.Label _status;
    private readonly Gtk.Button _connect;
    private readonly Adw.ActionRow _server, _protocol, _vpnIp, _remoteIp, _duration, _traffic;
    private readonly Gtk.TextView _logView;
    private readonly Gtk.ScrolledWindow _logScroll;
    private readonly Gtk.MenuButton _menuButton;
    private bool _ticking;

    public ProfilePage(MainWindow owner)
    {
        _owner = owner;

        // ---- status hero
        _icon = Gtk.Image.New();
        _icon.SetPixelSize(96);
        _icon.SetMarginBottom(12);

        _title = Gtk.Label.New(null);
        _title.AddCssClass("title-1");
        _title.SetWrap(true);
        _title.SetJustify(Gtk.Justification.Center);

        _status = Gtk.Label.New(null);
        _status.AddCssClass("dim-label");
        _status.SetWrap(true);
        _status.SetJustify(Gtk.Justification.Center);

        _connect = Gtk.Button.NewWithMnemonic("_Connect");
        _connect.AddCssClass("pill");
        _connect.SetHalign(Gtk.Align.Center);
        _connect.SetMarginTop(18);
        _connect.SetSizeRequest(200, -1);
        _connect.OnClicked += (_, _) => _ = ToggleAsync();

        var hero = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        hero.SetMarginTop(24);
        hero.SetMarginBottom(12);
        hero.Append(_icon);
        hero.Append(_title);
        hero.Append(_status);
        hero.Append(_connect);

        // ---- details
        _server = InfoRow("Server");
        _protocol = InfoRow("Protocol");
        _vpnIp = InfoRow("VPN Address");
        _remoteIp = InfoRow("Server Address");
        _duration = InfoRow("Connected For");
        _traffic = InfoRow("Traffic");

        var details = Adw.PreferencesGroup.New();
        details.SetTitle("Details");
        foreach (var r in new[] { _server, _protocol, _vpnIp, _remoteIp, _duration, _traffic })
            details.Add(r);

        // ---- log
        _logView = Gtk.TextView.New();
        _logView.SetEditable(false);
        _logView.SetCursorVisible(false);
        _logView.SetMonospace(true);
        _logView.SetWrapMode(Gtk.WrapMode.WordChar);
        _logView.SetTopMargin(8);
        _logView.SetBottomMargin(8);
        _logView.SetLeftMargin(10);
        _logView.SetRightMargin(10);
        _logView.AddCssClass("inline");

        _logScroll = Gtk.ScrolledWindow.New();
        _logScroll.SetChild(_logView);
        _logScroll.SetMinContentHeight(220);
        _logScroll.SetMaxContentHeight(220);
        _logScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);

        var logFrame = Gtk.Frame.New(null);
        logFrame.AddCssClass("card");
        logFrame.SetChild(_logScroll);

        var copyLog = Gtk.Button.NewFromIconName("edit-copy-symbolic");
        copyLog.AddCssClass("flat");
        copyLog.SetTooltipText("Copy Log");
        copyLog.SetValign(Gtk.Align.Center);
        copyLog.OnClicked += (_, _) =>
        {
            if (_log == null) return;
            copyLog.GetClipboard().SetText(_log.Text);
            _owner.Toast("Log copied");
        };
        var clearLog = Gtk.Button.NewFromIconName("edit-clear-all-symbolic");
        clearLog.AddCssClass("flat");
        clearLog.SetTooltipText("Clear Log");
        clearLog.SetValign(Gtk.Align.Center);
        clearLog.OnClicked += (_, _) => _log?.Clear();
        var logButtons = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        logButtons.Append(copyLog);
        logButtons.Append(clearLog);

        var logGroup = Adw.PreferencesGroup.New();
        logGroup.SetTitle("Log");
        logGroup.SetHeaderSuffix(logButtons);
        logGroup.Add(logFrame);

        // ---- layout
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 24);
        column.SetMarginStart(12);
        column.SetMarginEnd(12);
        column.SetMarginBottom(24);
        column.Append(hero);
        column.Append(details);
        column.Append(logGroup);

        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(600);
        clamp.SetChild(column);

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetChild(clamp);
        scroll.SetVexpand(true);

        // ---- header with profile menu
        var actions = Gio.SimpleActionGroup.New();
        AddAction(actions, "rename", () => { if (_profile != null) _ = _owner.RenameAsync(_profile); });
        AddAction(actions, "folder", () => { if (_profile != null) _owner.OpenFolder(_profile); });
        AddAction(actions, "remove", () => { if (_profile != null) _ = _owner.RemoveAsync(_profile); });
        AddAction(actions, "forget", () => { if (_profile != null) _ = _owner.ForgetPasswordAsync(_profile); });

        var menu = Gio.Menu.New();
        var section1 = Gio.Menu.New();
        section1.Append("_Rename…", "profile.rename");
        section1.Append("Open Config _Folder", "profile.folder");
        section1.Append("_Forget Saved Password", "profile.forget");
        var section2 = Gio.Menu.New();
        section2.Append("Re_move…", "profile.remove");
        menu.AppendSection(null, section1);
        menu.AppendSection(null, section2);

        _menuButton = Gtk.MenuButton.New();
        _menuButton.SetIconName("view-more-symbolic");
        _menuButton.SetTooltipText("Connection Menu");
        _menuButton.SetMenuModel(menu);

        var header = Adw.HeaderBar.New();
        header.PackEnd(_menuButton);

        var view = Adw.ToolbarView.New();
        view.AddTopBar(header);
        view.SetContent(scroll);
        view.InsertActionGroup("profile", actions);
        Widget = view;
    }

    private static void AddAction(Gio.SimpleActionGroup group, string name, Action handler)
    {
        var a = Gio.SimpleAction.New(name, null);
        a.OnActivate += (_, _) => handler();
        group.AddAction(a);
    }

    private static Adw.ActionRow InfoRow(string title)
    {
        var row = Adw.ActionRow.New();
        row.SetTitle(title);
        row.AddCssClass("property"); // small title above a selectable value
        row.SetSubtitleSelectable(true);
        return row;
    }

    public void Show(Profile profile, VpnSession session, LogBuffer log)
    {
        if (_log != null) _log.Appended -= ScrollLogToEnd;
        _profile = profile;
        _session = session;
        _log = log;
        _log.Appended += ScrollLogToEnd;
        _logView.SetBuffer(log.Buffer);
        try { _config = OvpnConfig.Parse(profile.ConfigPath); }
        catch (IOException) { _config = null; }
        TitleChanged?.Invoke(profile.Name);
        Refresh();
        ScrollLogToEnd();
    }

    public void Refresh()
    {
        if (_profile == null || _session == null) return;
        var s = _session;

        _title.SetText(_profile.Name);
        _icon.SetFromIconName(MainWindow.StatusIcon(s.State));
        foreach (var c in new[] { "success", "accent", "warning", "dim-label", "error" }) _icon.RemoveCssClass(c);
        _icon.AddCssClass(s.State switch
        {
            VpnState.Connected => "success",
            VpnState.Reconnecting => "warning",
            VpnState.Disconnected when s.LastError != null => "error",
            VpnState.Disconnected => "dim-label",
            _ => "accent",
        });

        _status.SetText(s.State == VpnState.Disconnected
            ? s.LastError ?? "Not connected"
            : s.StateDetail ?? s.State.ToString());
        _status.RemoveCssClass("error");
        if (s.State == VpnState.Disconnected && s.LastError != null) _status.AddCssClass("error");

        _connect.RemoveCssClass("suggested-action");
        _connect.RemoveCssClass("destructive-action");
        switch (s.State)
        {
            case VpnState.Disconnected:
                _connect.SetLabel("_Connect");
                _connect.AddCssClass("suggested-action");
                _connect.SetSensitive(true);
                break;
            case VpnState.Connected:
                _connect.SetLabel("_Disconnect");
                _connect.AddCssClass("destructive-action");
                _connect.SetSensitive(true);
                break;
            case VpnState.Disconnecting:
                _connect.SetLabel("Disconnecting…");
                _connect.SetSensitive(false);
                break;
            default:
                _connect.SetLabel("_Cancel");
                _connect.SetSensitive(true);
                break;
        }

        _menuButton.SetSensitive(true);
        _server.SetSubtitle(_config?.RemoteHost != null ? $"{_config.RemoteHost}:{_config.RemotePort}" : "—");
        _protocol.SetSubtitle(_config == null ? "—"
            : $"{(_config.Protocol ?? "udp").Replace("-client", "").ToUpperInvariant()}"
              + (_config.Device != null ? $" · {_config.Device}" : ""));

        bool up = s.State == VpnState.Connected;
        _vpnIp.SetSubtitle(up ? string.Join(", ", new[] { s.LocalIp, s.LocalIp6 }.Where(x => x != null)) is { Length: > 0 } ips ? ips : "—" : "—");
        _remoteIp.SetSubtitle(up && s.RemoteIp != null ? s.RemoteIp : "—");
        _traffic.SetSubtitle(s.State == VpnState.Disconnected && s.BytesIn == 0 ? "—"
            : $"↓ {FormatBytes(s.BytesIn)}   ↑ {FormatBytes(s.BytesOut)}");
        UpdateDuration();

        if (up && !_ticking) _ = TickAsync();
    }

    private void UpdateDuration()
    {
        var since = _session?.ConnectedSince;
        if (_session?.State != VpnState.Connected || since == null) { _duration.SetSubtitle("—"); return; }
        var d = DateTime.Now - since.Value;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        _duration.SetSubtitle(d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d:mm\\:ss}" : d.ToString(@"m\:ss"));
    }

    private async Task TickAsync()
    {
        _ticking = true;
        while (_session?.State == VpnState.Connected)
        {
            UpdateDuration();
            await Task.Delay(1000);
        }
        _ticking = false;
    }

    private async Task ToggleAsync()
    {
        if (_session == null) return;
        if (_session.State == VpnState.Disconnected)
        {
            var missing = ProfileStore.MissingFiles(_session.Profile);
            if (missing.Count > 0)
            {
                _owner.Toast($"Missing files: {string.Join(", ", missing)}. Use “Open Config Folder” to add them.");
                return;
            }
            _log?.Append($"--- Connecting {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            await _session.StartAsync();
        }
        else
        {
            await _session.DisconnectAsync();
        }
    }

    private void ScrollLogToEnd()
    {
        // Wait one frame so the new text is laid out before scrolling.
        _ = ScrollSoon();
        async Task ScrollSoon()
        {
            await Task.Delay(30);
            var adj = _logScroll.GetVadjustment();
            if (adj != null) adj.SetValue(adj.GetUpper() - adj.GetPageSize());
        }
    }

    internal static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.0} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.00} GB",
    };
}
