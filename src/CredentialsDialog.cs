namespace OvpnClient;

/// <summary>
/// Answers OpenVPN's credential requests. Answers go straight to OpenVPN over the
/// management socket. The username is remembered in the profile; the password only if
/// the user turns on "Save Password", and then in the login keyring, never in our files.
/// </summary>
public static class CredentialsDialog
{
    public static async Task<Credentials?> AskAsync(Gtk.Window parent, Profile profile, CredentialRequest req, ProfileStore store)
    {
        bool userPass = req.Kind == CredentialKind.UserPass;

        // ---- saved password: answer without asking, unless the server just rejected it
        string? saved = null;
        if (profile.SavePassword)
        {
            if (req.PreviousFailed)
                await Keyring.ClearAsync(profile.Id, req.Type);
            else
                saved = await Keyring.LookupAsync(profile.Id, req.Type);
        }
        bool haveUser = !userPass || !string.IsNullOrEmpty(profile.SavedUsername);
        if (saved != null && haveUser && req.ChallengeText == null)
            return new Credentials(userPass ? profile.SavedUsername! : "", saved, null);

        // ---- otherwise ask
        string heading, body;
        if (req.Type == "Private Key")
        {
            heading = "Private Key Password";
            body = $"“{profile.Name}” uses an encrypted private key. Enter its password to connect.";
        }
        else if (req.Type == "HTTP Proxy")
        {
            heading = "Proxy Sign In";
            body = $"The HTTP proxy for “{profile.Name}” needs a username and password.";
        }
        else
        {
            heading = $"Sign In to {profile.Name}";
            body = req.PreviousFailed
                ? "The username or password was not accepted. Try again."
                : req.ChallengeText != null
                    ? "Enter your VPN password and the requested code."
                    : "Enter your VPN username and password.";
        }

        var d = Adw.AlertDialog.New(heading, body);
        d.AddResponse("cancel", "_Cancel");
        d.AddResponse("connect", "C_onnect");
        d.SetResponseAppearance("connect", Adw.ResponseAppearance.Suggested);
        d.SetCloseResponse("cancel");
        d.SetDefaultResponse("connect");

        var rows = new List<Gtk.Widget>();

        Adw.EntryRow? user = null;
        if (userPass)
        {
            user = Adw.EntryRow.New();
            user.SetTitle("Username");
            user.SetText(req.LastUsername ?? (req.Type == "Auth" ? profile.SavedUsername : null) ?? "");
            user.SetActivatesDefault(true);
            rows.Add(user);
        }

        var pass = Adw.PasswordEntryRow.New();
        pass.SetTitle("Password");
        pass.SetActivatesDefault(true);
        if (saved != null) pass.SetText(saved); // e.g. only the one-time code is missing
        rows.Add(pass);

        Adw.EntryRow? challenge = null;
        if (req.ChallengeText != null)
        {
            challenge = req.ChallengeEcho ? Adw.EntryRow.New() : Adw.PasswordEntryRow.New();
            challenge.SetTitle(req.ChallengeText);
            challenge.SetActivatesDefault(true);
            rows.Add(challenge);
        }

        var remember = Adw.SwitchRow.New();
        remember.SetTitle("Save Password");
        remember.SetSubtitle("Kept in your login keyring");
        remember.SetActive(profile.SavePassword);
        rows.Add(remember);

        d.SetExtraChild(Dialogs.BoxedList(rows.ToArray()));

        void Validate() => d.SetResponseEnabled("connect",
            (user == null || user.GetText().Length > 0) && pass.GetText().Length > 0);
        user?.OnChanged += (_, _) => Validate();
        pass.OnChanged += (_, _) => Validate();
        Validate();

        Gtk.Widget focus = user != null && user.GetText().Length == 0 ? user
            : challenge != null && pass.GetText().Length > 0 ? challenge
            : pass;
        var r = await Dialogs.RunAsync(d, parent, focus);
        if (r != "connect") return null;

        // ---- remember what the user chose
        bool changed = false;
        if (user != null && req.Type == "Auth" && user.GetText() != profile.SavedUsername)
        {
            profile.SavedUsername = user.GetText();
            changed = true;
        }
        if (remember.GetActive() != profile.SavePassword)
        {
            profile.SavePassword = remember.GetActive();
            changed = true;
        }
        if (changed) store.Save();

        if (remember.GetActive())
            _ = Keyring.StoreAsync(profile.Id, req.Type, KeyringLabel(profile, req.Type), pass.GetText());
        else
            _ = Keyring.ClearAsync(profile.Id);

        return new Credentials(user?.GetText() ?? "", pass.GetText(), challenge?.GetText());
    }

    public static string KeyringLabel(Profile p, string type) =>
        type == "Auth" ? $"OpenVPN Client: {p.Name}" : $"OpenVPN Client: {p.Name} ({type})";
}
