using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace OvpnClient;

public enum VpnState
{
    Disconnected,
    Authorizing,     // waiting for the polkit password prompt
    Connecting,
    Authenticating,
    Configuring,
    Connected,
    Reconnecting,
    Disconnecting,
}

public enum CredentialKind { UserPass, PasswordOnly }

public sealed record CredentialRequest(
    CredentialKind Kind,
    string Type,              // OpenVPN's name for it: "Auth", "Private Key", "HTTP Proxy"…
    bool PreviousFailed,
    string? ChallengeText,    // static challenge prompt, if the server asks for an extra code
    bool ChallengeEcho,
    string? LastUsername);    // what was typed last time, to prefill after a failed attempt

public sealed record Credentials(string Username, string Password, string? ChallengeResponse);

/// <summary>
/// One OpenVPN process, driven through its management interface (a unix socket only
/// our user may connect to). OpenVPN runs as root via pkexec; everything else —
/// credential prompts, status, stopping it — goes over the socket.
///
/// All events are raised on the GLib main loop because the app runs with a GLib
/// SynchronizationContext and every await here resumes on it.
/// </summary>
public sealed class VpnSession
{
    public Profile Profile { get; }
    public VpnState State { get; private set; } = VpnState.Disconnected;
    public string? StateDetail { get; private set; }
    public string? LocalIp { get; private set; }
    public string? LocalIp6 { get; private set; }
    public string? RemoteIp { get; private set; }
    public long BytesIn { get; private set; }
    public long BytesOut { get; private set; }
    public DateTime? ConnectedSince { get; private set; }
    public string? LastError { get; private set; }
    public bool IsActive => State != VpnState.Disconnected;

    public event Action? Changed;
    public event Action<string>? Log;
    public event Func<CredentialRequest, Task<Credentials?>>? CredentialsRequested;
    public event Action<string>? OpenUrlRequested;
    public event Action? Ended;
    /// <summary>Something about the profile was learned (e.g. a routing fix) and should be saved.</summary>
    public event Action? ProfileChanged;

    private Process? _process;
    private Socket? _socket;
    private StreamWriter? _writer;
    private bool _stopRequested;
    private bool _authFailed;
    private string? _lastUsername;
    private readonly HashSet<string> _failedTypes = new();
    private string? _gatewayFix;
    private bool _restartAfterExit;
    private readonly List<string> _earlyOutput = new();
    private string LogFilePath => Path.Combine(ProfileStore.RuntimeDir, Profile.Id + ".log");

    public VpnSession(Profile profile) => Profile = profile;

    // ---------------------------------------------------------------- start / attach

    public async Task StartAsync()
    {
        if (IsActive) return;
        Reset();
        SetState(VpnState.Authorizing, "Waiting for authorization…");

        if (File.Exists(Profile.SocketPath))
        {
            // Left behind by an openvpn that died without cleaning up.
            try { File.Delete(Profile.SocketPath); } catch (IOException) { }
        }

        try
        {
            File.WriteAllText(LogFilePath, "");
            File.SetUnixFileMode(LogFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(LogFilePath); } catch (Exception) { } // root-owned leftover from an older version
        }

        var openvpn = FindOpenVpn();
        if (openvpn == null)
        {
            Fail("OpenVPN is not installed. Install it with: sudo apt install openvpn");
            return;
        }

        bool direct = Environment.GetEnvironmentVariable("OVPN_CLIENT_NO_PKEXEC") == "1";
        // setsid puts OpenVPN in its own session, so closing the terminal (or anything else
        // that signals our process group) doesn't take the tunnel down with the app.
        var psi = new ProcessStartInfo("setsid")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = Profile.Directory,
        };
        psi.ArgumentList.Add("--wait"); // pass the exit code through even if setsid has to fork
        if (!direct) psi.ArgumentList.Add("pkexec");
        psi.ArgumentList.Add(openvpn);
        foreach (var a in new[]
        {
            "--cd", Profile.Directory,
            "--config", Profile.ConfigPath,
            "--management", Profile.SocketPath, "unix",
            "--management-client-user", Environment.UserName,
            "--management-hold",
            "--management-query-passwords",
            "--auth-retry", "interact",
            // Logging to a file (not our pipes) lets OpenVPN keep running if this app quits.
            // The file is created by us beforehand and appended to, so it stays ours and readable
            // (a file OpenVPN creates itself would be root-only).
            "--log-append", LogFilePath,
        })
            psi.ArgumentList.Add(a);

