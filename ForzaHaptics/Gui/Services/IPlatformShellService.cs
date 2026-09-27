using System.Diagnostics;

namespace ForzaHaptics.Gui.Services;

public interface IPlatformShellService
{
    void OpenDirectory(string path);
    void OpenUrl(string url);
}

/// <summary>Current Windows implementation; replace this adapter when adding another desktop platform.</summary>
public sealed class WindowsPlatformShellService : IPlatformShellService
{
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Only HTTPS URLs are supported.", nameof(url));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void OpenDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { path },
            UseShellExecute = true,
        });
    }
}
