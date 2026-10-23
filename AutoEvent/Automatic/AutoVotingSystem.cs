using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutoEvent.API;
using AutoEvent.ApiFeatures;
using AutoEvent.Interfaces;
using CommandSystem;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using MEC;
using UserSettings.ServerSpecific;
using Event = AutoEvent.Interfaces.Event;

namespace AutoEvent.Automatic;

internal sealed class AutoVotingSystem : CustomEventsHandler, IDisposable
{
    private const int HeaderId = 6102410, YesId = 6102411, NoId = 6102412;
    private readonly AutoEvent _plugin;
    private readonly AutoVotingSession _session = new();
    private readonly ScpStatsLease _stats = new(ExecuteStats);
    private ServerSpecificSettingBase[] _settings = [];
    private CoroutineHandle _voteTimer;
    private Event _current;
    private Event _startingEvent;
    private bool _stopped, _cancelled, _startupFailed, _firstStart;
    private bool _enabled, _inLobby, _lobbyLock, _roundLock, _disposed;
    private int _generation, _broadcastGeneration;
    private AutoVotingConfig Config => _plugin.Config.AutoVoting;
    internal bool OwnsChain => _session.OwnsChain;
    internal string Status => $"Авто-ивенты: {(_enabled ? "включены" : "выключены")}; раундов: {_session.RoundsStarted}; следующий: {_session.RoundsStarted + 1}; этап: {_session.Stage}; за: {_session.Yes}; против: {_session.No}; поддержка: {_session.Percent:F2}%; восстановление SCPStats: {_stats.NeedsRestore}.";

    internal AutoVotingSystem(AutoEvent plugin)
    {
        _plugin = plugin;
        ValidateConfig();
        _enabled = Config.Enabled && RegisterSettings();
        // Buttons must also be registered when initially disabled, for `ev auto on`.
        if (_settings.Length == 0 && !Config.Enabled) RegisterSettings();
        CustomHandlersManager.RegisterEventsHandler(this);
        ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnSetting;
        if (!Round.IsRoundStarted) OnServerWaitingForPlayers();
    }

    private void ValidateConfig()
    {
        _plugin.Config.AutoVoting ??= new AutoVotingConfig();
        if (Config.RoundInterval < 1) { LogManager.Warn("auto_voting.round_interval сброшен на 4."); Config.RoundInterval = 4; }
        if (Config.VoteDurationSeconds < 1) { LogManager.Warn("auto_voting.vote_duration_seconds сброшен на 45."); Config.VoteDurationSeconds = 45; }
        if (!ValidFloat(Config.BroadcastIntervalSeconds, 0.1f, 60f)) Config.BroadcastIntervalSeconds = 1;
        if (!ValidFloat(Config.BroadcastDurationSeconds, 0.1f, 60f)) Config.BroadcastDurationSeconds = 1.5f;
        var defaults = new AutoVotingConfig();
        foreach (var property in typeof(AutoVotingConfig).GetProperties().Where(p => p.PropertyType == typeof(string)))
            if (string.IsNullOrWhiteSpace((string)property.GetValue(Config)))
                property.SetValue(Config, property.GetValue(defaults));
        Config.ExcludedEvents ??= [];
    }

    private static bool ValidFloat(float value, float min, float max) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;

    private bool RegisterSettings()
    {
        var existing = ServerSpecificSettingsSync.DefinedSettings ?? [];
        if (existing.Any(s => s.SettingId is HeaderId or YesId or NoId))
        {
            LogManager.Error("Авто-ивенты отключены: идентификаторы кнопок Server Specific Settings уже заняты.");
            return false;
        }
        _settings = [new SSGroupHeader(HeaderId, Config.SettingsGroupName),
            new SSButton(YesId, Config.YesLabel, Config.YesButtonText, 0f, Config.YesHint),
            new SSButton(NoId, Config.NoLabel, Config.NoButtonText, 0f, Config.NoHint)];
        ServerSpecificSettingsSync.DefinedSettings = existing.Concat(_settings).ToArray();
        ServerSpecificSettingsSync.SendToAll();
        return true;
    }

    public override void OnServerWaitingForPlayers()
    {
        if (_disposed) return;
        _inLobby = true;
        ClearSession();
        RestoreStats();
        TryOpenInitialVote();
    }