        if (Profile.RouteGatewayOverride is { } gw)
        {
            psi.ArgumentList.Add("--pull-filter");
            psi.ArgumentList.Add("ignore");
            psi.ArgumentList.Add("route-gateway");
            psi.ArgumentList.Add("--route-gateway");
            psi.ArgumentList.Add(gw);
            Log?.Invoke($"Using gateway {gw} inside the VPN subnet (the server's pushed gateway is unreachable).");
        }

        try
        {
            if (OvpnConfig.Parse(Profile.ConfigPath).LegacyCipherToOffer is { } legacy)
            {
                psi.ArgumentList.Add("--data-ciphers");
                psi.ArgumentList.Add("DEFAULT:" + legacy);
                psi.ArgumentList.Add("--data-ciphers-fallback");
                psi.ArgumentList.Add(legacy);
                Log?.Invoke($"Config uses legacy cipher {legacy}; offering it to the server for compatibility.");
            }
        }
        catch (IOException) { }

        try
        {
            _process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Fail($"Could not start {(direct ? "openvpn" : "pkexec")}: {e.Message}");
            return;
        }
        if (_process == null) { Fail("Could not start OpenVPN."); return; }

        _process.StandardInput.Close();
        _ = CollectEarlyOutput(_process.StandardOutput);
        _ = CollectEarlyOutput(_process.StandardError);
        var exited = _process.WaitForExitAsync();

        // Wait for openvpn to open its management socket. There's no timeout while the
        // process is alive: the user may still be typing their password into polkit.
        // If the user cancels meanwhile we keep waiting: OpenVPN (root) blocks on the hold
        // until we connect, and telling it to quit over the socket is the only way to stop it.
        while (true)
        {
            if (exited.IsCompleted)
            {
                await OnProcessExitedEarly();
                return;
            }
            if (File.Exists(Profile.SocketPath) && await TryConnectSocket())
                break;
            if (State == VpnState.Authorizing && File.Exists(Profile.SocketPath))
                SetState(VpnState.Connecting, "Starting OpenVPN…");
            await Task.WhenAny(exited, Task.Delay(200));
        }

        if (!_stopRequested)
            SetState(VpnState.Connecting, "Starting OpenVPN…");

        if (_stopRequested)
        {
            await SendAsync("signal SIGTERM");
            await ReadLoop();
            return;
        }

