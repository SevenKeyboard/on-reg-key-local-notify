//==============================================================
// OnRegKeyLocalNotify — Registers local registry key change callbacks with asynchronous notification handling
//
// GitHub: https://github.com/SevenKeyboard/on-reg-key-local-notify
// Author: SevenKeyboard Ltd. (2026)
// License: MIT License
//==============================================================

using System.ComponentModel;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SevenKeyboard;

public sealed partial class OnRegKeyLocalNotify : IDisposable
{
    private const int KeyNotify = 0x0010;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorKeyDeleted = 1018;
    private const uint NotifyName = 0x00000001;
    private const uint NotifyLastSet = 0x00000004;
    private const uint NotifyThreadAgnostic = 0x10000000;

    private readonly nint _root;
    private readonly int _access;
    private readonly string[] _paths;
    private readonly Action _changed;
    private readonly bool _watchSubtree;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEvent _stop;
    private readonly Thread _thread;
    private Registration _registration;
    private bool _finished;

    /// <summary>Completes after cleanup; faults on a native error or an uncaught callback exception.</summary>
    /// <remarks>Each subscription is independent. Uncaught callback errors terminate it;
    /// catch errors inside the callback to continue receiving notifications. Do not wait for
    /// this task inside the callback: cleanup follows callback return.</remarks>
    public Task Completion => _completion.Task;

    /// <summary>Starts watching a local key without creating it. No initial callback is promised.</summary>
    /// <remarks>
    /// Callbacks run sequentially per subscription on a background thread and may start before
    /// construction returns. Read current values in the callback; native value-change and
    /// deletion/recreation notifications coalesce rather than reporting every operation.
    /// Missing keys are never created: the nearest existing ancestor is watched until creation.
    /// Ancestor or sibling changes may wake the watcher without invoking the callback.
    /// Notification access to the target and missing-key ancestors is required; startup errors throw.
    /// Renaming the target or an ancestor is unsupported; dispose and subscribe again afterward.
    /// RegistryView.Default uses the process view; choose Registry32 or Registry64 explicitly
    /// to avoid bitness-dependent views. Subscriptions have independent callback ordering.
    /// </remarks>
    public OnRegKeyLocalNotify(RegistryHive root, string path, RegistryView view, Action changed)
        : this(root, path, view, changed, watchSubtree: false) { }

    /// <summary>Starts watching values in the key and, optionally, all its descendants.</summary>
    /// <remarks>The same lifetime and recovery rules apply as for the four-argument constructor.</remarks>
    public OnRegKeyLocalNotify(RegistryHive root, string path, RegistryView view, Action changed, bool watchSubtree)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(changed);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException();
        if (root is not (RegistryHive.ClassesRoot or RegistryHive.CurrentUser or RegistryHive.LocalMachine
            or RegistryHive.Users or RegistryHive.CurrentConfig))
            throw new ArgumentOutOfRangeException(nameof(root));
        if (view is not (RegistryView.Default or RegistryView.Registry32 or RegistryView.Registry64))
            throw new ArgumentOutOfRangeException(nameof(view));

        string[] parts = path.Split('\\');
        if (parts.Any(part => part.Length is 0 or > 255) || path.Contains('\0'))
            throw new ArgumentException("Expected a nonempty relative registry key path.", nameof(path));

        // Predefined HKEY constants are sign-extended on x64, not zero-extended.
        _root = (nint)(int)root;
        _access = KeyNotify | (int)view;
        _changed = changed;
        _watchSubtree = watchSubtree;
        _paths = new string[parts.Length + 1];
        _paths[0] = "";
        for (int i = 0; i < parts.Length; i++)
            _paths[i + 1] = i == 0 ? parts[i] : _paths[i] + "\\" + parts[i];

