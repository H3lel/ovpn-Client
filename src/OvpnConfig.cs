namespace OvpnClient;

/// <summary>
/// Light-weight reader for .ovpn files. It only extracts what the UI needs to show
/// and the external files a config points at, so they can be imported with it.
/// OpenVPN itself does the real parsing.
/// </summary>
public sealed class OvpnConfig
{
    // Directives whose first argument is a path to another file.
    private static readonly HashSet<string> FileDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "ca", "cert", "key", "pkcs12", "dh", "tls-auth", "tls-crypt", "tls-crypt-v2",
        "secret", "crl-verify", "extra-certs", "auth-user-pass", "askpass", "http-proxy-user-pass",
    };

    public string? RemoteHost { get; private set; }
    public string? RemotePort { get; private set; }
    public string? Protocol { get; private set; }
    public string? Device { get; private set; }
    public int RemoteCount { get; private set; }
    public bool NeedsUserPass { get; private set; }
    public string? Cipher { get; private set; }
    public bool HasDataCiphers { get; private set; }

    /// <summary>
    /// OpenVPN 2.6+ ignores "cipher" when negotiating and only offers modern AEAD ciphers.
    /// Older servers that only speak the config's cipher (e.g. AES-256-CBC) then drop the
    /// connection mid-handshake ("Connection reset" loops). Offering it as well fixes that.
    /// </summary>
    public string? LegacyCipherToOffer =>
        !HasDataCiphers && Cipher != null && !Cipher.Contains("GCM", StringComparison.OrdinalIgnoreCase)
            && !Cipher.Contains("CHACHA", StringComparison.OrdinalIgnoreCase)
            ? Cipher : null;

    /// <summary>Relative or absolute paths of external files referenced by the config.</summary>
    public List<string> ReferencedFiles { get; } = new();

    public static OvpnConfig Parse(string path) => ParseText(File.ReadAllText(path));

    public static OvpnConfig ParseText(string text)
    {
        var cfg = new OvpnConfig();
        string? inlineTag = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (inlineTag != null)
            {
                if (line.Equals($"</{inlineTag}>", StringComparison.OrdinalIgnoreCase))
                    inlineTag = null;
                continue;
            }
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;
            if (line.StartsWith('<') && line.EndsWith('>') && !line.StartsWith("</"))
            {
                inlineTag = line[1..^1];
                if (inlineTag.Equals("connection", StringComparison.OrdinalIgnoreCase))
                    inlineTag = null; // <connection> blocks contain directives, keep parsing
                continue;
            }
            if (line.StartsWith("</"))
                continue;

            var args = Tokenize(line);
            if (args.Count == 0) continue;
            var directive = args[0].TrimStart('-').ToLowerInvariant();

            switch (directive)
            {
                case "remote":
                    cfg.RemoteCount++;
                    if (cfg.RemoteHost == null && args.Count > 1)
                    {
                        cfg.RemoteHost = args[1];
                        if (args.Count > 2) cfg.RemotePort = args[2];
                        if (args.Count > 3) cfg.Protocol = args[3];
                    }
                    break;
                case "port" or "rport" when args.Count > 1:
                    cfg.RemotePort ??= args[1];
                    break;
                case "proto" when args.Count > 1:
                    cfg.Protocol ??= args[1];
                    break;
                case "dev" when args.Count > 1:
                    cfg.Device = args[1];
                    break;
                case "cipher" when args.Count > 1:
                    cfg.Cipher = args[1];
                    break;
                case "data-ciphers" or "ncp-ciphers" or "data-ciphers-fallback":
                    cfg.HasDataCiphers = true;
                    break;
            }

            if (directive == "auth-user-pass")
                cfg.NeedsUserPass = true;

            if (FileDirectives.Contains(directive) && args.Count > 1 && args[1] != "[inline]")
            {
                // "auth-user-pass" with a file means credentials come from disk, not from us.
                if (directive == "auth-user-pass") cfg.NeedsUserPass = false;
                cfg.ReferencedFiles.Add(args[1]);
            }
        }

        cfg.Protocol ??= "udp";
        cfg.RemotePort ??= "1194";
        return cfg;
    }

    /// <summary>"vpn.example.com:1194 (UDP)"</summary>
    public string ServerSummary
    {
        get
        {
            if (RemoteHost == null) return "No server set";
            var proto = (Protocol ?? "udp").Replace("-client", "").ToUpperInvariant();
            var extra = RemoteCount > 1 ? $" + {RemoteCount - 1} more" : "";
            return $"{RemoteHost}:{RemotePort} ({proto}){extra}";
        }
    }

    // OpenVPN config tokenizer: whitespace separated, supports "double" and 'single' quotes.
    private static List<string> Tokenize(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        char quote = '\0';
        bool inToken = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; continue; }
                if (c == '\\' && quote == '"' && i + 1 < line.Length) { sb.Append(line[++i]); continue; }
                sb.Append(c);
            }
            else if (c is '"' or '\'') { quote = c; inToken = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken) { result.Add(sb.ToString()); sb.Clear(); inToken = false; }
            }
            else if (c is '#' or ';' && !inToken) break;
            else { sb.Append(c); inToken = true; }
        }
        if (inToken) result.Add(sb.ToString());
        return result;
    }
}