        await SendAsync("state on");
        await SendAsync("bytecount 2");
        await SendAsync("log on");
        await SendAsync("hold release");
        await ReadLoop();
    }

    /// <summary>Reconnects to an OpenVPN this app started earlier and that is still running.</summary>
    public async Task<bool> TryAttachAsync()
    {
        if (IsActive || !File.Exists(Profile.SocketPath)) return false;
        Reset();
        if (!await TryConnectSocket())
        {
            try { File.Delete(Profile.SocketPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
        SetState(VpnState.Connecting, "Reattaching…");
        await SendAsync("state");       // current state, answered with a CSV line + END
        await SendAsync("state on");
        await SendAsync("bytecount 2");
        await SendAsync("log on all");  // recent history, then live
        await SendAsync("hold release"); // harmless if not held
        _ = ReadLoop();
        return true;
    }

    // ---------------------------------------------------------------- stop

    public async Task DisconnectAsync()
    {
        if (!IsActive) return;
        _stopRequested = true;
        if (_writer == null)
        {
            // Still at the polkit prompt / not up yet; StartAsync handles it once the socket appears.
            SetState(VpnState.Disconnecting, "Cancelling…");
            return;
        }
        SetState(VpnState.Disconnecting, "Disconnecting…");
        await SendAsync("signal SIGTERM");
    }

    // ---------------------------------------------------------------- socket I/O

    private async Task<bool> TryConnectSocket()
    {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(Profile.SocketPath));
        }
        catch (SocketException)
        {
            sock.Dispose();
            return false;
        }
        _socket = sock;
        var stream = new NetworkStream(sock, ownsSocket: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        _reader = new StreamReader(stream, Encoding.UTF8);
        return true;
    }

    private StreamReader? _reader;

    private async Task SendAsync(string command)
    {
        if (_writer == null) return;
        try { await _writer.WriteLineAsync(command); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
    }

    private async Task ReadLoop()
    {
        var reader = _reader!;
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                await HandleLine(line.TrimEnd('\r'));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
        finally
        {
            _writer = null;
            _reader = null;
            _socket?.Dispose();
            _socket = null;
        }
        await Finish();
    }

    private async Task HandleLine(string line)
    {
        if (line.StartsWith('>'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) return;
            var kind = line[1..colon];
            var body = line[(colon + 1)..];
            switch (kind)
            {
                case "STATE": ApplyState(body); break;
                case "LOG":
                    EmitLogCsv(body);
                    if (body.Contains("PUSH_REPLY,")) InspectPushReply(body);
                    break;
                case "BYTECOUNT": ApplyBytes(body); break;
                case "PASSWORD": await HandlePassword(body); break;
                case "FATAL":
                    LastError = body;
                    Log?.Invoke("FATAL: " + body);
                    break;
                case "INFOMSG":
                    if (body.StartsWith("WEB_AUTH:") || body.StartsWith("OPEN_URL:"))
                    {
                        var url = body[(body.IndexOf(':') + 1)..];
                        url = url[(url.IndexOf(':') + 1)..]; // WEB_AUTH:flags:url
                        if (url.StartsWith("http")) OpenUrlRequested?.Invoke(url);
                    }
                    break;
                case "HOLD":
                    // --management-hold applies to every restart (e.g. after an auth failure),
                    // not just startup.
                    if (!_stopRequested) await SendAsync("hold release");
                    break;
                case "INFO": Log?.Invoke(body); break;
            }
            return;
        }

        if (line.StartsWith("ERROR:"))
        {
            Log?.Invoke("Management: " + line);
            return;
        }
        if (line.StartsWith("SUCCESS:") || line == "END")
            return;

        // History lines from "state" / "log on all": "time,FIELD,..."
        var parts = line.Split(',', 3);
        if (parts.Length >= 2 && long.TryParse(parts[0], out _))
        {
            if (parts[1].Length > 2 && parts[1].All(c => char.IsUpper(c) || c == '_'))
                ApplyState(line);
            else
                EmitLogCsv(line);
        }
    }

    // ---------------------------------------------------------------- protocol handlers

    private void ApplyState(string csv)
    {
        // time,STATE,desc,local_ip,remote_ip,remote_port,local_addr,local_port,local_ipv6
        var f = csv.Split(',');
        if (f.Length < 2) return;
        string desc = f.Length > 2 ? f[2] : "";
        string Field(int i) => f.Length > i ? f[i] : "";

        switch (f[1])
        {
            case "CONNECTING":
            case "RESOLVE":
            case "TCP_CONNECT":
                SetState(VpnState.Connecting, "Connecting to server…");
                break;
            case "WAIT":
                SetState(VpnState.Authenticating, "Waiting for server…");
                break;
            case "AUTH":
                SetState(VpnState.Authenticating, "Authenticating…");
                break;
            case "AUTH_PENDING":
                SetState(VpnState.Authenticating, "Waiting for login to finish…");
                break;
            case "GET_CONFIG":
                SetState(VpnState.Configuring, "Getting configuration…");
                break;
            case "ASSIGN_IP":
                SetState(VpnState.Configuring, "Assigning address…");
                break;
            case "ADD_ROUTES":
                SetState(VpnState.Configuring, "Adding routes…");
                break;
            case "CONNECTED":
                LocalIp = NullIfEmpty(Field(3));
                RemoteIp = NullIfEmpty(Field(4));
                LocalIp6 = NullIfEmpty(Field(8));
                if (long.TryParse(f[0], out var t))
                    ConnectedSince = DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;
                LastError = null;
                _authFailed = false;
                if (desc == "ROUTE_ERROR" && _gatewayFix != null && Profile.RouteGatewayOverride != _gatewayFix)
                {
                    Profile.RouteGatewayOverride = _gatewayFix;
                    ProfileChanged?.Invoke();
                    Log?.Invoke($"The server pushed a gateway outside the VPN subnet, so routes could not be added. " +
                                $"Reconnecting with gateway {_gatewayFix}; this is remembered for this connection.");
                    _restartAfterExit = true;
                    SetState(VpnState.Reconnecting, "Fixing routing for this server…");
                    _ = SendAsync("signal SIGTERM");
                    break;
                }
                SetState(VpnState.Connected, desc is "ERROR" or "ROUTE_ERROR"
                    ? "Connected, but some routes failed (see log)" : "Connected");
                break;
            case "RECONNECTING":
                ConnectedSince = null;
                SetState(VpnState.Reconnecting, desc switch
                {
                    "tls-error" => "TLS handshake failed, retrying…",
                    "auth-failure" => "Authentication failed, retrying…",
                    "ping-restart" => "Connection lost, reconnecting…",
                    "" => "Reconnecting…",
                    _ => $"Reconnecting ({desc})…",
                });
                break;
            case "EXITING":
                if (desc != "exit-with-notification" && desc != "SIGTERM" && desc != "management" && !_stopRequested)
                    LastError ??= desc == "auth-failure" ? "Authentication failed." : $"OpenVPN stopped ({desc}).";
                SetState(VpnState.Disconnecting, "Disconnecting…");
                break;
        }
    }

    private void ApplyBytes(string body)
    {
        var f = body.Split(',');
        if (f.Length == 2 && long.TryParse(f[0], out var i) && long.TryParse(f[1], out var o))
        {
            BytesIn = i;
            BytesOut = o;
            Changed?.Invoke();
        }
    }

    private void EmitLogCsv(string body)
    {
        // time,flags,message
        var f = body.Split(',', 3);
        if (f.Length == 3 && long.TryParse(f[0], out var t))
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.ToString("HH:mm:ss");
            var level = f[1].Contains('F') ? "FATAL " : f[1].Contains('N') ? "ERROR " : f[1].Contains('W') ? "WARN " : "";
            Log?.Invoke($"{ts} {level}{f[2]}");
        }
        else Log?.Invoke(body);
    }

    private async Task HandlePassword(string body)
    {
        // Need 'Auth' username/password [SC:<flags>,<challenge text>]
        // Need 'Private Key' password
        // Verification Failed: 'Auth' [...]
        // Auth-Token:xxxx
        if (body.StartsWith("Verification Failed"))
        {
            int q1 = body.IndexOf('\''), q2 = q1 >= 0 ? body.IndexOf('\'', q1 + 1) : -1;
            var failedType = q2 > q1 ? body[(q1 + 1)..q2] : "Auth";
            _failedTypes.Add(failedType);
            if (failedType == "Auth") _authFailed = true;
            Log?.Invoke("Authentication failed: " + body);
            return;
        }
        if (!body.StartsWith("Need '")) return;

        int end = body.IndexOf('\'', 6);
        if (end < 0) return;
        var type = body[6..end];
        var rest = body[(end + 1)..].Trim();

        var kind = rest.StartsWith("username/password") ? CredentialKind.UserPass : CredentialKind.PasswordOnly;
        if (rest.StartsWith("ok") || rest.StartsWith("confirm")) return; // needok/needstr prompts — not used here

        string? challenge = null;
        bool echo = false;
        int sc = rest.IndexOf("SC:", StringComparison.Ordinal);
        if (sc >= 0)
        {
            var scBody = rest[(sc + 3)..];
            int comma = scBody.IndexOf(',');
            if (comma > 0)
            {
                if (int.TryParse(scBody[..comma], out var flags)) echo = (flags & 1) != 0;
                challenge = scBody[(comma + 1)..];
            }
        }

        var request = new CredentialRequest(kind, type, _failedTypes.Remove(type), challenge, echo, _lastUsername);
        Credentials? creds = CredentialsRequested == null ? null : await CredentialsRequested(request);

        if (creds == null)
        {
            _stopRequested = true;
            SetState(VpnState.Disconnecting, "Cancelled");
            await SendAsync("signal SIGTERM");
            return;
        }

        if (kind == CredentialKind.UserPass)
        {
            _lastUsername = creds.Username;
            await SendAsync($"username {Quote(type)} {Quote(creds.Username)}");
            var pass = creds.Password;
            if (challenge != null)
                pass = $"SCRV1:{B64(creds.Password)}:{B64(creds.ChallengeResponse ?? "")}";
            await SendAsync($"password {Quote(type)} {Quote(pass)}");
        }
        else
        {
            await SendAsync($"password {Quote(type)} {Quote(creds.Password)}");
        }
    }

    /// <summary>
    /// Some servers push "route-gateway" outside the subnet they assign (e.g. ifconfig
    /// 192.168.130.64/24 with route-gateway 172.29.40.1). Linux refuses routes via such a
    /// gateway, and with redirect-gateway that leaves no default route at all. On a tun
    /// device the gateway is only a label, so any address in the assigned subnet works.
    /// </summary>
    private void InspectPushReply(string line)
    {
        var start = line.IndexOf("PUSH_REPLY,", StringComparison.Ordinal);
        var opts = line[(start + 11)..].TrimEnd('\'').Split(',');
        string? gw = null, ip = null, mask = null;
        foreach (var o in opts)
        {
            var a = o.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length >= 2 && a[0] == "route-gateway") gw = a[1];
            if (a.Length >= 3 && a[0] == "ifconfig") { ip = a[1]; mask = a[2]; }
        }
        _gatewayFix = GatewayFix(ip, mask, gw);
        if (_gatewayFix != null)
            Log?.Invoke($"Note: pushed gateway {gw} is outside the assigned subnet {ip}/{mask}.");
    }

    internal static string? GatewayFix(string? ip, string? mask, string? gw)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var ipA) || !System.Net.IPAddress.TryParse(mask, out var maskA)
            || !System.Net.IPAddress.TryParse(gw, out var gwA)
            || ipA.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;
        uint U(System.Net.IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        uint m = U(maskA), net = U(ipA) & m;
        if ((U(gwA) & m) == net) return null;          // gateway is fine
        if (~m < 3) return null;                         // /31, /32: nothing sensible to pick
        uint cand = net + 1;
        if (cand == U(ipA)) cand = net + 2;
        return new System.Net.IPAddress(new[] { (byte)(cand >> 24), (byte)(cand >> 16), (byte)(cand >> 8), (byte)cand }).ToString();
    }

    // ---------------------------------------------------------------- lifecycle

    private async Task CollectEarlyOutput(StreamReader r)
    {
        try
        {
            while (await r.ReadLineAsync() is { } line)
            {
                lock (_earlyOutput) _earlyOutput.Add(line);
                if (_writer == null) Log?.Invoke(line);
            }
        }
        catch (IOException) { }
    }

    private async Task OnProcessExitedEarly()
    {
        int code = _process?.ExitCode ?? -1;
        await Task.Delay(100); // let the stdout/stderr readers drain
        if (!_stopRequested)
        {
            if (Environment.GetEnvironmentVariable("OVPN_CLIENT_NO_PKEXEC") != "1" && code is 126 or 127)
                LastError = code == 126 ? "Authorization was cancelled." : "Not authorized to start OpenVPN.";
            else
                LastError = ExplainFailure() ?? $"OpenVPN exited (code {code}).";
        }
        await Finish();
    }

    private string? ExplainFailure()
    {
        var lines = new List<string>();
        lock (_earlyOutput) lines.AddRange(_earlyOutput);
        try { if (File.Exists(LogFilePath)) lines.AddRange(File.ReadAllLines(LogFilePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var interesting = lines.LastOrDefault(l =>
            l.Contains("Options error", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("ERROR", StringComparison.Ordinal) ||
            l.Contains("Exiting due to fatal error", StringComparison.Ordinal) ||
            l.Contains("Error executing command", StringComparison.Ordinal));
        if (interesting == null) return null;
        // Drop openvpn's timestamp prefix ("2026-11-10 12:00:00 ").
        if (interesting.Length > 20 && char.IsDigit(interesting[0]) && interesting[4] == '-')
            interesting = interesting[20..];
        return interesting.Trim();
    }

    private async Task Finish()
    {
        if (State == VpnState.Disconnected) return;
        if (_restartAfterExit && !_stopRequested)
        {
            _restartAfterExit = false;
            if (_process != null)
            {
                await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(5000));
                _process.Dispose();
                _process = null;
            }
            State = VpnState.Disconnected; // quietly, StartAsync takes over from here
            Log?.Invoke($"--- Reconnecting {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            await StartAsync();
            return;
        }
        if (!_stopRequested && LastError == null && _authFailed)
            LastError = "Authentication failed.";
        if (_process != null)
        {
            await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(3000));
            _process.Dispose();
            _process = null;
        }
        ConnectedSince = null;
        SetState(VpnState.Disconnected, LastError ?? "Not connected");
        Ended?.Invoke();
    }

    private void Fail(string message)
    {
        LastError = message;
        SetState(VpnState.Disconnected, message);
        Ended?.Invoke();
    }

    private void Reset()
    {
        _stopRequested = false;
        _authFailed = false;
        _failedTypes.Clear();
        _gatewayFix = null;
        _restartAfterExit = false;
        LastError = null;
        LocalIp = LocalIp6 = RemoteIp = null;
        BytesIn = BytesOut = 0;
        ConnectedSince = null;
        lock (_earlyOutput) _earlyOutput.Clear();
    }

    private void SetState(VpnState state, string? detail)
    {
        State = state;
        StateDetail = detail;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- helpers

    public static string? FindOpenVpn()
    {
        var overridePath = Environment.GetEnvironmentVariable("OVPN_CLIENT_OPENVPN");
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;
        foreach (var p in new[] { "/usr/sbin/openvpn", "/usr/bin/openvpn", "/usr/local/sbin/openvpn", "/sbin/openvpn" })
            if (File.Exists(p)) return p;
        return null;
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
