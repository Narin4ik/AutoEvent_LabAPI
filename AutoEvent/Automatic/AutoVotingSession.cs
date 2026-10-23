using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoEvent.Automatic;

internal enum AutoStage
{
    Idle, InitialVote, PendingRound, Starting, RandomEvent, ContinuationVote, Finale, Cancelling, Restarting
}

// Pure state transitions shared by the server adapter and the executable tests.
internal sealed class AutoVotingSession
{
    private readonly Dictionary<string, bool> _votes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _played = new(StringComparer.OrdinalIgnoreCase);
    internal AutoStage Stage { get; private set; }
    internal int RoundsStarted { get; private set; }
    internal int Yes => _votes.Values.Count(v => v);
    internal int No => _votes.Count - Yes;
    internal double Percent => _votes.Count == 0 ? 0 : 100d * Yes / _votes.Count;
    internal bool IsVoting => Stage is AutoStage.InitialVote or AutoStage.ContinuationVote;
    internal bool OwnsChain => Stage is AutoStage.Starting or AutoStage.RandomEvent or AutoStage.ContinuationVote
        or AutoStage.Finale or AutoStage.Cancelling or AutoStage.Restarting;
    internal bool ShouldVote(int interval) => interval > 0 && (RoundsStarted + 1) % interval == 0;

    internal void OpenInitialVote()
    {
        if (Stage != AutoStage.Idle) throw new InvalidOperationException("Session is busy.");
        _votes.Clear();
        Stage = AutoStage.InitialVote;
    }

    internal bool RecordVote(string id, bool yes)
    {
        if (!IsVoting || string.IsNullOrWhiteSpace(id)) return false;
        if (_votes.TryGetValue(id, out bool old) && old == yes) return false;
        _votes[id] = yes;
        return true;
    }

    internal void RemoveVote(string id) { if (id != null) _votes.Remove(id); }

    internal void FinishVote()
    {
        if (!IsVoting) return;
        bool passed = Stage == AutoStage.InitialVote ? Yes > No :
            _votes.Count > 0 && 100L * Yes >= 65L * _votes.Count;
        Stage = Stage == AutoStage.InitialVote ? (passed ? AutoStage.PendingRound : AutoStage.Idle) :
            (passed ? AutoStage.Starting : AutoStage.Finale);
        _votes.Clear();
    }

    internal bool RoundStarted()
    {
        RoundsStarted++;
        if (Stage != AutoStage.PendingRound) return false;
        Stage = AutoStage.Starting;
        return true;
    }

    internal bool CanChoose(string command, IEnumerable<string> excluded)
    {
        return !string.IsNullOrWhiteSpace(command) &&
            !new[] { "jail", "nukerun", "escape", "lobby", "vote" }.Contains(command, StringComparer.OrdinalIgnoreCase) &&
            !_played.Contains(command) &&
            !(excluded ?? Array.Empty<string>()).Any(x => string.Equals(x?.Trim(), command, StringComparison.OrdinalIgnoreCase));
    }

    internal void StartRandom(string command)
    {
        if (Stage != AutoStage.Starting || !CanChoose(command, null))
            throw new InvalidOperationException("Invalid or repeated automatic event.");
        _played.Add(command);
        Stage = AutoStage.RandomEvent;
    }

    internal void RandomCleaned(bool stopped, bool hasNext)
    {
        if (Stage == AutoStage.Cancelling || stopped) { Clear(); return; }
        if (Stage != AutoStage.RandomEvent) return;
        _votes.Clear();
        Stage = hasNext ? AutoStage.ContinuationVote : AutoStage.Finale;
    }

    internal void RequireFinale()
    {
        if (Stage is AutoStage.Starting or AutoStage.ContinuationVote) Stage = AutoStage.Finale;
        _votes.Clear();
    }

    internal bool FinaleCleaned()
    {
        if (Stage == AutoStage.Cancelling) { Clear(); return false; }
        if (Stage != AutoStage.Finale) return false;
        Stage = AutoStage.Restarting;
        return true;
    }

    internal void Cancel(bool eventRunning)
    {
        _votes.Clear();
        if (eventRunning) Stage = AutoStage.Cancelling;
        else Clear();
    }

    internal void ResetCounter() => RoundsStarted = 0;

    internal void Clear()
    {
        Stage = AutoStage.Idle;
        _votes.Clear();
        _played.Clear();
    }
}
