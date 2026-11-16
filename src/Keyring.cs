using System.Runtime.InteropServices;

namespace OvpnClient;

/// <summary>
/// Saved VPN passwords, kept in the user's login keyring (GNOME Keyring / any Secret
/// Service) via libsecret — never in our own files. Entries are keyed by profile id and
/// OpenVPN's credential type ("Auth", "Private Key", …) and show up in the Passwords and
/// Keys app as "OpenVPN Client: &lt;profile&gt;".
///
/// libsecret's sync calls may wait on a keyring unlock prompt, so they run off the UI thread.
/// </summary>
public static partial class Keyring
{
    private const string Lib = "libsecret-1.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string SchemaName = Program.AppId + ".Password";

    public static Task<string?> LookupAsync(string profileId, string type) =>
        Task.Run(() => WithAttributes(profileId, type, (attrs, err) =>
        {
            var p = secret_password_lookupv_sync(Schema, attrs, IntPtr.Zero, err);
            if (p == IntPtr.Zero) return null;
            var s = Marshal.PtrToStringUTF8(p);
            secret_password_free(p); // wipes the memory before freeing
            return s;
        }));

    public static Task<bool> StoreAsync(string profileId, string type, string label, string password) =>
        Task.Run(() => WithAttributes(profileId, type, (attrs, err) =>
        {
            var lbl = Marshal.StringToCoTaskMemUTF8(label);
            var pw = Marshal.StringToCoTaskMemUTF8(password);
            try { return secret_password_storev_sync(Schema, attrs, IntPtr.Zero, lbl, pw, IntPtr.Zero, err); }
            finally
            {
                // Don't leave a plaintext copy of the password lying around in freed memory.
                unsafe { new Span<byte>((void*)pw, System.Text.Encoding.UTF8.GetByteCount(password)).Clear(); }
                Marshal.FreeCoTaskMem(pw);
                Marshal.FreeCoTaskMem(lbl);
            }
        }));

    public static Task ClearAsync(string profileId, string? type = null) =>
        Task.Run(() => WithAttributes(profileId, type, (attrs, err) =>
            secret_password_clearv_sync(Schema, attrs, IntPtr.Zero, err)));

    // ------------------------------------------------------------------ plumbing

    private static T? WithAttributes<T>(string profileId, string? type, Func<IntPtr, IntPtr, T> call)
    {
        var strings = new List<IntPtr>();
        IntPtr Str(string s) { var p = Marshal.StringToCoTaskMemUTF8(s); strings.Add(p); return p; }

        var table = g_hash_table_new(StrHash, StrEqual);
        var errBox = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(errBox, IntPtr.Zero);
        try
        {
            g_hash_table_insert(table, Str("profile"), Str(profileId));
            if (type != null) g_hash_table_insert(table, Str("type"), Str(type));

            var result = call(table, errBox);

            var err = Marshal.ReadIntPtr(errBox);
            if (err != IntPtr.Zero)
            {
                // struct GError { GQuark domain; gint code; gchar *message; }
                var msg = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(err, 8));
                g_error_free(err);
                Console.Error.WriteLine($"Keyring: {msg}");
                return default;
            }
            return result;
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("Keyring: libsecret is not installed; passwords cannot be saved.");
            return default;
        }
        finally
        {
            g_hash_table_unref(table);
            Marshal.FreeHGlobal(errBox);
            foreach (var p in strings) Marshal.FreeCoTaskMem(p);
        }
    }

    // SecretSchema (secret-schema.h), built by hand because secret_schema_new is variadic:
    //   const gchar *name; SecretSchemaFlags flags;
    //   SecretSchemaAttribute attributes[32];   // { const gchar *name; SecretSchemaAttributeType type; }
    //   gint reserved; gpointer reserved1..7;
    private static readonly IntPtr Schema = BuildSchema();

    private static IntPtr BuildSchema()
    {
        int attrSize = IntPtr.Size * 2;                      // pointer + enum, padded
        int size = IntPtr.Size * 2 + attrSize * 32 + IntPtr.Size * 8;
        var s = Marshal.AllocHGlobal(size);                  // lives for the whole process
        unsafe { new Span<byte>((void*)s, size).Clear(); }
        Marshal.WriteIntPtr(s, 0, Marshal.StringToCoTaskMemUTF8(SchemaName));
        Marshal.WriteInt32(s, IntPtr.Size, 0);               // SECRET_SCHEMA_NONE
        int a = IntPtr.Size * 2;
        Marshal.WriteIntPtr(s, a, Marshal.StringToCoTaskMemUTF8("profile"));
        Marshal.WriteInt32(s, a + IntPtr.Size, 0);           // SECRET_SCHEMA_ATTRIBUTE_STRING
        Marshal.WriteIntPtr(s, a + attrSize, Marshal.StringToCoTaskMemUTF8("type"));
        Marshal.WriteInt32(s, a + attrSize + IntPtr.Size, 0);
        return s;                                            // attributes[2].name == NULL terminates
    }

    private static readonly IntPtr StrHash = NativeLibrary.GetExport(NativeLibrary.Load(GLib), "g_str_hash");
    private static readonly IntPtr StrEqual = NativeLibrary.GetExport(NativeLibrary.Load(GLib), "g_str_equal");

    [LibraryImport(Lib)] private static partial IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, IntPtr error);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, IntPtr error);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, IntPtr error);
    [LibraryImport(Lib)] private static partial void secret_password_free(IntPtr password);

    [LibraryImport(GLib)] private static partial IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);
    [LibraryImport(GLib)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [LibraryImport(GLib)] private static partial void g_hash_table_unref(IntPtr table);
    [LibraryImport(GLib)] private static partial void g_error_free(IntPtr error);
}
