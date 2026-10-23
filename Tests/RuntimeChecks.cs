using AutoEvent.API;
using AutoEvent.Automatic;
using AutoEvent.Interfaces;
using LabApi.Features.Wrappers;
using MEC;
using UserSettings.ServerSpecific;
using CorePlugin = AutoEvent.AutoEvent;
using Finale = AutoEvent.Games.NukeRun.Plugin;
using Jail = AutoEvent.Games.Jail.Plugin;

internal static class RuntimeChecks
{
    private sealed class MissingMap : Event, IEventMap
    {
        public MapInfo MapInfo { get; } = new() { MapName = "missing" };
    }
    private sealed class TooManyPlayers : Event, IPlayerCountLimited { public int MaxPlayers => 2; }
    private sealed class MissingPlugin : Event, IRequiresPlugins
    {
        public IEnumerable<string> RequiredPlugins => ["NotInstalled"];
        public IEnumerable<string> RequiredDependencies => [];
    }
    private sealed class Rig : IDisposable
    {
        internal readonly Event First = new() { CommandName = "dm" };
        internal readonly Event Second = new() { CommandName = "race" };
        internal readonly Finale Finale = new();
        internal readonly Jail Jail = new();
        internal CorePlugin Plugin { get; }
        internal AutoVotingSystem System => Plugin.VotingSystem;
        internal CommandSystem.FakeCommands Stats => GameCore.Console.ConsoleCommandHandler;
        internal Rig(Action<CorePlugin> configure = null, Action beforeStart = null)
        {
            Timing.Reset();
            Round.IsLocked = Round.IsLobbyLocked = Round.IsRoundStarted = false;
            Round.Restarts = 0;
            EventManager.CurrentEvent = null;
            EventManager.Events = [First, Second, Jail, Finale];
            Player.ReadyList = Enumerable.Range(0, 20).Select(i => new Player { UserId = $"p{i}" }).ToList();
            ServerSpecificSettingsSync.DefinedSettings = [new SSGroupHeader(999, "Other plugin")];
            GameCore.Console.ConsoleCommandHandler = new();
            Plugin = new CorePlugin();
            CorePlugin.Singleton = Plugin;
            configure?.Invoke(Plugin);
            beforeStart?.Invoke();
            Plugin.VotingSystem = new AutoVotingSystem(Plugin);
        }
        internal void Yes() => ServerSpecificSettingsSync.Vote("p0", true);
        internal void Start()
        {
            Yes();
            Timing.Advance(2.01f);
            Round.IsRoundStarted = true;
            System.OnServerRoundStarted();
            Timing.Advance(2.01f);
        }
        internal void Complete(Event ev) { ev.Complete(); Timing.Advance(0.11f); }
        internal void Vote(int yes, int no)
        {
            for (int i = 0; i < yes + no; i++) ServerSpecificSettingsSync.Vote($"p{i}", i < yes);
            Timing.Advance(2.11f);
        }
        public void Dispose() { System.Dispose(); CorePlugin.Singleton = null; }
    }

