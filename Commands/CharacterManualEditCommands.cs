using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static partial class CharacterCommands
{
    private static readonly ConcurrentDictionary<ulong, ManualEditSession> ManualEditSessions = new();
    private static readonly HttpClient ManualEditHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private sealed record ManualEditSession(
        SocketGuild Guild, ulong OwnerId, ulong ChannelId, Character Original, string EditId, DateTimeOffset ExpiresAt)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IReadOnlyList<ManualFieldEdit>? Pending { get; set; }
    }

    private static async Task BeginManualEditAsync(SocketSlashCommand command, CharacterStore store, IUser owner, string selector)
    {
        await command.DeferAsync(ephemeral: true);
        var character = await store.GetAsync(command.GuildId!.Value, owner.Id, selector);
        if (character is null)
        {
            await UpdateOriginalAsync(command, "Character not found.");
            return;
        }
        foreach (var entry in ManualEditSessions.Where(entry => entry.Value.ExpiresAt <= DateTimeOffset.UtcNow))
            RemoveManualEdit(entry.Key, entry.Value);

        var editId = Guid.NewGuid().ToString("N");
        var template = CharacterManualEdit.Template(character, editId);
        if (Encoding.UTF8.GetByteCount(template) > CharacterManualEdit.MaxBytes)
        {
            await UpdateOriginalAsync(command, "This character exceeds the 256 KiB manual-edit limit. Use individual field edits.");
            return;
        }

        ManualEditSession session;
        try
        {
            var dm = await command.User.CreateDMChannelAsync();
            session = new ManualEditSession(((SocketGuildChannel)command.Channel).Guild, owner.Id, dm.Id,
                character, editId, DateTimeOffset.UtcNow.AddHours(1));
            await SendManualTextAsync(dm, template, "character-edit.txt",
                $"Edit **{CharacterSchema.BoundedName(character.Name)}** from **{session.Guild.Name}**. " +
                "Copy the block into Notepad, edit it, then paste the entire block back here or attach one UTF-8 .txt file (up to 256 KiB). " +
                "A preview comes before Confirm; nothing is saved yet.\n" +
                "Omitted or blank fields stay unchanged. Use [clear] to empty a field. " +
                "Quoted field names are custom fields; keep their spelling. Add custom fields as Field: value. " +
                "Quoted values use JSON escaping (for example \\n for a new line). " +
                "Keep the Edit ID. Reply Abort to cancel. Expires in one hour; starting another manual edit replaces the previous one.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not send character edit DM: {exception}");
            await UpdateOriginalAsync(command, "I could not send the template by DM. Enable DMs from this server and try again. Nothing was changed.");
            return;
        }
        ManualEditSessions[command.User.Id] = session;
        await UpdateOriginalAsync(command, "I sent the prefilled template to your DMs. Edit it and send it back there for a preview, then Confirm or Abort.");
    }

    public static async Task<bool> HandleManualEditReplyAsync(
        SocketMessage message, CharacterStore store, PermissionService permissions, SitePublicationService sitePublisher)
    {
        if (message.Author.IsBot || message.Channel is not IDMChannel) return false;
        if (!ManualEditSessions.TryGetValue(message.Author.Id, out var session))
        {
            if (message.Content.Contains("BEGIN CHARACTER EDIT", StringComparison.Ordinal) ||
                message.Content.Trim().Equals("confirm", StringComparison.OrdinalIgnoreCase) ||
                message.Content.Trim().Equals("abort", StringComparison.OrdinalIgnoreCase) ||
                message.Attachments.Any(attachment => attachment.Filename.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                await SendManualReplyAsync(message.Channel, "No active manual edit. Run /character manual-edit in your server to get a fresh template.");
                return true;
            }
            return false;
        }
        if (message.Channel.Id != session.ChannelId) return false;

        await session.Gate.WaitAsync();
        try
        {
            if (!ManualEditSessions.TryGetValue(message.Author.Id, out var current) || !ReferenceEquals(current, session))
                return true;
            var reply = message.Content.Trim();
            if (reply.Equals("abort", StringComparison.OrdinalIgnoreCase))
            {
                RemoveManualEdit(message.Author.Id, session);
                await SendManualReplyAsync(message.Channel, "Manual edit aborted. No changes were saved.");
                return true;
            }
            if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                RemoveManualEdit(message.Author.Id, session);
                await SendManualReplyAsync(message.Channel, "This edit expired. Nothing was saved. Run /character manual-edit again.");
                return true;
            }

            // Resolve the server member again: DM users do not contain current guild roles.
            var member = session.Guild.GetUser(message.Author.Id);
            var required = CommandPermissionResolver.CharacterEditPermissions(message.Author.Id, session.OwnerId);
            if (member is null || !await permissions.HasAnyAsync(session.Guild.Id, member, required))
            {
                RemoveManualEdit(message.Author.Id, session);
                await SendManualReplyAsync(message.Channel, "You no longer have permission to edit this character. Nothing was saved.");
                return true;
            }

            if (reply.Equals("confirm", StringComparison.OrdinalIgnoreCase) && message.Attachments.Count == 0)
            {
                if (session.Pending is null)
                {
                    await SendManualReplyAsync(message.Channel, "Send the edited block or .txt file first. Confirm works only after a change preview.");
                    return true;
                }
                var result = await store.ApplyManualEditAsync(session.Guild.Id, session.OwnerId, session.Original, session.Pending, commit: true);
                if (result.Error is not null)
                {
                    session.Pending = null;
                    await SendManualReplyAsync(message.Channel, result.Error);
                    return true;
                }
                RemoveManualEdit(message.Author.Id, session);
                var publishNotice = "";
                if (result.Changes.Count > 0)
                {
                    try { await sitePublisher.QueueAsync(session.Guild.Id); }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine($"Manual character edit saved but website queue failed: {exception}");
                        publishNotice = " Website update could not be queued; ask staff to publish it.";
                    }
                }
                await SendManualReplyAsync(message.Channel,
                    $"Saved {result.Changes.Count} change(s) to **{CharacterSchema.BoundedName(result.Character!.Name)}**.{publishNotice}");
                return true;
            }

            // An invalid replacement must not leave an old preview eligible for confirmation.
            session.Pending = null;
            string document;
            try
            {
                document = message.Attachments.Count == 0 ? message.Content : await ReadManualAttachmentAsync(message);
                var edits = CharacterManualEdit.Parse(document, session.EditId);
                var preview = await store.ApplyManualEditAsync(session.Guild.Id, session.OwnerId, session.Original, edits, commit: false);
                if (preview.Error is not null)
                {
                    await SendManualReplyAsync(message.Channel, preview.Error);
                    return true;
                }
                if (preview.Changes.Count == 0)
                {
                    await SendManualReplyAsync(message.Channel, "No changes found. Edit the template and send it again, or reply Abort.");
                    return true;
                }
                await SendManualTextAsync(message.Channel, string.Join("\n", preview.Changes), "character-changes.txt",
                    $"{preview.Changes.Count} proposed change(s). Review the full preview below, then reply Confirm to save everything or Abort to cancel. " +
                    "You can also submit a revised block. Nothing has been saved.");
                session.Pending = edits;
            }
            catch (Exception exception) when (exception is FormatException or JsonException or HttpRequestException or OperationCanceledException or DecoderFallbackException)
            {
                await SendManualReplyAsync(message.Channel,
                    exception is HttpRequestException or OperationCanceledException
                        ? "The attachment could not be downloaded. Try uploading it again. Nothing was saved."
                        : $"Could not read that edit: {exception.Message}\nNothing was saved. Correct it and resend, or reply Abort.");
            }
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Manual character edit failed: {exception}");
            await SendManualReplyAsync(message.Channel,
                "The manual edit could not finish. Check /character view before retrying, or ask staff to check the bot log.");
            return true;
        }
        finally { session.Gate.Release(); }
    }


    private static void RemoveManualEdit(ulong actorId, ManualEditSession session) =>
        ((ICollection<KeyValuePair<ulong, ManualEditSession>>)ManualEditSessions).Remove(new(actorId, session));

    private static Task<IUserMessage> SendManualReplyAsync(IMessageChannel channel, string text) =>
        channel.SendMessageAsync(text.Length <= 2000 ? text : text[..1997] + "...", allowedMentions: AllowedMentions.None);

    private static async Task SendManualTextAsync(IMessageChannel channel, string text, string fileName, string instructions)
    {
        // Always supply a file, so profiles containing code fences or long values remain copyable.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await channel.SendFileAsync(stream, fileName, text: instructions, allowedMentions: AllowedMentions.None);
        if (text.Length <= 1800 && !text.Contains("```", StringComparison.Ordinal))
            await channel.SendMessageAsync("```text\n" + text + "\n```", allowedMentions: AllowedMentions.None);
    }

    private static async Task<string> ReadManualAttachmentAsync(SocketMessage message)
    {
        if (message.Attachments.Count != 1)
            throw new FormatException("Upload exactly one .txt file.");
        if (!string.IsNullOrWhiteSpace(message.Content))
            throw new FormatException("Send the .txt attachment by itself, without message text.");
        var attachment = message.Attachments.Single();
        if (!attachment.Filename.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || attachment.Size > CharacterManualEdit.MaxBytes)
            throw new FormatException("Use a UTF-8 .txt file no larger than 256 KiB.");
        var uri = new Uri(attachment.Url);
        if (uri.Scheme != "https" || uri.Host is not ("cdn.discordapp.com" or "media.discordapp.net"))
            throw new FormatException("Use a file attached directly to Discord.");
        using var response = await ManualEditHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + read > CharacterManualEdit.MaxBytes)
                throw new FormatException("The file is larger than 256 KiB.");
            output.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }
}