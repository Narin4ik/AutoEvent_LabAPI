using System;
using CommandSystem;
using LabApi.Features.Permissions;

namespace AutoEvent.Commands;

[CommandHandler(typeof(MainCommand))]
internal sealed class Auto : ICommand, IUsageProvider
{
    public string Command => "auto";
    public string[] Aliases => [];
    public string Description => "Периодические авто-ивенты: status, reset, on, off";
    public string[] Usage => ["status|reset|on|off"];
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.HasPermissions("ev.auto")) { response = "Требуется право ev.auto."; return false; }
        var system = AutoEvent.Singleton?.VotingSystem;
        if (system == null) { response = "Система авто-ивентов не загружена."; return false; }
        switch (arguments.Count > 0 ? arguments.At(0).ToLowerInvariant() : "status")
        {
            case "status": break;
            case "reset": system.Reset(); break;
            case "off": system.SetEnabled(false); break;
            case "on":
                if (!system.SetEnabled(true)) { response = "Не удалось включить авто-ивенты: кнопки голосования не зарегистрированы."; return false; }
                break;
            default: response = "ev auto status|reset|on|off"; return false;
        }
        response = system.Status;
        return true;
    }
}
