using System.Diagnostics;
using Microsoft.Win32;
using SevenKeyboard;

internal static class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static int Main()
    {
        string path = @"Software\SevenKeyboard.Tests\" + Guid.NewGuid().ToString("N");
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        Console.WriteLine($"Temporary key: HKCU\\{path}");
        try
        {
            RootHandleAudit.Run();
            Lifecycle(root, path);
            Subtree(root, path + @"\Subtree", initiallyMissing: false);
            Subtree(root, path + @"\MissingSubtree", initiallyMissing: true);
            SerialAndDispose(root, path + @"\Serial");
            Independent(root, path + @"\Independent");
            CallbackFailure(root, path + @"\Failure");
            Churn(root, path + @"\Churn");
            Console.WriteLine("PASS: creation, values, deletion/recreation, serial callbacks, disposal, exceptions, races.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            using RegistryKey? remaining = root.OpenSubKey(path);
            if (remaining is not null)
                throw new InvalidOperationException("Temporary key cleanup failed.");
            Console.WriteLine("Temporary key removed.");
        }
    }

    private static void Subtree(RegistryKey root, string path, bool initiallyMissing)
    {
        if (!initiallyMissing)
            SetValue(root, path + @"\Child", 0);
        var direct = new Signals();
        var descendants = new Signals();
        using var directWatcher = Watch(path, direct.Add);
        using var subtreeWatcher = new OnRegKeyLocalNotify(RegistryHive.CurrentUser,
            path, RegistryView.Registry64, descendants.Add, watchSubtree: true);
        if (initiallyMissing)
        {
            using (root.CreateSubKey(path)) { }
            direct.WaitAfter(0);
            descendants.WaitAfter(0);
            using (root.CreateSubKey(path + @"\Child")) { }
        }
        int directBefore = direct.Count;
        int descendantBefore = descendants.Count;
        SetValue(root, path + @"\Child", 1);
        descendants.WaitAfter(descendantBefore);
        Thread.Sleep(100);
        Check(direct.Count == directBefore, "Default watcher reported a descendant value.");
        descendantBefore = descendants.Count;
        SetValue(root, path, 2);
        direct.WaitAfter(directBefore);
        descendants.WaitAfter(descendantBefore);

        directBefore = direct.Count;
        descendantBefore = descendants.Count;
        root.DeleteSubKeyTree(path);
        direct.WaitAfter(directBefore);
        descendants.WaitAfter(descendantBefore);
        descendantBefore = descendants.Count;
        using (root.CreateSubKey(path)) { }
        descendants.WaitAfter(descendantBefore);
        using (root.CreateSubKey(path + @"\Child")) { }
        descendantBefore = descendants.Count;
        SetValue(root, path + @"\Child", 3);
        descendants.WaitAfter(descendantBefore);
        Console.WriteLine($"PASS: subtree={true}, initially missing={initiallyMissing}, recovery and unchanged default.");
    }

    private static void Lifecycle(RegistryKey root, string path)
    {
        var signals = new Signals();
        using var watcher = Watch(path + @"\Parent\Target", signals.Add);
        using (RegistryKey? absent = root.OpenSubKey(path))
            Check(absent is null, "Watcher created a key.");

        using (root.CreateSubKey(path + @"\Parent")) { }
        using (root.CreateSubKey(path + @"\Parent\Target")) { }
        signals.WaitAfter(0);
        int before = signals.Count;
        SetValue(root, path + @"\Parent\Target", 1);
        signals.WaitAfter(before);

        before = signals.Count;
        root.DeleteSubKeyTree(path + @"\Parent");
        signals.WaitAfter(before);
        before = signals.Count;
        SetValue(root, path + @"\Parent\Target", 2);
        signals.WaitAfter(before);
        before = signals.Count;
        SetValue(root, path + @"\Parent\Target", 3);
        signals.WaitAfter(before);

        watcher.Dispose();
        watcher.Dispose();
        watcher.Completion.GetAwaiter().GetResult();
        before = signals.Count;
        SetValue(root, path + @"\Parent\Target", 4);
        Check(signals.Count == before, "Callback after Dispose returned.");
        Console.WriteLine("PASS: initially missing key, ancestor removal, recreation and duplicate Dispose.");
    }

    private static void SerialAndDispose(RegistryKey root, string path)
    {
        SetValue(root, path, 0);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var selfDisposed = new ManualResetEventSlim();
        var signals = new Signals();
        int active = 0;
        int calls = 0;
        OnRegKeyLocalNotify? watcher = null;
        watcher = Watch(path, () =>
        {
            Check(Interlocked.Increment(ref active) == 1, "Overlapping callbacks.");
            try
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.Set();
                    Check(release.Wait(Timeout), "Callback release timed out.");
                    // Caller-handled failures must not stop future notifications.
                    try { throw new InvalidOperationException("Handled callback failure."); }
                    catch (InvalidOperationException) { }
                }
                else
                {
                    watcher!.Dispose();
                    watcher.Dispose();
                    selfDisposed.Set();
                }
                signals.Add();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        using (watcher)
        {
            try
            {
                SetValue(root, path, 1);
                Check(entered.Wait(Timeout), "First callback timed out.");
                for (int i = 2; i < 50; i++)
                    SetValue(root, path, i);
                Check(Volatile.Read(ref calls) == 1, "Callbacks were not serialized.");
                release.Set();
                Check(selfDisposed.Wait(Timeout), "Callback Dispose timed out.");
                Check(watcher.Completion.Wait(Timeout), "Self-disposal cleanup timed out.");
                Check(signals.Count == 2, "Unexpected callback count after self-disposal.");
            }
            finally
            {
                release.Set();
            }
        }

        entered.Reset();
        release.Reset();
        using var external = Watch(path, () =>
        {
            entered.Set();
            Check(release.Wait(Timeout), "External disposal release timed out.");
        });
        SetValue(root, path, 100);
        Check(entered.Wait(Timeout), "External disposal callback timed out.");
        Task stop = Task.Run(external.Dispose);
        try
        {
            Check(!stop.Wait(TimeSpan.FromMilliseconds(100)), "Dispose did not wait for callback.");
        }
        finally
        {
            release.Set();
        }
        Check(stop.Wait(Timeout), "External Dispose timed out.");
        external.Completion.GetAwaiter().GetResult();
        Console.WriteLine("PASS: sequential callbacks, handled failure, callback/external Dispose.");
    }

    private static void Independent(RegistryKey root, string path)
    {
        SetValue(root, path, 0);
        var firstSignals = new Signals();
        var secondSignals = new Signals();
        using var first = Watch(path, firstSignals.Add);
        using var second = Watch(path, secondSignals.Add);
        SetValue(root, path, 1);
        firstSignals.WaitAfter(0);
        secondSignals.WaitAfter(0);
        first.Dispose();
        int before = secondSignals.Count;
        SetValue(root, path, 2);
        secondSignals.WaitAfter(before);
        second.Dispose();
        first.Completion.GetAwaiter().GetResult();
        second.Completion.GetAwaiter().GetResult();
        Console.WriteLine("PASS: disposing one subscription leaves the other active.");
    }

    private static void CallbackFailure(RegistryKey root, string path)
    {
        SetValue(root, path, 0);
        var expected = new InvalidOperationException("Expected terminal callback failure.");
        using var watcher = Watch(path, () => throw expected);
        SetValue(root, path, 1);
        try
        {
            watcher.Completion.WaitAsync(Timeout).GetAwaiter().GetResult();
            throw new InvalidOperationException("Uncaught callback exception was lost.");
        }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, expected)) { }
        watcher.Dispose();
        Console.WriteLine("PASS: uncaught callback failure reaches Completion after cleanup.");
    }

    private static void Churn(RegistryKey root, string path)
    {
        // Race construction, ancestor changes and replacement without requiring
        // one callback per write: native notifications intentionally coalesce.
        using var begin = new ManualResetEventSlim();
        Task writer = Task.Run(() =>
        {
            begin.Wait();
            for (int i = 0; i < 100; i++)
            {
                SetValue(root, path + @"\Parent\Target", i);
                root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            }
        });
        begin.Set();
        try
        {
            var signals = new Signals();
            using var watcher = Watch(path + @"\Parent\Target", signals.Add);
            Check(writer.Wait(Timeout), "Race writer timed out.");
            int before = signals.Count;
            SetValue(root, path + @"\Parent\Target", 101);
            signals.WaitAfter(before);
            before = signals.Count;
            SetValue(root, path + @"\Parent\Target", 102);
            signals.WaitAfter(before);
            watcher.Dispose();
            watcher.Completion.GetAwaiter().GetResult();
            Console.WriteLine("PASS: creation/deletion races recover to final key.");
        }
        finally
        {
            // Even construction/assertion failure must finish the writer before
            // Main removes the unique subtree.
            writer.GetAwaiter().GetResult();
        }
    }

    private static OnRegKeyLocalNotify Watch(string path, Action changed) =>
        new(RegistryHive.CurrentUser, path, RegistryView.Registry64, changed);

    private static void SetValue(RegistryKey root, string path, int value)
    {
        using RegistryKey key = root.CreateSubKey(path);
        key.SetValue("Value", value, RegistryValueKind.DWord);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Signals
    {
        private readonly object _gate = new();
        private int _count;
        public int Count { get { lock (_gate) return _count; } }

        public void Add()
        {
            lock (_gate)
            {
                _count++;
                Monitor.PulseAll(_gate);
            }
        }

        public void WaitAfter(int before)
        {
            var elapsed = Stopwatch.StartNew();
            lock (_gate)
            {
                while (_count <= before)
                {
                    TimeSpan remaining = Timeout - elapsed.Elapsed;
                    Check(remaining > TimeSpan.Zero && Monitor.Wait(_gate, remaining), "Notification timed out.");
                }
            }
        }
    }
}
