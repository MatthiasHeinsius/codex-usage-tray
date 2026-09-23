using System.Globalization;

// Keep the fixture independent of the test runner and scripting engines.
// The hold mode deliberately never reads stdin, so a large parent write fills the pipe.
switch (args)
{
    case ["single-instance", var mutexName]:
        using (var mutex = new Mutex(false, mutexName))
        {
            Console.WriteLine("ready");
            Console.Out.Flush();
            // Keep the instance marker alive until the test releases it, with bounded cleanup on failure.
            await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            GC.KeepAlive(mutex);
        }
        return 0;

    case ["hold", var pidPath]:
        File.WriteAllText(pidPath + ".tmp", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        File.Move(pidPath + ".tmp", pidPath);
        Console.WriteLine("ready");
        Console.Out.Flush();
        // A failed test cannot leave the fixture alive indefinitely.
        Thread.Sleep(TimeSpan.FromSeconds(20));
        return 0;

    case ["delay"]:
        Thread.Sleep(TimeSpan.FromMilliseconds(800));
        return 0;

    default:
        Console.Error.WriteLine("Usage: CodexUsageTray.ProcessFixture hold <pid-path> | delay | single-instance <mutex-name>");
        return 2;
}