    private void TryOpenInitialVote()
    {
        if (_disposed || !_enabled || !_inLobby || _session.Stage != AutoStage.Idle ||
            !_session.ShouldVote(Config.RoundInterval)) return;
        if (EventManager.CurrentEvent != null || Round.IsLobbyLocked || AutomaticEvents.IsExternalActivityRunning())
        {
            LogManager.Info("Голосование пропущено: активен другой режим или чужая блокировка лобби.");
            return;
        }
        _session.OpenInitialVote();
        Round.IsLobbyLocked = true;
        _lobbyLock = true;
        BeginVoteTimer();
    }

    private void BeginVoteTimer()
    {
        LogManager.Info($"Голосование авто-ивентов: {_session.Stage}, {Config.VoteDurationSeconds} секунд.");
        _voteTimer = Timing.RunCoroutine(VoteCountdown(_generation));
    }

    private IEnumerator<float> VoteCountdown(int generation)
    {
        double deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + Config.VoteDurationSeconds;
        while (!_disposed && generation == _generation && _session.IsVoting)
        {
            double remaining = deadline - UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (remaining <= 0) break;
            if (AutomaticEvents.IsExternalActivityRunning()) { Cancel(); yield break; }
            string template = _session.Stage == AutoStage.InitialVote ? Config.VoteBroadcastText : Config.ContinueBroadcastText;
            string message = template.Replace("{time}", Math.Ceiling(remaining).ToString(CultureInfo.InvariantCulture))
                .Replace("{yes}", _session.Yes.ToString()).Replace("{no}", _session.No.ToString())
                .Replace("{percent}", _session.Percent.ToString("F2", CultureInfo.InvariantCulture));
            Server.SendBroadcast(message, (ushort)Math.Ceiling(Config.BroadcastDurationSeconds), Broadcast.BroadcastFlags.Normal, true);
            int broadcast = ++_broadcastGeneration;
            Timing.CallDelayed(Config.BroadcastDurationSeconds, () =>
            {
                if (!_disposed && generation == _generation && broadcast == _broadcastGeneration && _session.IsVoting)
                    Server.ClearBroadcasts();
            });
            yield return Timing.WaitForSeconds((float)Math.Min(Config.BroadcastIntervalSeconds, remaining));
        }
        if (_disposed || generation != _generation || !_session.IsVoting) yield break;
        bool initial = _session.Stage == AutoStage.InitialVote;
        LogManager.Info($"Голосование: за {_session.Yes}, против {_session.No}, поддержка {_session.Percent:F2}%.");
        _session.FinishVote();
        _broadcastGeneration++;
        ReleaseLobbyLock();
        bool passed = _session.Stage is AutoStage.PendingRound or AutoStage.Starting;
        Server.SendBroadcast(initial ? (passed ? Config.VotePassedText : Config.VoteFailedText) :
            (passed ? Config.ContinuePassedText : Config.ContinueFailedText), 5, Broadcast.BroadcastFlags.Normal, true);
        if (!initial) Schedule(0.1f, passed ? StartNextRandom : StartFinale);
    }

    private void OnSetting(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        if (_disposed || !_session.IsVoting || setting is not SSButton || setting.SettingId is not (YesId or NoId)) return;
        Player player = Player.Get(hub);
        if (player == null || !player.IsReady || player.IsHost) return;
        _session.RecordVote(player.UserId, setting.SettingId == YesId);
    }

    public override void OnPlayerLeft(PlayerLeftEventArgs ev) => _session.RemoveVote(ev.Player?.UserId);

    public override void OnServerRoundStarted()
    {
        if (_disposed) return;
        _inLobby = false;
        bool start = _session.RoundStarted();
        if (!start) { if (_session.IsVoting) Cancel(); return; }
        if (!_enabled || Round.IsLocked || EventManager.CurrentEvent != null || AutomaticEvents.IsExternalActivityRunning())
        { ClearSession(); return; }
        Round.IsLocked = true;
        _roundLock = true;
        _firstStart = true;
        Schedule(2f, StartNextRandom);
    }

    private List<Event> Candidates() => (EventManager.Events ?? []).Where(ev =>
        ev is not Games.Jail.Plugin && ev is not Games.NukeRun.Plugin &&
        _session.CanChoose(ev.CommandName, Config.ExcludedEvents) &&
        !Config.ExcludedEvents.Any(x => string.Equals(x?.Trim(), ev.Name, StringComparison.OrdinalIgnoreCase)) && CanRun(ev)).ToList();

