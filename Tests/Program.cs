using AutoEvent.Automatic;

int checks = 0;
void Check(bool value, string message)
{
    checks++;
    if (!value) throw new Exception(message);
}

AutoVotingSession Chain()
{
    var s = new AutoVotingSession();
    s.OpenInitialVote();
    s.RecordVote("a", true);
    s.FinishVote();
    Check(s.Stage == AutoStage.PendingRound, "Successful initial vote queues a round");
    Check(s.RoundStarted(), "Queued round starts chain");
    return s;
}

void Vote(AutoVotingSession s, int yes, int no)
{
    for (int i = 0; i < yes + no; i++) s.RecordVote($"p{i}", i < yes);
    s.FinishVote();
}

var rounds = new AutoVotingSession();
for (int i = 0; i < 12; i++)
{
    Check(rounds.ShouldVote(4) == (i is 3 or 7 or 11), $"Vote before round {i + 1}");
    rounds.RoundStarted();
}
Check(!rounds.ShouldVote(0), "Invalid interval does not vote");
rounds.ResetCounter();
Check(rounds.RoundsStarted == 0 && !rounds.ShouldVote(4), "Reset starts counting again");

var votes = new AutoVotingSession();
Check(!votes.RecordVote("a", true), "No votes outside a vote");
votes.OpenInitialVote();
Check(votes.RecordVote("a", true), "First vote");
Check(!votes.RecordVote("A", true), "User id is case insensitive; duplicates ignored");
Check(votes.RecordVote("a", false) && votes.No == 1 && votes.Yes == 0, "Vote can change");
votes.RecordVote("b", true);
votes.RemoveVote("a");
Check(votes.Yes == 1 && votes.No == 0, "Disconnected vote removed");
votes.RemoveVote("b");
votes.FinishVote();
Check(votes.Stage == AutoStage.Idle, "No initial votes means ordinary round");
votes.OpenInitialVote();
Vote(votes, 1, 1);
Check(votes.Stage == AutoStage.Idle, "Initial tie means ordinary round");
votes.OpenInitialVote();
Vote(votes, 2, 1);
Check(votes.Stage == AutoStage.PendingRound, "Initial vote needs only a majority");

foreach (var (yes, no, pass) in new[] { (13, 7, true), (9, 5, false), (64, 36, false), (65, 35, true), (66, 34, true), (1, 0, true), (0, 0, false), (1, 1, false) })
{
    var s = Chain();
    s.StartRandom("dm");
    s.RandomCleaned(false, true);
    Check(s.Stage == AutoStage.ContinuationVote && s.Yes == 0 && s.No == 0, "Each continuation starts empty");
    Vote(s, yes, no);
    Check(s.Stage == (pass ? AutoStage.Starting : AutoStage.Finale), $"Exact threshold {yes}/{yes + no}");
}

var sequence = Chain();
foreach (string game in new[] { "dm", "tdm", "glass" })
{
    Check(sequence.CanChoose(game, []), "Unplayed event allowed");
    sequence.StartRandom(game);
    Check(!sequence.CanChoose(game.ToUpperInvariant(), []), "No repeats, including case changes");
    sequence.RandomCleaned(false, true);
    Vote(sequence, 13, 7);
}
foreach (string blocked in new[] { "jail", "JAIL", "nukerun", "escape", "lobby", "vote" })
    Check(!sequence.CanChoose(blocked, []), $"Permanent exclusion {blocked}");
Check(!sequence.CanChoose("race", [" RACE "]), "Custom exclusions ignore case and whitespace");
sequence.RequireFinale();
Check(sequence.FinaleCleaned(), "Finale schedules restart");
Check(!sequence.FinaleCleaned(), "Duplicate cleanup cannot schedule a second restart");
sequence.Clear();
Check(sequence.CanChoose("dm", []), "New chain resets played games");

var exhausted = Chain();
exhausted.StartRandom("dm");
exhausted.RandomCleaned(false, false);
Check(exhausted.Stage == AutoStage.Finale, "Exhausted pool skips vote and ends in Nuke Run");

foreach (bool stopped in new[] { false, true })
{
    var cancelled = Chain();
    cancelled.StartRandom("dm");
    if (!stopped) cancelled.Cancel(true);
    Check(cancelled.OwnsChain, "Keep ownership until active event cleanup");
    cancelled.RandomCleaned(stopped, true);
    Check(cancelled.Stage == AutoStage.Idle && !cancelled.OwnsChain, "Off/reset/manual stop cancels continuation after cleanup");
}
var pending = Chain();
pending.Cancel(false);
Check(pending.Stage == AutoStage.Idle, "Pending startup can be cancelled by generation guard");
var midVote = Chain();
midVote.StartRandom("dm");
midVote.RandomCleaned(false, true);
midVote.RecordVote("a", true);
midVote.Cancel(false);
midVote.FinishVote();
Check(midVote.Stage == AutoStage.Idle && midVote.Yes == 0, "Cancelled vote cannot restart chain");
var finaleCancelled = Chain();
finaleCancelled.RequireFinale();
finaleCancelled.Cancel(true);
Check(!finaleCancelled.FinaleCleaned() && !finaleCancelled.OwnsChain, "Admin cancellation of finale does not restart");

var commands = new List<string>();
bool succeed = false;
var lease = new ScpStatsLease(action => { commands.Add(action); return succeed; });
Check(!lease.Disable() && !lease.NeedsRestore, "Refused or missing SCPStats disables automatic startup");
Check(lease.Restore() && commands.Count == 1, "No enable without successful disable");
succeed = true;
Check(lease.Disable() && lease.NeedsRestore, "Successful disable records restoration");
Check(lease.Disable() && commands.Count == 2, "No repeated disable between games or votes");
succeed = false;
Check(!lease.Restore() && lease.NeedsRestore, "Failed enable retains retry state");
succeed = true;
Check(lease.Restore() && !lease.NeedsRestore, "Next lobby/unload retries enable");
Check(lease.Restore() && commands.Count == 4, "Successful enable is sent once");

RuntimeChecks.Run(Check);
AudioPlaybackChecks.Run(Check);
Console.WriteLine($"Passed {checks} controller checks (voting, lifecycle, SCPStats, audio adapter).");
