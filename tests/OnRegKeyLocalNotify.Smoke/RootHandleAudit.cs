using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SevenKeyboard;

internal static class RootHandleAudit
{
    public static void Run()
    {
        // Fixed empty subkeys open existing hives only, with KEY_NOTIFY access.
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine,
            RegistryHive.ClassesRoot, RegistryHive.Users, RegistryHive.CurrentConfig })
        {
            using SafeRegistryHandle first = Open(hive);
            using SafeRegistryHandle second = Open(hive);
            if (first.DangerousGetHandle() == second.DangerousGetHandle())
                throw new InvalidOperationException("Root handles are shared.");
        }

        using SafeRegistryHandle firstRoot = Open(RegistryHive.CurrentUser);
        using SafeRegistryHandle secondRoot = Open(RegistryHive.CurrentUser);
        using var firstSignal = new ManualResetEvent(false);
        using var secondSignal = new ManualResetEvent(false);
        try
        {
            // Observe root values only; no writes and no subtree name monitoring.
            Arm(firstRoot, firstSignal);
            Arm(secondRoot, secondSignal);
            if (firstSignal.WaitOne(0) || secondSignal.WaitOne(0))
                throw new InvalidOperationException("Concurrent root activity made the close test inconclusive.");
            firstRoot.Dispose();
            if (!firstSignal.WaitOne(TimeSpan.FromSeconds(2)) || secondSignal.WaitOne(0))
                throw new InvalidOperationException("Closing one root did not cancel only its own registration.");
            secondRoot.Dispose();
            if (!secondSignal.WaitOne(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("Closing the second root did not cancel its registration.");
        }
        finally
        {
            firstRoot.Dispose();
            secondRoot.Dispose();
        }

        // Exercise the library's depth-zero fallback without creating the missing key.
        string missingPath = "SevenKeyboard.Tests." + Guid.NewGuid().ToString("N");
        using var watcher = new OnRegKeyLocalNotify(RegistryHive.CurrentUser, missingPath, RegistryView.Registry64,
            () => throw new InvalidOperationException("Absent key unexpectedly notified."));
        using (RegistryKey? missing = Registry.CurrentUser.OpenSubKey(missingPath))
        {
            if (missing is not null)
                throw new InvalidOperationException("Root fallback created a key.");
        }
        watcher.Dispose();
        watcher.Dispose();
        watcher.Completion.GetAwaiter().GetResult();
        Console.WriteLine("PASS: existing-root handles are independent; close cancels only its own notification; depth-zero Dispose.");
    }

    private static SafeRegistryHandle Open(RegistryHive hive)
    {
        nint root = (nint)(int)hive;
        int error = RegCreateKeyEx(root, "", 0, 0, 0, 0x0010 | (int)RegistryView.Registry64, 0,
            out nint handle, out uint disposition);
        if (error != 0)
            throw new Win32Exception(error);
        var result = new SafeRegistryHandle(handle, ownsHandle: handle != root);
        if (handle != root && disposition == 2)
            return result;
        result.Dispose();
        throw new InvalidOperationException("Expected an owned handle with REG_OPENED_EXISTING_KEY.");
    }

    private static void Arm(SafeRegistryHandle key, EventWaitHandle signal)
    {
        int error = RegNotifyChangeKeyValue(key, 0, 0x10000004, signal.SafeWaitHandle, 1);
        if (error != 0)
            throw new Win32Exception(error);
    }

    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegCreateKeyEx(nint key, string subKey, uint reserved, nint keyClass,
        uint options, int access, nint securityAttributes, out nint result, out uint disposition);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, int subtree, uint filter,
        SafeWaitHandle signal, int asynchronous);
}
