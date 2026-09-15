using System.Globalization;

// Keep the fixture independent of the test runner and scripting engines.
// It deliberately never reads stdin, so a large parent write fills the pipe.
switch (args)
{
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
        Console.Error.WriteLine("Usage: CodexUsageTray.ProcessFixture hold <pid-path> | delay");
        return 2;
}
