using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;

namespace ForzaHaptics.Gui.Services;

/// <summary>Hands controller ownership to an elevated GUI process after the old process exits.</summary>
internal static class ElevatedRestart
{
    internal const string EnableArgument = "--internal-impulse-triggers";
    internal readonly record struct Request(int ParentId, long ParentStartTime);

    internal static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static bool IsRestartInvocation(string[] args) => args.Contains(EnableArgument, StringComparer.Ordinal);

    internal static Request? Parse(string[] args)
    {
        if (!IsRestartInvocation(args)) return null;
        if (args.Length != 5 || args[0] != EnableArgument || args[1] != "--wait-for-parent" ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId) || parentId <= 0 ||
            args[3] != "--parent-start-time" ||
            !long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out long startTime) || startTime <= 0)
            throw new ArgumentException("Invalid administrator restart arguments.");
        return new Request(parentId, startTime);
    }

    internal static ProcessStartInfo CreateStartInfo(string processPath, string assemblyPath, int parentId, long parentStartTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        var info = new ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
            info.ArgumentList.Add(assemblyPath);
        }
        info.ArgumentList.Add(EnableArgument);
        info.ArgumentList.Add("--wait-for-parent");
        info.ArgumentList.Add(parentId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--parent-start-time");
        info.ArgumentList.Add(parentStartTime.ToString(CultureInfo.InvariantCulture));
        return info;
    }

    internal static Task<bool> RestartAsync() => Task.Run(() =>
    {
        using var current = Process.GetCurrentProcess();
        var info = CreateStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running application."),
            typeof(ElevatedRestart).Assembly.Location, current.Id, current.StartTime.ToUniversalTime().Ticks);
        return Start(info, Process.Start);
    });

    internal static bool Start(ProcessStartInfo info, Func<ProcessStartInfo, Process?> start)
    {
        try
        {
            using var child = start(info) ?? throw new InvalidOperationException("The administrator process did not start.");
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return false; // The user cancelled the Windows elevation prompt.
        }
    }

    internal static bool Prepare(string[] args, bool isAdministrator, Action<Request>? wait = null)
    {
        var request = Parse(args);
        if (request is null) return false;
        if (!isAdministrator) throw new InvalidOperationException("Impulse Triggers requires administrator privileges. Restart the application as administrator.");
        (wait ?? (parent => WaitForParent(parent, TimeSpan.FromSeconds(30))))(request.Value);
        return true;
    }

    internal static void WaitForParent(Request request, TimeSpan timeout)
    {
        Process parent;
        try { parent = Process.GetProcessById(request.ParentId); }
        catch (ArgumentException) { return; } // The old application has already exited.
        using (parent)
        {
            try
            {
                if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTime) return;
            }
            catch (InvalidOperationException) { return; }
            if (!parent.WaitForExit((int)timeout.TotalMilliseconds))
                throw new TimeoutException("The previous application did not close within 30 seconds. Close it and start the application again.");
        }
    }
}
