# OpenVPN Client

A native GNOME app for connecting to VPNs with OpenVPN config files (`.ovpn`). It is written in C# (.NET 10) with GTK 4 and libadwaita, using the [GirCore](https://gircore.github.io/) bindings, so it looks and behaves like the other apps on your desktop and follows your light/dark and accent settings.

## Features

- **Import `.ovpn` / `.conf` files** with the + button, Ctrl+O, by dropping files onto the window, or with "Open With" in Files. Certificate and key files the config points to (`ca`, `cert`, `key`, `tls-auth`, `tls-crypt`, `pkcs12`, …) are copied along with it.
- **One-click connect and disconnect.** OpenVPN runs as root through `pkexec`, so you get the standard system password prompt.
- **Sign-in prompts** for username/password, private-key passwords, proxy credentials and static challenges (OTP codes). Answers go straight to OpenVPN.
- **Save Password** keeps the password in your login keyring (GNOME Keyring via libsecret), never in the app's own files. Saved passwords are filled in automatically. If the server rejects one, it is deleted and you're asked again. **Forget Saved Password** in the connection menu removes it, and it appears as "OpenVPN Client: <name>" in the Passwords and Keys app.
- **Misconfigured gateways are fixed automatically:** some servers push a `route-gateway` outside the subnet they assign. Linux then refuses the routes, and with `redirect-gateway` you end up with no internet at all (state `ROUTE_ERROR`). The app detects this, picks an address inside the VPN subnet (on a tun device the gateway is only a label), remembers it for that connection, and reconnects once.
- **Older servers work as-is:** configs with a legacy `cipher` (e.g. `AES-256-CBC`) and no `data-ciphers` get that cipher offered too. OpenVPN 2.6+ would otherwise offer only modern ciphers, and servers that only speak the old one drop the connection mid-handshake ("Connection reset" loops).
- **Live status**: connection stage, VPN and server addresses, how long you've been connected, traffic counters, and a live log you can copy.
- **Web sign-in**: SAML / `WEB_AUTH` servers open your browser automatically.
- **Stays connected after you close the window** if you choose to. Reopening the app reattaches to the running tunnel.
- Several profiles can be connected at the same time.

## Install from a release

Download `ovpn-client-<version>-linux-x64.tar.gz` from [Releases](https://github.com/H3lel/Ovpn-Client/releases). It's self-contained, so .NET doesn't need to be installed.

```bash
sudo apt install openvpn
tar xzf ovpn-client-*-linux-x64.tar.gz
cd ovpn-client-*-linux-x64
./install.sh
```

GTK 4 and libadwaita (1.5 or newer) come with Ubuntu's GNOME desktop.

## Build from source

```bash
sudo apt install openvpn dotnet-sdk-10.0
```

### Run

```bash
dotnet run
```

### Install for your user

```bash
./install.sh
```

This builds a Release version into `~/.local/lib/ovpn-client`, links `~/.local/bin/ovpn-client`, and adds **OpenVPN Client** to the app grid. To uninstall, run `./install.sh --uninstall`.

## How it works

```
GTK app (your user) ──unix socket──▶ openvpn (root, started with pkexec)
     ▲   management protocol: state, bytecount, log, password, signal
```

- When you connect, the app runs `setsid pkexec openvpn --config … --management <socket> unix --management-client-user $USER --management-hold --management-query-passwords --auth-retry interact`.
- OpenVPN creates the management socket in `$XDG_RUNTIME_DIR/ovpn-client/` and accepts connections on it only from your user.
- Everything else goes over that socket: status, credential prompts, traffic counters, the log, and stopping the connection (`signal SIGTERM`). The app never needs root after the initial `pkexec`.
- `setsid` puts OpenVPN in its own session, so the tunnel survives the app closing. The socket path is fixed per profile, so the next launch finds it and reattaches.

Where things are stored:

| What | Where |
| --- | --- |
| Imported profiles | `~/.local/share/ovpn-client/profiles/<id>/` |
| Profile list | `~/.local/share/ovpn-client/profiles.json` |
| Sockets and OpenVPN logs | `$XDG_RUNTIME_DIR/ovpn-client/` |

## Notes

- **DNS:** pushed DNS servers are applied by OpenVPN 2.7's built-in `dns-updown` handling, which works with systemd-resolved on Ubuntu. On older OpenVPN versions, add an `up`/`down` script such as `update-systemd-resolved` to the config.
- **Password every time:** `pkexec` asks for your password on each connect. If you want to avoid that, add a polkit rule for `/usr/sbin/openvpn`. That effectively gives your user root-level VPN control, so decide whether you're comfortable with that.
- **Configs that set their own `management` option** will clash with the app's, so remove that line from the config.

## Development

`src/` layout:

| File | Purpose |
| --- | --- |
| `Program.cs` | `Adw.Application` setup, single instance, open-file handling |
| `MainWindow.cs` | Window, sidebar, import, drag and drop, close confirmation, reattach |
| `ProfilePage.cs` | Detail page: status, connect button, details, log |
| `VpnSession.cs` | Starts OpenVPN and speaks the management protocol |
| `CredentialsDialog.cs`, `Dialogs.cs` | Awaitable `Adw.AlertDialog` helpers |
| `OvpnConfig.cs` | Minimal `.ovpn` reader (server summary, referenced files) |
| `ProfileStore.cs` | Profile import and storage |
| `Keyring.cs` | Saved passwords via libsecret |
| `Native.cs` | `GdkFileList` → paths, for drag and drop |

Environment variables for testing:

- `OVPN_CLIENT_NO_PKEXEC=1` runs OpenVPN as your own user, without `pkexec`. This is useful with `dev null` test configs.
- `OVPN_CLIENT_OPENVPN=/path/to/openvpn` uses a different OpenVPN binary.

To build a single native binary (requires `clang`):

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

## License

MIT, see [LICENSE](LICENSE).
