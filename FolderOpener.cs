using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ImOrder;

public static class FolderOpener
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);

    public static async Task<bool> Open(string directory)
    {
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(directory);
        Process.Start(start);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(150);
            var window = FindFolderWindow(directory);
            if (window == IntPtr.Zero) continue;
            if (IsIconic(window)) ShowWindowAsync(window, 9);
            var foreground = GetForegroundWindow();
            var otherThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
            var thisThread = GetCurrentThreadId();
            bool attached = otherThread != 0 && otherThread != thisThread && AttachThreadInput(thisThread, otherThread, true);
            try { BringWindowToTop(window); SetForegroundWindow(window); }
            finally { if (attached) AttachThreadInput(thisThread, otherThread, false); }
            return true;
        }
        return false;
    }

    private static IntPtr FindFolderWindow(string directory)
    {
        object? shell = null, windows = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return IntPtr.Zero;
            shell = Activator.CreateInstance(type);
            windows = type.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
            if (windows == null) return IntPtr.Zero;
            dynamic collection = windows;
            int count = collection.Count;
            for (var i = count - 1; i >= 0; i--)
            {
                object? item = null;
                try
                {
                    item = collection.Item(i);
                    if (item == null) continue;
                    dynamic window = item;
                    string url = window.LocationURL ?? "";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
                    if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(uri.LocalPath)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase))
                        return new IntPtr(Convert.ToInt64(window.HWND));
                }
                catch (COMException) { }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
            }
        }
        catch (COMException) { }
        finally { if (windows != null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows); if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
        return IntPtr.Zero;
    }
}
