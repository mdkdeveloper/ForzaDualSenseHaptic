using System.Diagnostics;

namespace ForzaHaptics.Gui.Services;

public interface IPlatformShellService
{
    void OpenDirectory(string path);
}

/// <summary>Current Windows implementation; replace this adapter when adding another desktop platform.</summary>
public sealed class WindowsPlatformShellService : IPlatformShellService
{
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
