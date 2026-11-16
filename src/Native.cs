using System.Runtime.InteropServices;

namespace OvpnClient;

/// <summary>The few C calls the bindings don't wrap conveniently.</summary>
internal static partial class Native
{
    [LibraryImport("libgtk-4.so.1")]
    private static partial IntPtr gdk_file_list_get_files(IntPtr fileList);

    [LibraryImport("libgio-2.0.so.0")]
    private static partial IntPtr g_file_get_path(IntPtr file);

    [LibraryImport("libglib-2.0.so.0")]
    private static partial void g_free(IntPtr mem);

    [LibraryImport("libglib-2.0.so.0")]
    private static partial void g_slist_free(IntPtr list);

    /// <summary>Local paths in a GdkFileList (what a file drag-and-drop delivers).</summary>
    public static List<string> FileListPaths(IntPtr fileList)
    {
        var result = new List<string>();
        if (fileList == IntPtr.Zero) return result;

        var head = gdk_file_list_get_files(fileList); // GSList of GFile*, container is ours
        for (var node = head; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
        {
            var file = Marshal.ReadIntPtr(node);
            var cpath = g_file_get_path(file);
            if (cpath == IntPtr.Zero) continue; // not a local file
            var path = Marshal.PtrToStringUTF8(cpath);
            g_free(cpath);
            if (path != null) result.Add(path);
        }
        g_slist_free(head);
        return result;
    }
}
