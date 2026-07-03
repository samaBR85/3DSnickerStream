using System.Runtime.InteropServices;

namespace SnickerstreamV2;

/// <summary>
/// Puts a 32bpp image on the Windows clipboard as CF_DIB — the format Paint, Office, browsers, etc.
/// actually paste. Avalonia's clipboard has no cross-platform image support, so this is a Windows-only
/// P/Invoke path guarded by <see cref="OperatingSystem.IsWindows"/>; it compiles everywhere but only
/// runs on Windows.
/// </summary>
internal static class WindowsClipboard
{
    private const int CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int HeaderSize = 40;   // BITMAPINFOHEADER

    /// <summary>bgraTopDown = top-down BGRA8888 pixels (row 0 = top), w×h.</summary>
    public static bool TrySetDib(byte[] bgraTopDown, int w, int h)
    {
        if (!OperatingSystem.IsWindows()) return false;

        int imgSize = w * h * 4;
        int total = HeaderSize + imgSize;
        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)total);
        if (hGlobal == IntPtr.Zero) return false;

        IntPtr ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
        try
        {
            var header = new byte[HeaderSize];
            BitConverter.GetBytes(HeaderSize).CopyTo(header, 0);   // biSize
            BitConverter.GetBytes(w).CopyTo(header, 4);            // biWidth
            BitConverter.GetBytes(-h).CopyTo(header, 8);           // biHeight (negative = top-down)
            BitConverter.GetBytes((short)1).CopyTo(header, 12);    // biPlanes
            BitConverter.GetBytes((short)32).CopyTo(header, 14);   // biBitCount
            // biCompression = BI_RGB (0), the rest stays zero except biSizeImage:
            BitConverter.GetBytes(imgSize).CopyTo(header, 20);     // biSizeImage
            Marshal.Copy(header, 0, ptr, HeaderSize);
            Marshal.Copy(bgraTopDown, 0, ptr + HeaderSize, imgSize);
        }
        finally { GlobalUnlock(hGlobal); }

        if (!OpenClipboard(IntPtr.Zero)) { GlobalFree(hGlobal); return false; }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(CF_DIB, hGlobal) == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
            return true;   // on success the clipboard owns hGlobal — do not free it
        }
        finally { CloseClipboard(); }
    }

    [DllImport("kernel32")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32")] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("user32")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32")] private static extern bool EmptyClipboard();
    [DllImport("user32")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32")] private static extern bool CloseClipboard();
}
