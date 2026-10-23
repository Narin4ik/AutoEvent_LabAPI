using System.Collections.Generic;
using System.ComponentModel;

namespace AutoEvent.Automatic;

public sealed class AutoVotingConfig
{
    public bool Enabled { get; set; } = true;
    public int RoundInterval { get; set; } = 4;
    public int VoteDurationSeconds { get; set; } = 45;
    public float BroadcastIntervalSeconds { get; set; } = 1f;
    public float BroadcastDurationSeconds { get; set; } = 1.5f;

    [Description("Подстановки: {time}, {yes}, {no}, {percent}.")]
    public string VoteBroadcastText { get; set; } = "<size=28><color=yellow>Провести авто-ивенты?</color>\nДа: {yes} | Нет: {no} | Осталось: {time} с\nГолосуйте в Server Specific Settings.</size>";
    public string VotePassedText { get; set; } = "Голосование завершено: начинаются авто-ивенты!";
    public string VoteFailedText { get; set; } = "Голосование завершено: обычный раунд.";
    public string ContinueBroadcastText { get; set; } = "<size=28><color=yellow>Продолжить авто-ивенты? Нужно 65% за.</color>\nДа: {yes} | Нет: {no} | За: {percent}% | Осталось: {time} с\nГолосуйте в Server Specific Settings.</size>";
    public string ContinuePassedText { get; set; } = "За продолжение не менее 65%: следующая случайная мини-игра!";
    public string ContinueFailedText { get; set; } = "Авто-ивенты завершаются: финальный Nuke Run!";
    public string SettingsGroupName { get; set; } = "Авто-ивенты";
    public string YesLabel { get; set; } = "Проголосовать за проведение / продолжение авто-ивентов";
    public string YesHint { get; set; } = "Да — начать или продолжить случайные мини-игры";
    public string YesButtonText { get; set; } = "Да";
    public string NoLabel { get; set; } = "Проголосовать против проведения / продолжения авто-ивентов";
    public string NoHint { get; set; } = "Нет — обычный раунд или финальный Nuke Run";
    public string NoButtonText { get; set; } = "Нет";
    [Description("Команды дополнительных исключений. jail и nukerun никогда не участвуют в случайном выборе.")]
    public List<string> ExcludedEvents { get; set; } = [];
}
