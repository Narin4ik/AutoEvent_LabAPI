using System;
using System.Linq;
using AutoEvent.ApiFeatures;

namespace AutoEvent.API;

/// <summary>Coordinates optional event controllers with the periodic automatic chain.</summary>
public static class AutomaticEvents
{
    /// <summary>Optional modules report whether their own controller is running.</summary>
    public static event Func<bool> ExternalActivityRunning;

    public static bool IsChainActive => AutoEvent.Singleton?.VotingSystem?.OwnsChain == true;

    /// <summary>Returns false during an automatic chain; otherwise cancels a pending periodic vote.</summary>
    public static bool TryBeginExternalActivity() => AutoEvent.Singleton?.VotingSystem?.PrepareExternalActivity() ?? true;

    internal static bool IsExternalActivityRunning()
    {
        try { return ExternalActivityRunning?.GetInvocationList().Cast<Func<bool>>().Any(f => f()) == true; }
        catch (Exception e)
        {
            LogManager.Error($"Cannot check external event controller: {e}");
            return true;
        }
    }
}
