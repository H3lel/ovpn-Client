namespace OvpnClient;

/// <summary>Small awaitable wrappers around Adw.AlertDialog.</summary>
public static class Dialogs
{
    public static Task<string?> ChooseAsync(Gtk.Widget parent, string heading, string? body,
        params (string Id, string Label, Adw.ResponseAppearance Look)[] responses)
    {
        var d = Adw.AlertDialog.New(heading, body);
        foreach (var (id, label, look) in responses)
        {
            d.AddResponse(id, label);
            d.SetResponseAppearance(id, look);
        }
        d.SetCloseResponse(responses[0].Id);
        // Never make a destructive choice the Enter-key default.
        var safeDefault = responses.LastOrDefault(r => r.Look != Adw.ResponseAppearance.Destructive);
        d.SetDefaultResponse(safeDefault.Id ?? responses[0].Id);
        return RunAsync(d, parent);
    }

    public static async Task<bool> ConfirmAsync(Gtk.Widget parent, string heading, string body,
        string confirmLabel, bool destructive)
    {
        var r = await ChooseAsync(parent, heading, body,
            ("cancel", "_Cancel", Adw.ResponseAppearance.Default),
            ("ok", confirmLabel, destructive ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested));
        return r == "ok";
    }

    public static async Task<string?> AskTextAsync(Gtk.Widget parent, string heading, string? body,
        string fieldTitle, string initial, string confirmLabel)
    {
        var d = Adw.AlertDialog.New(heading, body);
        d.AddResponse("cancel", "_Cancel");
        d.AddResponse("ok", confirmLabel);
        d.SetResponseAppearance("ok", Adw.ResponseAppearance.Suggested);
        d.SetCloseResponse("cancel");
        d.SetDefaultResponse("ok");

        var entry = Adw.EntryRow.New();
        entry.SetTitle(fieldTitle);
        entry.SetText(initial);
        entry.SetActivatesDefault(true);
        var list = BoxedList(entry);
        d.SetExtraChild(list);

        entry.OnChanged += (_, _) => d.SetResponseEnabled("ok", entry.GetText().Trim().Length > 0);

        var r = await RunAsync(d, parent, focus: entry);
        return r == "ok" ? entry.GetText() : null;
    }

    public static Gtk.ListBox BoxedList(params Gtk.Widget[] rows)
    {
        var list = Gtk.ListBox.New();
        list.AddCssClass("boxed-list");
        list.SetSelectionMode(Gtk.SelectionMode.None);
        foreach (var r in rows) list.Append(r);
        return list;
    }

    public static Task<string?> RunAsync(Adw.AlertDialog d, Gtk.Widget parent, Gtk.Widget? focus = null)
    {
        var tcs = new TaskCompletionSource<string?>();
        d.OnResponse += (_, e) => tcs.TrySetResult(e.Response);
        d.Present(parent);
        focus?.GrabFocus();
        return tcs.Task;
    }
}
