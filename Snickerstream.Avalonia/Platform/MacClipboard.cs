using System.Diagnostics;

namespace SnickerstreamV2;

/// <summary>
/// Puts an image on the macOS clipboard. Avalonia's clipboard has no cross-platform image support, and
/// Cocoa's NSPasteboard isn't reachable without a native binding — so this shells out to <c>osascript</c>,
/// reading the already-saved PNG and setting it as the clipboard's image data (pasteable in Preview,
/// Messages, Notes, image editors, etc.). macOS-only; guarded by <see cref="OperatingSystem.IsMacOS"/>.
/// </summary>
internal static class MacClipboard
{
    public static bool TrySetImageFromPng(string pngPath)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(pngPath)) return false;
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-e");
            // «class PNGf» tells AppleScript to read the file's bytes as PNG image data for the clipboard.
            psi.ArgumentList.Add($"set the clipboard to (read (POSIX file \"{pngPath}\") as «class PNGf»)");

            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { /* best effort */ } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
