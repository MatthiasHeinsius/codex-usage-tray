using System.Diagnostics;

namespace CodexUsageTray.Tests;

public sealed class ProgramTests
{
    [Fact]
    public async Task SecondProcessExitsWithoutDisturbingTheExistingInstanceMarker()
    {
        var holderStart = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "CodexUsageTray.ProcessFixture.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        holderStart.ArgumentList.Add("single-instance");
        holderStart.ArgumentList.Add("Local\\CodexUsageTray.SingleInstance");
        using var holder = Process.Start(holderStart)!;
        Process? second = null;
        try
        {
            Assert.Equal("ready", await holder.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            second = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "CodexUsageTray.exe"),
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            await second.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(0, second.ExitCode);
            Assert.False(holder.HasExited);
            using var marker = Mutex.OpenExisting("Local\\CodexUsageTray.SingleInstance");
            await holder.StandardInput.WriteLineAsync();
            await holder.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, holder.ExitCode);
        }
        finally
        {
            if (second is not null)
            {
                if (!second.HasExited)
                {
                    second.Kill(entireProcessTree: true);
                    await second.WaitForExitAsync(CancellationToken.None);
                }
                second.Dispose();
            }
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
