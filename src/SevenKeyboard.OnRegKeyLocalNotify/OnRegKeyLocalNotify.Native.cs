using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SevenKeyboard;

public sealed partial class OnRegKeyLocalNotify
{
    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyEx(nint key, string path, int options, int access,
        out SafeRegistryHandle result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyEx(nint key, string subKey, uint reserved, nint keyClass,
        uint options, int access, nint securityAttributes, out SafeRegistryHandle result, out uint disposition);

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(SafeRegistryHandle key, int watchSubtree,
        uint filter, SafeWaitHandle signal, int asynchronous);
}