        _registration = FindClosest();
        try
        {
            _stop = new ManualResetEvent(false);
            try
            {
                _thread = new Thread(Run) { IsBackground = true, Name = nameof(OnRegKeyLocalNotify) };
                _thread.Start();
            }
            catch
            {
                _stop.Dispose();
                throw;
            }
        }
        catch
        {
            _registration.Dispose();
            throw;
        }
    }

    private Registration FindClosest()
    {
        for (int depth = _paths.Length - 1; depth >= 0; depth--)
        {
            int error = TryOpen(depth, out Registration? registration);
            if (error == 0)
                return MoveCloser(registration!);
            if (!IsMissing(error) || depth == 0)
                throw new Win32Exception(error);
        }
        throw new InvalidOperationException();
    }

    private Registration MoveCloser(Registration registration)
    {
        try
        {
            while (!registration.IsTarget)
            {
                // The ancestor is already armed before probing its child. A racing
                // creation/deletion either opens here or signals that ancestor.
                int error = TryOpen(registration.Depth + 1, out Registration? child);
                if (IsMissing(error))
                    return registration;
                if (error != 0)
                    throw new Win32Exception(error);

                registration.Dispose();
                registration = child!;
            }
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    private int TryOpen(int depth, out Registration? registration)
    {
        registration = null;
        SafeRegistryHandle key;
        // An empty subkey gives an owned handle to the existing hive. Never pass
        // the watched path to RegCreateKeyEx: missing keys must remain missing.
        int error = depth == 0
            ? RegCreateKeyEx(_root, "", 0, 0, 0, _access, 0, out key, out _)
            : RegOpenKeyEx(_root, _paths[depth], 0, _access, out key);
        if (error != 0)
        {
            key.Dispose();
            return error;
        }

        Registration candidate;
        try
        {
            candidate = new Registration(key, depth, depth == _paths.Length - 1, _watchSubtree);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        try
        {
            error = candidate.Arm();
            if (error == 0)
            {
                registration = candidate;
                return 0;
            }
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
        candidate.Dispose();
        return error;
    }

    private void Run()
    {
        Exception? failure = null;
        try
        {
            WaitHandle[] waits = [_stop, _registration.Signal];
            while (WaitHandle.WaitAny(waits) == 1)
            {
                bool notify = _registration.IsTarget;
                int error = _registration.Arm();
                if (IsMissing(error))
                {
                    // Deleted handles never refer to a replacement at the same path.
                    _registration.Dispose();
                    _registration = FindClosest();
                }
                else
                {
                    if (error != 0)
                        throw new Win32Exception(error);
                    _registration = MoveCloser(_registration);
                }
                notify |= _registration.IsTarget;
                waits[1] = _registration.Signal;

                // Re-arm before invoking user code, so changes during a callback
                // remain pending. One dedicated thread serializes this subscription.
                if (_stop.WaitOne(0))
                    break;
                if (notify)
                    _changed();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _registration.Dispose();
            lock (_gate)
            {
                _finished = true;
                _stop.Dispose();
            }
            if (failure is null)
                _completion.SetResult();
            else
                _completion.SetException(failure);
        }
    }

    /// <summary>Stops and waits for cleanup, except when called by this subscription's callback.</summary>
    /// <remarks>Repeatable; does not rethrow terminal errors. From the callback, requests stop
    /// and cleanup follows callback return. Do not block the callback on a thread calling Dispose.</remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (!_finished)
                _stop.Set();
        }
        if (Thread.CurrentThread != _thread)
            _thread.Join();
    }

    private static bool IsMissing(int error) =>
        error is ErrorFileNotFound or ErrorPathNotFound or ErrorKeyDeleted;

    private sealed class Registration(SafeRegistryHandle key, int depth, bool isTarget, bool watchSubtree) : IDisposable
    {
        public AutoResetEvent Signal { get; } = new(false);
        public int Depth { get; } = depth;
        public bool IsTarget { get; } = isTarget;

        public int Arm() => RegNotifyChangeKeyValue(key, !IsTarget || watchSubtree ? 1 : 0,
            (IsTarget ? NotifyLastSet : NotifyName) | NotifyThreadAgnostic, Signal.SafeWaitHandle, 1);

        public void Dispose()
        {
            // Closing a key can signal its pending event; keep the event alive until then.
            key.Dispose();
            Signal.Dispose();
        }
    }
}
