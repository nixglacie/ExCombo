using System;
using System.Runtime.InteropServices;

namespace ExCombo.Helpers;

// Win32 clipboard access. ImGui's clipboard functions corrupt / truncate large payloads
// (observed: a ~58 KB flow import garbled the whole font rendering and failed to parse),
// so flow import/export goes straight to the OS clipboard instead.
internal static class ClipboardHelper {
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE  = 0x0002;

    [DllImport("user32.dll",  SetLastError = true)] private static extern bool   OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll",  SetLastError = true)] private static extern bool   CloseClipboard();
    [DllImport("user32.dll",  SetLastError = true)] private static extern bool   EmptyClipboard();
    [DllImport("user32.dll")]                       private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll",  SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")]                     private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")]                     private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")]                     private static extern bool   GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")]                     private static extern IntPtr GlobalFree(IntPtr hMem);

    public static string? GetText() {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try     { return Marshal.PtrToStringUni(ptr); }
            finally { GlobalUnlock(handle); }
        } finally {
            CloseClipboard();
        }
    }

    public static bool SetText(string text) {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try {
            EmptyClipboard();
            var bytes  = (UIntPtr)((text.Length + 1) * sizeof(char));
            var handle = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (handle == IntPtr.Zero) return false;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) { GlobalFree(handle); return false; }
            try {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * sizeof(char), 0);   // null terminator
            } finally {
                GlobalUnlock(handle);
            }
            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero) { GlobalFree(handle); return false; }
            return true;   // clipboard owns the handle now
        } finally {
            CloseClipboard();
        }
    }
}