    private static bool CanRun(Event ev)
    {
        var players = Player.ReadyList.Where(p => !p.IsHost && !AutoEvent.Singleton.Config.IgnoredRoles.Contains(p.Role)).ToList();
        if (players.Count == 0 || ev is IPlayerCountLimited limited && players.Count > limited.MaxPlayers) return false;
        if (ev is IRequiresPlugins req &&
            (req.RequiredPlugins.Any(name => !PluginLoader.EnabledPlugins.Any(p => p.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) ||
             req.RequiredDependencies.Any(name => !PluginLoader.Dependencies.Any(a => a.GetName().Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)))) return false;
        if (ev is not IEventMap map) return true;
        var names = ev.InternalConfig?.AvailableMaps?.Select(m => m?.MapName).ToList() ?? [];
        if (names.Count == 0) names.Add(map.MapInfo?.MapName);
        return names.Where(n => !string.IsNullOrWhiteSpace(n) && !string.Equals(n, "none", StringComparison.OrdinalIgnoreCase))
            .All(n => Extensions.IsExistsMap(n, out _));
    }

    private void StartNextRandom()
    {
        if (_session.Stage != AutoStage.Starting || !_enabled) return;
        if (EventManager.CurrentEvent != null || AutomaticEvents.IsExternalActivityRunning()) { FailChain("Другой режим занял раунд."); return; }
        Event finale = EventManager.Events?.OfType<Games.NukeRun.Plugin>().FirstOrDefault();
        if (finale == null || !CanRun(finale)) { FailChain("Nuke Run недоступен."); return; }
        List<Event> pool = Candidates();
        if (pool.Count == 0)
        {
            if (_firstStart) { LogManager.Warn("Нет доступных случайных мини-игр."); ClearSession(); }
            else { _session.RequireFinale(); StartFinale(); }
            return;
        }
        if (_firstStart && !_stats.Disable())
        { LogManager.Error("Авто-ивенты отменены: SCPStats не отключён."); ClearSession(); return; }
        Event ev = pool[UnityEngine.Random.Range(0, pool.Count)];
        _session.StartRandom(ev.CommandName);
        StartOwnedEvent(ev);
    }

    private void StartFinale()
    {
        if (_session.Stage != AutoStage.Finale) return;
        Event ev = EventManager.Events?.OfType<Games.NukeRun.Plugin>().FirstOrDefault();
        if (ev == null || !CanRun(ev)) { FailChain("Финальный Nuke Run недоступен."); return; }
        StartOwnedEvent(ev);
    }

    private void StartOwnedEvent(Event ev)
    {
        _current = ev;
        _stopped = _cancelled = _startupFailed = false;
        ev.EventStopped += OnStopped;
        ev.CleanupFinished += OnCleanup;
        _startingEvent = ev;
        try
        {
            LogManager.Info($"Авто-ивент: {ev.Name} ({ev.CommandName}).");
            ev.StartEvent();
            if (_stopped) throw new InvalidOperationException("Ивент остановился при запуске.");
            _firstStart = false;
        }
        catch (Exception e)
        {
            LogManager.Error($"Ошибка запуска авто-ивента {ev.Name}: {e}");
            _startupFailed = true;
            if (_firstStart) RestoreStats();
            // Partial startup may already own maps and handlers. Clean it before releasing the round.
            try { ev.StopEvent(); }
            catch (Exception stopError) { LogManager.Error($"Ошибка остановки: {stopError}"); FailChain("Очистка после ошибки запуска не удалась."); }
        }
        finally { _startingEvent = null; }
    }

    private void OnStopped(string name) => _stopped = true;

    private void OnCleanup(string name)
    {
        bool stopped = _stopped, failed = _startupFailed, first = _firstStart, cancelled = _cancelled;
        bool finale = _session.Stage == AutoStage.Finale;
        DetachEvent();
        Schedule(0.1f, () =>
        {
            if (failed && !cancelled)
            {
                if (first) ClearSession();
                else RestartOnce();
                return;
            }
            if (cancelled || stopped && !finale) { ClearSession(); return; }
            if (finale)
            {
                if (_session.FinaleCleaned()) RestartOnce();
                else ClearSession();
                return;
            }
            _session.RandomCleaned(stopped, Candidates().Count > 0);
            if (_session.Stage == AutoStage.ContinuationVote)
            {
                foreach (Player p in Player.ReadyList.Where(p => !p.IsHost && !_plugin.Config.IgnoredRoles.Contains(p.Role)))
                { p.ClearInventory(); p.SetRole(_plugin.Config.LobbyRole); }
                BeginVoteTimer();
            }
            else if (_session.Stage == AutoStage.Finale) StartFinale();
            else ClearSession();
        });
    }

    private void FailChain(string reason)
    {
        LogManager.Error($"Авто-ивенты: {reason}");
        if (_firstStart) { RestoreStats(); ClearSession(); }
        else RestartOnce();
    }

    private bool _restartRequested;
    private void RestartOnce()
    {
        if (_restartRequested || _disposed) return;
        _restartRequested = true;
        StopVote();
        _session.Cancel(false);
        // Keep the round locked until Restart() has been requested.
        try { Round.Restart(); }
        catch (Exception e) { LogManager.Error($"Не удалось перезапустить раунд: {e}"); RestoreStats(); }
        finally { ClearSession(); }
    }

    private static bool ExecuteStats(string action)
    {
        try
        {
            ICommand command;
            if (!GameCore.Console.ConsoleCommandHandler.TryGetCommand("scpstats", out command))
            {
                LogManager.Error("Команда scpstats не найдена в серверной консоли.");
                return false;
            }
            bool success = command.Execute(new ArraySegment<string>([action]), ServerConsole.Scs, out string response);
            if (!success) LogManager.Error($"scpstats {action}: {response}");
            else LogManager.Info($"scpstats {action}: {response}");
            return success;
        }
        catch (Exception e) { LogManager.Error($"scpstats {action}: {e}"); return false; }
    }

    private void RestoreStats()
    {
        if (!_stats.Restore()) LogManager.Error("Не удалось восстановить SCPStats; повторная попытка в следующем лобби / при выгрузке.");
    }

    internal bool AllowEventStart(Event ev)
    {
        if (_startingEvent == ev) return true;
        return PrepareExternalActivity();
    }

    internal bool PrepareExternalActivity()
    {
        if (_disposed) return true;
        if (OwnsChain) return false;
        if (_session.Stage != AutoStage.Idle) Cancel();
        return true;
    }

    internal bool SetEnabled(bool enabled)
    {
        if (enabled && _settings.Length == 0) return false;
        _enabled = enabled;
        if (!enabled) Cancel();
        else TryOpenInitialVote();
        return true;
    }

    internal void Reset() { Cancel(); _session.ResetCounter(); }

    private void Cancel()
    {
        _generation++;
        StopVote();
        _cancelled = true;
        _session.Cancel(_current != null);
        ReleaseLobbyLock();
        if (_current == null) ReleaseRoundLock();
    }

    public override void OnServerRoundRestarted()
    {
        _inLobby = false;
        ClearSession();
    }

    private void Schedule(float delay, Action action)
    {
        int generation = _generation;
        Timing.CallDelayed(delay, () =>
        {
            if (_disposed || generation != _generation) return;
            try { action(); }
            catch (Exception e) { LogManager.Error($"Ошибка перехода авто-ивентов: {e}"); FailChain("Переход между этапами не удался."); }
        });
    }

    private void StopVote()
    {
        _broadcastGeneration++;
        if (_voteTimer.IsRunning) Timing.KillCoroutines(_voteTimer);
    }

    private void DetachEvent()
    {
        if (_current == null) return;
        _current.EventStopped -= OnStopped;
        _current.CleanupFinished -= OnCleanup;
        _current = null;
    }

    private void ClearSession()
    {
        _generation++;
        StopVote();
        DetachEvent();
        _session.Clear();
        ReleaseLobbyLock();
        ReleaseRoundLock();
        _restartRequested = false;
    }

    private void ReleaseLobbyLock() { if (_lobbyLock) Round.IsLobbyLocked = false; _lobbyLock = false; }
    private void ReleaseRoundLock() { if (_roundLock) Round.IsLocked = false; _roundLock = false; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CustomHandlersManager.UnregisterEventsHandler(this);
        ServerSpecificSettingsSync.ServerOnSettingValueReceived -= OnSetting;
        ClearSession();
        RestoreStats();
        var existing = ServerSpecificSettingsSync.DefinedSettings ?? [];
        ServerSpecificSettingsSync.DefinedSettings = existing.Where(s => !_settings.Contains(s)).ToArray();
        ServerSpecificSettingsSync.SendToAll();
    }
}
