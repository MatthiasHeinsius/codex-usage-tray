using System.Globalization;
using System.Text.Json;

// Keep the fixture independent of the test runner and scripting engines.
// The hold mode leaves the app-server exchange blocked on a read.
switch (args)
{
    case ["app-server", var markerPath]:
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                continue;
            }

            var method = root.GetProperty("method").GetString();
            if (method == "fail-once" && !File.Exists(markerPath))
            {
                File.WriteAllText(markerPath, "failed");
                return 0;
            }
            if (method == "auth-failure")
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    id = id.GetInt32(),
                    error = new { message = "Provided authentication token is expired" }
                }));
                Console.WriteLine("""{"id":2,"result":{"stale":true}}""");
                continue;
            }
            if (method == "hang")
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                continue;
            }
            if (method == "oversized-line")
            {
                Console.WriteLine(new string('x', 1_048_577));
                continue;
            }
            if (method == "long-diagnostic")
            {
                Console.Error.WriteLine(new string('x', 5000) + "end");
                Console.Error.Flush();
            }

            if (method == "model/list")
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    id = id.GetInt32(),
                    result = new { data = new[] { new { model = "gpt-6-luna", isDefault = true } } }
                }));
                continue;
            }

            if (method == "thread/start")
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    id = id.GetInt32(),
                    result = new { thread = new { id = "activation-thread", ephemeral = true } }
                }));
                continue;
            }

            if (method == "turn/start")
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    id = id.GetInt32(),
                    result = new { turn = new { id = "activation-turn", status = "inProgress" } }
                }));
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    method = "turn/completed",
                    @params = new
                    {
                        threadId = "activation-thread",
                        turn = new { id = "activation-turn", status = "completed" }
                    }
                }));
                continue;
            }

            if (method == "thread/unsubscribe")
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    id = id.GetInt32(),
                    result = new { status = "unsubscribed" }
                }));
                continue;
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                id = id.GetInt32(),
                result = new { pid = Environment.ProcessId }
            }));
        }
        return 0;

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

    default:
        Console.Error.WriteLine("Usage: CodexUsageTray.ProcessFixture app-server <marker-path> | hold <pid-path> | single-instance <mutex-name>");
        return 2;
}
