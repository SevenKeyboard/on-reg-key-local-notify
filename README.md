# on-reg-key-local-notify
Local registry change notifications for .NET 10 on Windows 10+.

```csharp
using Microsoft.Win32;
using SevenKeyboard;

using var watcher = new OnRegKeyLocalNotify(RegistryHive.CurrentUser,
    @"Software\Example\Settings", RegistryView.Registry64, () => Console.WriteLine("Settings changed"));
Console.ReadLine(); // Keep the subscription alive until Enter; then dispose it.
```

Callbacks run sequentially in the background, with no initial notification.
Pass `watchSubtree: true` as the fifth argument to include descendant value changes.
Missing or recreated keys are watched without creating them; rename requires resubscription.
Dispose when finished and observe `Completion` for terminal errors. See the API comments for lifetime details.
