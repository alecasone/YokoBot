using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;

namespace Yoko.Bot.Commands;

internal static class ScheduledCommandSupport
{
    internal static bool TryParseDestination(SocketSlashCommand command, string value,
        out ScheduledDestination destination, out string error)
    {
        destination = new ScheduledDestination();
        error = "Use `here`, mention a channel, mention a user for DMs, or use `dm me`.";
        if (command.Channel is not SocketGuildChannel current) return false;
        var guild = current.Guild;
        var input = value.Trim();
        if (input.Equals("dm me", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("dm", StringComparison.OrdinalIgnoreCase))
        {
            destination.Kind = "user-dm";
            destination.TargetId = command.User.Id;
            return true;
        }

        var userMatch = Regex.Match(input, @"^(?:dm\s+)?<@!?(\d+)>$", RegexOptions.IgnoreCase);
        var explicitUser = input.StartsWith("user:", StringComparison.OrdinalIgnoreCase);
        if ((userMatch.Success && ulong.TryParse(userMatch.Groups[1].Value, out var userId)) ||
            (explicitUser && ulong.TryParse(input[5..].Trim(), out userId)))
        {
            if (guild.GetUser(userId) is null)
            {
                error = "That DM recipient must be a member of this server.";
                return false;
            }
            destination.Kind = "user-dm";
            destination.TargetId = userId;
            return true;
        }

        ulong channelId;
        if (input.Equals("here", StringComparison.OrdinalIgnoreCase))
            channelId = current.Id;
        else
        {
            var channelMatch = Regex.Match(input, @"^<#(\d+)>$");
            var raw = input.StartsWith("channel:", StringComparison.OrdinalIgnoreCase)
                ? input[8..].Trim() : input;
            if (!ulong.TryParse(channelMatch.Success ? channelMatch.Groups[1].Value : raw, out channelId))
                return false;
        }

        var channel = guild.GetChannel(channelId) ?? (SocketGuildChannel?)guild.GetThreadChannel(channelId);
        if (channel is not IMessageChannel)
        {
            error = "Choose a message channel in this server.";
            return false;
        }
        if (command.User is not SocketGuildUser member || !member.GetPermissions(channel).ViewChannel)
        {
            error = "You must be able to view the destination channel.";
            return false;
        }
        var botPermissions = guild.CurrentUser.GetPermissions(channel);
        if (!botPermissions.ViewChannel ||
            (channel is SocketThreadChannel ? !botPermissions.SendMessagesInThreads : !botPermissions.SendMessages))
        {
            error = "Yoko needs permission to view and send messages in that channel.";
            return false;
        }
        destination.Kind = "channel";
        destination.TargetId = channelId;
        error = string.Empty;
        return true;
    }

    internal static string DestinationLabel(ScheduledDestination destination) =>
        destination.Kind == "user-dm" ? $"DM <@{destination.TargetId}>" : $"<#{destination.TargetId}>";
}
