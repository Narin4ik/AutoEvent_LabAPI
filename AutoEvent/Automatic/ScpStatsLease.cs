using System;

namespace AutoEvent.Automatic;

internal sealed class ScpStatsLease
{
    private readonly Func<string, bool> _execute;
    internal bool NeedsRestore { get; private set; }
    internal ScpStatsLease(Func<string, bool> execute) => _execute = execute;
    internal bool Disable()
    {
        if (NeedsRestore) return true;
        return NeedsRestore = _execute("disable");
    }
    internal bool Restore()
    {
        if (!NeedsRestore) return true;
        if (!_execute("enable")) return false;
        NeedsRestore = false;
        return true;
    }
}
