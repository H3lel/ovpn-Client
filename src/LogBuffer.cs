namespace OvpnClient;

/// <summary>A capped text buffer of one profile's OpenVPN log, shown in the log view.</summary>
public sealed class LogBuffer
{
    private const int MaxLines = 2000;
    public Gtk.TextBuffer Buffer { get; } = Gtk.TextBuffer.New(null);
    public event Action? Appended;

    public void Append(string line)
    {
        Buffer.GetEndIter(out var end);
        Buffer.Insert(end, (Buffer.GetCharCount() > 0 ? "\n" : "") + line, -1);

        int extra = Buffer.GetLineCount() - MaxLines;
        if (extra > 0)
        {
            Buffer.GetStartIter(out var start);
            Buffer.GetIterAtLine(out var cut, extra);
            Buffer.Delete(start, cut);
        }
        Appended?.Invoke();
    }

    public void Clear() => Buffer.SetText("", 0);

    public string Text
    {
        get
        {
            Buffer.GetStartIter(out var s);
            Buffer.GetEndIter(out var e);
            return Buffer.GetText(s, e, false);
        }
    }
}
