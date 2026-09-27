using System.ComponentModel;
using System.Diagnostics;
using ForzaHaptics.Gui.Services;

namespace ForzaHaptics.Tests;

public sealed class ElevatedRestartTests
{
    [Fact]
    public void UacCancellationReturnsFalse()
    {
        Assert.False(ElevatedRestart.Start(new ProcessStartInfo(), _ => throw new Win32Exception(1223)));
    }

    [Fact]
    public void OtherLaunchFailuresAreReported()
    {
        Assert.Throws<Win32Exception>(() => ElevatedRestart.Start(new ProcessStartInfo(), _ => throw new Win32Exception(2)));
        Assert.Throws<InvalidOperationException>(() => ElevatedRestart.Start(new ProcessStartInfo(), _ => null));
    }

    [Fact]
    public void SuccessfulLaunchReturnsTrue()
    {
        Assert.True(ElevatedRestart.Start(new ProcessStartInfo(), _ => Process.GetCurrentProcess()));
    }

    [Fact]
    public void ExecutableRestartUsesRunAsAndParentIdentity()
    {
        var info = ElevatedRestart.CreateStartInfo(@"C:\Program Files\Forza\ForzaHaptics.exe", "", 123, 456);
        Assert.True(info.UseShellExecute);
        Assert.Equal("runas", info.Verb);
        Assert.Equal(@"C:\Program Files\Forza\ForzaHaptics.exe", info.FileName);
        Assert.Equal(new[] { ElevatedRestart.EnableArgument, "--wait-for-parent", "123", "--parent-start-time", "456" }, info.ArgumentList);
        Assert.Equal(new ElevatedRestart.Request(123, 456), ElevatedRestart.Parse(info.ArgumentList.ToArray()));
    }

    [Fact]
    public void DotnetRestartPlacesAssemblyBeforeInternalArguments()
    {
        var info = ElevatedRestart.CreateStartInfo(@"C:\Program Files\dotnet\dotnet.exe", @"D:\App Folder\ForzaHaptics.dll", 123, 456);
        Assert.Equal(@"D:\App Folder\ForzaHaptics.dll", info.ArgumentList[0]);
        Assert.Equal(new ElevatedRestart.Request(123, 456), ElevatedRestart.Parse(info.ArgumentList.Skip(1).ToArray()));
    }

    [Fact]
    public void OrdinaryLaunchNeverEnablesImpulseTriggers()
    {
        Assert.False(ElevatedRestart.Prepare([], true, _ => throw new Exception("Should not wait")));
        Assert.False(ElevatedRestart.Prepare([], false, _ => throw new Exception("Should not wait")));
    }

    [Fact]
    public void RestartRequiresAdministratorBeforeWaiting()
    {
        string[] args = [ElevatedRestart.EnableArgument, "--wait-for-parent", "123", "--parent-start-time", "456"];
        bool waited = false;
        Assert.Throws<InvalidOperationException>(() => ElevatedRestart.Prepare(args, false, _ => waited = true));
        Assert.False(waited);
        Assert.True(ElevatedRestart.Prepare(args, true, parent =>
        {
            Assert.Equal(new ElevatedRestart.Request(123, 456), parent);
            waited = true;
        }));
        Assert.True(waited);
    }

    [Fact]
    public void FailedParentWaitDoesNotEnableImpulseTriggers()
    {
        string[] args = [ElevatedRestart.EnableArgument, "--wait-for-parent", "123", "--parent-start-time", "456"];
        Assert.Throws<TimeoutException>(() => ElevatedRestart.Prepare(args, true, _ => throw new TimeoutException()));
    }

    [Fact]
    public void MalformedRestartArgumentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ElevatedRestart.Parse([ElevatedRestart.EnableArgument]));
        Assert.Throws<ArgumentException>(() => ElevatedRestart.Parse([ElevatedRestart.EnableArgument, "--wait-for-parent", "0", "--parent-start-time", "456"]));
    }

    [Fact]
    public void ParentWaitTimesOutWhileParentIsAlive()
    {
        using var current = Process.GetCurrentProcess();
        var parent = new ElevatedRestart.Request(current.Id, current.StartTime.ToUniversalTime().Ticks);
        Assert.Throws<TimeoutException>(() => ElevatedRestart.WaitForParent(parent, TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void ParentWaitDoesNotWaitForReusedProcessId()
    {
        using var current = Process.GetCurrentProcess();
        ElevatedRestart.WaitForParent(new(current.Id, 1), TimeSpan.Zero);
    }

    [Fact]
    public void ParentWaitAcceptsAlreadyExitedProcess()
    {
        ElevatedRestart.WaitForParent(new(int.MaxValue, 1), TimeSpan.Zero);
    }
}