    internal static void Run(Action<bool, string> check)
    {
        using (var r = new Rig())
        {
            check(Round.IsLobbyLocked, "Runtime: initial vote locks lobby");
            check(ServerSpecificSettingsSync.DefinedSettings.Any(s => s.SettingId == 999), "Runtime: preserve other settings");
            r.Start();
            check(r.First.Starts == 1 && Round.IsLocked && !Round.IsLobbyLocked, "Runtime: first game and owned round lock");
            check(r.Stats.Calls.SequenceEqual(["disable"]), "Runtime: disable before first game");
            check(!AutomaticEvents.TryBeginExternalActivity(), "Runtime: AutoRun and Vote cannot reserve active chain");
            bool refused = false;
            try { new Event().StartEvent(); } catch (InvalidOperationException) { refused = true; }
            check(refused, "Runtime: direct external event cannot steal chain");
            r.Complete(r.First);
            check(EventManager.CurrentEvent == null && Round.IsLocked && r.System.Status.Contains("ContinuationVote"), "Runtime: cleanup then continuation vote");
            check(Player.ReadyList.All(p => p.Role == r.Plugin.Config.LobbyRole), "Runtime: players return to lobby role");
            r.Vote(13, 7);
            check(r.Second.Starts == 1 && r.First.Starts == 1 && r.Jail.Starts == 0, "Runtime: exactly 65% starts unplayed random game");
            r.Complete(r.Second);
            check(r.Finale.Starts == 1 && r.Jail.Starts == 0, "Runtime: exhausted pool directly starts finale");
            r.Complete(r.Finale);
            Timing.Advance(10);
            check(Round.Restarts == 1 && !Round.IsLocked, "Runtime: finale restarts once after cleanup");
            check(r.Stats.Calls.SequenceEqual(["disable"]), "Runtime: statistics remain disabled through votes and finale");
            r.System.OnServerWaitingForPlayers();
            r.System.OnServerWaitingForPlayers();
            check(r.Stats.Calls.SequenceEqual(["disable", "enable"]), "Runtime: next lobby restores statistics once");
        }
        using (var r = new Rig())
        {
            r.Start(); r.Complete(r.First); r.Vote(9, 5);
            check(r.Finale.Starts == 1 && r.Second.Starts == 0, "Runtime: 64.28% goes straight to finale");
            r.Complete(r.Finale);
            check(Round.Restarts == 1, "Runtime: low-support finale restarts");
        }
        using (var r = new Rig())
        {
            r.Start(); r.Complete(r.First); r.Vote(0, 0);
            check(r.Finale.Starts == 1, "Runtime: empty continuation vote ends chain");
        }
        using (var r = new Rig())
        {
            Timing.Advance(3);
            Round.IsRoundStarted = true; r.System.OnServerRoundStarted(); Timing.Advance(3);
            check(r.First.Starts == 0 && r.Stats.Calls.Count == 0 && !Round.IsLobbyLocked, "Runtime: empty initial vote keeps ordinary round and stats");
        }
        foreach (bool reset in new[] { false, true })
        using (var r = new Rig())
        {
            r.Start();
            if (reset) r.System.Reset(); else r.System.SetEnabled(false);
            check(Round.IsLocked, "Runtime: admin cancellation retains lock until active cleanup");
            r.Complete(r.First); Timing.Advance(5);
            check(!Round.IsLocked && r.Second.Starts == 0 && r.Finale.Starts == 0 && Round.Restarts == 0, "Runtime: off/reset cancels chain after cleanup");
            check(r.Stats.Calls.SequenceEqual(["disable"]), "Runtime: admin cancellation restores stats only at next lobby");
        }
        using (var r = new Rig())
        {
            r.Start(); r.First.StopEvent(); r.Complete(r.First); Timing.Advance(3);
            check(!Round.IsLocked && r.Finale.Starts == 0 && r.Second.Starts == 0, "Runtime: manual stop cancels continuation");
        }
        using (var r = new Rig())
        {
            r.Start(); r.Complete(r.First); r.Yes(); r.System.SetEnabled(false); Timing.Advance(5);
            check(!Round.IsLocked && r.Finale.Starts == 0 && r.Second.Starts == 0, "Runtime: off invalidates continuation timer");
        }
        using (var r = new Rig())
        {
            r.Yes(); Timing.Advance(2.01f);
            check(AutomaticEvents.TryBeginExternalActivity(), "Runtime: manual start can cancel pending round");
            Round.IsRoundStarted = true; r.System.OnServerRoundStarted(); Timing.Advance(5);
            check(r.First.Starts == 0 && r.Stats.Calls.Count == 0, "Runtime: pending auto start never runs after manual activity");
        }
        using (var r = new Rig())
        {
            r.Yes(); Timing.Advance(2.01f);
            Round.IsRoundStarted = true; r.System.OnServerRoundStarted();
            r.System.OnServerRoundRestarted(); Timing.Advance(5);
            check(r.First.Starts == 0 && !Round.IsLocked, "Runtime: restart invalidates delayed first start");
        }
        using (var r = new Rig())
        {
            r.Start(); r.System.OnServerRoundRestarted(); r.Complete(r.First); Timing.Advance(5);
            check(r.Second.Starts == 0 && r.Finale.Starts == 0 && !Round.IsLocked, "Runtime: restart detaches old cleanup");
            r.System.OnServerWaitingForPlayers();
            check(r.Stats.Calls.Last() == "enable", "Runtime: restart recovers stats in lobby");
        }
        foreach (string failure in new[] { "missing", "refused", "exception" })
        using (var r = new Rig())
        {
            r.Stats.Available = failure != "missing";
            r.Stats.FailDisable = failure == "refused";
            r.Stats.Throw = failure == "exception";
            r.Start();
            check(r.First.Starts == 0 && !Round.IsLocked, $"Runtime: SCPStats {failure} aborts chain");
        }
        using (var r = new Rig())
        {
            r.First.FailOnStart = true;
            r.Start();
            check(r.Stats.Calls.SequenceEqual(["disable", "enable"]), "Runtime: first startup failure restores stats immediately");
            r.Complete(r.First);
            check(!Round.IsLocked && Round.Restarts == 0 && r.Finale.Starts == 0, "Runtime: first startup failure cleans before unlocking");
        }
        using (var r = new Rig())
        {
            r.First.StopOnStart = true; r.Start(); r.Complete(r.First);
            check(r.Stats.Calls.SequenceEqual(["disable", "enable"]) && r.Finale.Starts == 0, "Runtime: swallowed startup error that stops event is detected");
        }
        using (var r = new Rig())
        {
            r.Start(); r.Complete(r.First); r.Second.FailOnStart = true; r.Vote(13, 7); r.Complete(r.Second);
            check(Round.Restarts == 1 && r.Stats.Calls.SequenceEqual(["disable"]), "Runtime: later startup failure restarts with stats still disabled");
        }
        using (var r = new Rig())
        {
            r.Start(); r.Complete(r.First); r.Finale.FailOnStart = true; r.Vote(0, 0); r.Complete(r.Finale);
            check(Round.Restarts == 1 && !Round.IsLocked, "Runtime: failed finale also restarts after cleanup");
        }
        using (var r = new Rig())
        {
            EventManager.Events.Remove(r.Finale); r.Start();
            check(r.First.Starts == 0 && r.Stats.Calls.Count == 0 && !Round.IsLocked, "Runtime: preflight requires final Nuke Run");
        }
        using (var r = new Rig())
        {
            EventManager.Events = [r.Jail, r.Finale, new MissingMap { CommandName = "map" },
                new TooManyPlayers { CommandName = "limited" }, new MissingPlugin { CommandName = "dependencies" }];
            r.Start();
            check(EventManager.Events.All(e => e.Starts == 0) && r.Stats.Calls.Count == 0 && !Round.IsLocked,
                "Runtime: no runnable candidates means no jail/finale as first game and no stats disable");
        }
        using (var r = new Rig(p => p.Config.AutoVoting.ExcludedEvents = ["dm", " RACE "]))
        {
            r.Start();
            check(r.First.Starts == 0 && r.Second.Starts == 0 && r.Finale.Starts == 0, "Runtime: configured exclusions cannot force forbidden fallback");
        }
        using (var r = new Rig())
        {
            r.Yes(); r.System.OnPlayerLeft(new() { Player = Player.ReadyList[0] }); Timing.Advance(3);
            Round.IsRoundStarted = true; r.System.OnServerRoundStarted(); Timing.Advance(3);
            check(r.First.Starts == 0, "Runtime: disconnected initial voter no longer counts");
        }
        using (var r = new Rig())
        {
            r.Start(); r.Complete(r.First);
            for (int i = 0; i < 20; i++) ServerSpecificSettingsSync.Vote($"p{i}", i < 13);
            r.System.OnPlayerLeft(new() { Player = Player.ReadyList[0] }); Timing.Advance(3);
            check(r.Finale.Starts == 1 && r.Second.Starts == 0, "Runtime: departing yes voter changes continuation below 65%");
        }
        using (var r = new Rig())
        {
            r.Start(); r.Complete(r.First); r.Vote(0, 0);
            r.Finale.Complete(); r.Finale.Complete(); Timing.Advance(5);
            check(Round.Restarts == 1, "Runtime: duplicate finale cleanup does not duplicate restart");
        }
        using (var r = new Rig(p =>
        {
            p.Config.AutoVoting.RoundInterval = 0;
            p.Config.AutoVoting.VoteDurationSeconds = 0;
            p.Config.AutoVoting.BroadcastIntervalSeconds = float.NaN;
            p.Config.AutoVoting.BroadcastDurationSeconds = float.PositiveInfinity;
            p.Config.AutoVoting.VoteBroadcastText = " ";
            p.Config.AutoVoting.ExcludedEvents = null;
        }))
        {
            var c = r.Plugin.Config.AutoVoting;
            check(c.RoundInterval == 4 && c.VoteDurationSeconds == 45 && c.BroadcastIntervalSeconds == 1f &&
                c.BroadcastDurationSeconds == 1.5f && !string.IsNullOrWhiteSpace(c.VoteBroadcastText) && c.ExcludedEvents != null,
                "Runtime: invalid configuration restores usable defaults");
        }
        using (var r = new Rig())
        {
            r.Start(); r.Stats.FailEnable = true; r.System.OnServerWaitingForPlayers();
            r.Stats.FailEnable = false; r.System.Dispose();
            check(r.Stats.Calls.SequenceEqual(["disable", "enable", "enable"]), "Runtime: unload retries unsuccessful lobby restoration");
            check(ServerSpecificSettingsSync.DefinedSettings.Length == 1 && ServerSpecificSettingsSync.DefinedSettings[0].SettingId == 999, "Runtime: unload removes only owned settings");
            Timing.Advance(5);
            check(r.Finale.Starts == 0, "Runtime: unload cancels pending callbacks");
        }
        using (var r = new Rig(beforeStart: () => ServerSpecificSettingsSync.DefinedSettings = [new SSGroupHeader(6102410, "Conflict")]))
        {
            check(!r.System.SetEnabled(true) && !Round.IsLobbyLocked, "Runtime: setting-id conflict blocks enabling");
        }
        using (var r = new Rig(p => p.Config.AutoVoting.Enabled = false))
        {
            check(!Round.IsLobbyLocked && r.System.SetEnabled(true) && Round.IsLobbyLocked, "Runtime: initially disabled system can be enabled with registered buttons");
        }
        Func<bool> external = () => true;
        AutomaticEvents.ExternalActivityRunning += external;
        try
        {
            using var r = new Rig();
            check(!Round.IsLobbyLocked, "Runtime: active AutoRun/Vote suppresses periodic vote");
        }
        finally { AutomaticEvents.ExternalActivityRunning -= external; }
    }
}
