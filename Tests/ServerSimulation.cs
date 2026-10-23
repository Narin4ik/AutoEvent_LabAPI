// Minimal deterministic server boundary. Tests compile the production controller unchanged.
using AutoEvent.Automatic;
using AutoEvent.Interfaces;
using LabApi.Features.Wrappers;

namespace AutoEvent
{
    public sealed class Config
    {
        public AutoVotingConfig AutoVoting { get; set; } = new() { RoundInterval = 1, VoteDurationSeconds = 2 };
        public List<int> IgnoredRoles { get; set; } = [];
        public int LobbyRole { get; set; } = 1;
    }
    public sealed class AutoEvent
    {
        public static AutoEvent Singleton;
        public Config Config { get; } = new();
        internal AutoVotingSystem VotingSystem { get; set; }
    }
}
namespace AutoEvent.ApiFeatures
{
    internal static class LogManager
    {
        internal static readonly List<string> Messages = [];
        internal static void Info(string s) => Messages.Add(s);
        internal static void Warn(string s) => Messages.Add(s);
        internal static void Error(string s) => Messages.Add(s);
    }
}
namespace AutoEvent.API
{
    public static class EventManager
    {
        public static Event CurrentEvent;
        public static List<Event> Events = [];
    }
    public static class Extensions
    {
        public static bool IsExistsMap(string name, out string response) { response = ""; return name != "missing"; }
    }
}
namespace AutoEvent.Interfaces
{
    public class Event
    {
        public string Name { get; set; } = "Test game";
        public string CommandName { get; set; } = "dm";
        public EventConfig InternalConfig { get; set; } = new();
        public bool FailOnStart, StopOnStart;
        public int Starts, Stops;
        public event Action<string> EventStopped;
        public event Action<string> CleanupFinished;
        public void StartEvent()
        {
            if (AutoEvent.Singleton.VotingSystem?.AllowEventStart(this) == false)
                throw new InvalidOperationException("Automatic chain owns the round");
            API.EventManager.CurrentEvent = this;
            Starts++;
            if (StopOnStart) StopEvent();
            if (FailOnStart) throw new InvalidOperationException("Fake startup failure");
        }
        public void StopEvent() { Stops++; EventStopped?.Invoke(Name); }
        public void Complete()
        {
            CleanupFinished?.Invoke(Name);
            API.EventManager.CurrentEvent = null;
        }
    }
    public sealed class EventConfig { public List<MapChance> AvailableMaps { get; set; } = []; }
    public sealed class MapChance { public string MapName { get; set; } }
    public sealed class MapInfo { public string MapName { get; set; } }
    public interface IEventMap { MapInfo MapInfo { get; } }
    public interface IPlayerCountLimited { int MaxPlayers { get; } }
    public interface IRequiresPlugins { IEnumerable<string> RequiredPlugins { get; } IEnumerable<string> RequiredDependencies { get; } }
}
namespace AutoEvent.Games.Jail { public sealed class Plugin : Event { public Plugin() { CommandName = "jail"; } } }
namespace AutoEvent.Games.NukeRun { public sealed class Plugin : Event { public Plugin() { CommandName = "nukerun"; } } }
namespace LabApi.Events.CustomHandlers
{
    public abstract class CustomEventsHandler
    {
        public virtual void OnServerWaitingForPlayers() { }
        public virtual void OnServerRoundStarted() { }
        public virtual void OnServerRoundRestarted() { }
        public virtual void OnPlayerLeft(LabApi.Events.Arguments.PlayerEvents.PlayerLeftEventArgs ev) { }
    }
    public static class CustomHandlersManager
    {
        public static void RegisterEventsHandler(CustomEventsHandler h) { }
        public static void UnregisterEventsHandler(CustomEventsHandler h) { }
    }
}
namespace LabApi.Events.Arguments.PlayerEvents
{
    public sealed class PlayerLeftEventArgs { public Player Player { get; set; } }
}
public sealed class ReferenceHub { public Player Player { get; set; } }
public static class Broadcast { public enum BroadcastFlags { Normal } }
namespace LabApi.Features.Wrappers
{
    public static class Round
    {
        public static bool IsRoundStarted, IsLocked, IsLobbyLocked;
        public static int Restarts;
        public static void Restart() { Restarts++; }
    }
    public sealed class Player
    {
        public static List<Player> ReadyList = [];
        public bool IsReady { get; set; } = true;
        public bool IsHost { get; set; }
        public string UserId { get; set; }
        public int Role { get; set; }
        public static Player Get(ReferenceHub hub) => hub?.Player;
        public void ClearInventory() { }
        public void SetRole(int role) => Role = role;
    }
    public static class Server
    {
        public static readonly List<string> Broadcasts = [];
        public static void SendBroadcast(string text, ushort seconds, Broadcast.BroadcastFlags flags, bool clear) => Broadcasts.Add(text);
        public static void ClearBroadcasts() { }
    }
}
namespace LabApi.Loader
{
    public sealed class Plugin { public string Name { get; set; } }
    public static class PluginLoader
    {
        public static List<Plugin> EnabledPlugins = [];
        public static List<System.Reflection.Assembly> Dependencies = [];
    }
}
namespace UserSettings.ServerSpecific
{
    public class ServerSpecificSettingBase { public int SettingId { get; set; } }
    public sealed class SSGroupHeader : ServerSpecificSettingBase
    {
        public SSGroupHeader(int id, string text) { SettingId = id; }
    }
    public sealed class SSButton : ServerSpecificSettingBase
    {
        public SSButton(int id, string label, string text, float hold, string hint) { SettingId = id; }
    }
    public static class ServerSpecificSettingsSync
    {
        public static ServerSpecificSettingBase[] DefinedSettings;
        public static event Action<ReferenceHub, ServerSpecificSettingBase> ServerOnSettingValueReceived;
        public static void SendToAll() { }
        public static void Vote(string user, bool yes)
        {
            var player = Player.ReadyList.Single(p => p.UserId == user);
            ServerOnSettingValueReceived?.Invoke(new ReferenceHub { Player = player }, new SSButton(yes ? 6102411 : 6102412, "", "", 0, ""));
        }
    }
}
namespace CommandSystem
{
    public interface ICommandSender { }
    public interface ICommand { bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response); }
    public sealed class FakeCommands : ICommand
    {
        public bool Available = true, FailDisable, FailEnable, Throw;
        public readonly List<string> Calls = [];
        public bool TryGetCommand(string name, out ICommand command) { command = this; return Available; }
        public bool Execute(ArraySegment<string> args, ICommandSender sender, out string response)
        {
            if (Throw) throw new InvalidOperationException("Fake command exception");
            string action = args[0];
            Calls.Add(action);
            response = action;
            return action == "disable" ? !FailDisable : !FailEnable;
        }
    }
}
namespace GameCore { public static class Console { public static CommandSystem.FakeCommands ConsoleCommandHandler = new(); } }
public static class ServerConsole { public static CommandSystem.ICommandSender Scs; }
namespace UnityEngine
{
    public static class Time { public static double realtimeSinceStartupAsDouble; }
    public static class Random { public static int Range(int min, int max) => min; }
}
namespace MEC
{
    public readonly struct CoroutineHandle(int id)
    {
        internal int Id => id;
        public bool IsRunning => Timing.Active.Contains(id);
    }
    public static class Timing
    {
        private static readonly List<(double At, int Id, Action Action)> Queue = [];
        internal static readonly HashSet<int> Active = [];
        private static int _id;
        public static void Reset() { Queue.Clear(); Active.Clear(); _id = 0; UnityEngine.Time.realtimeSinceStartupAsDouble = 0; }
        public static float WaitForSeconds(float seconds) => seconds;
        public static CoroutineHandle RunCoroutine(IEnumerator<float> routine)
        {
            int id = ++_id;
            Active.Add(id);
            void Tick()
            {
                if (!Active.Contains(id)) return;
                if (!routine.MoveNext()) { Active.Remove(id); return; }
                Queue.Add((UnityEngine.Time.realtimeSinceStartupAsDouble + routine.Current, id, Tick));
            }
            Tick();
            return new CoroutineHandle(id);
        }
        public static void CallDelayed(float seconds, Action action) => Queue.Add((UnityEngine.Time.realtimeSinceStartupAsDouble + seconds, 0, action));
        public static void KillCoroutines(CoroutineHandle handle) => Active.Remove(handle.Id);
        public static void Advance(float seconds)
        {
            double target = UnityEngine.Time.realtimeSinceStartupAsDouble + seconds;
            int safety = 0;
            while (Queue.Any(x => x.At <= target))
            {
                if (++safety > 10000) throw new Exception("Timer loop");
                var item = Queue.OrderBy(x => x.At).First(x => x.At <= target);
                Queue.Remove(item);
                UnityEngine.Time.realtimeSinceStartupAsDouble = item.At;
                if (item.Id == 0 || Active.Contains(item.Id)) item.Action();
            }
            UnityEngine.Time.realtimeSinceStartupAsDouble = target;
        }
    }
}
