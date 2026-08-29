using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class CharacterRoleService
{
    private readonly DiscordSocketClient _client;
    private readonly CharacterStore _characters;
    private readonly CharacterSettingsStore _settings;
    private int _started;

    public CharacterRoleService(
        DiscordSocketClient client,
        CharacterStore characters,
        CharacterSettingsStore settings)
    {
        _client = client;
        _characters = characters;
        _settings = settings;
    }

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        foreach (var guild in _client.Guilds)
        {
            var configuration = await _settings.GetOcRoleConfigurationAsync(guild.Id);
            await SyncGuildAsync(guild.Id, configuration.SequentialRoleIds);
        }
    }

    public async Task<CharacterCapacity> GetCapacityAsync(ulong guildId, ulong userId)
    {
        var configuration = await _settings.GetOcRoleConfigurationAsync(guildId);
        var characterCount = await _characters.GetCharacterCountAsync(guildId, userId);
        return new CharacterCapacity(characterCount, configuration.SequentialRoleIds.Count);
    }

    public async Task<CharacterRoleSyncResult> SyncMemberAsync(ulong guildId, ulong userId)
    {
        var counts = await _characters.ReindexOcRolesAsync(guildId, userId);
        var characterCount = counts.TryGetValue(userId, out var count) ? count : 0;
        var configuration = await _settings.GetOcRoleConfigurationAsync(guildId);
        return await SyncMemberCoreAsync(
            guildId,
            userId,
            characterCount,
            configuration,
            configuration.SequentialRoleIds);
    }

    public async Task<CharacterRoleConfigurationResult> ConfigureAddedRolesAsync(
        ulong guildId,
        IEnumerable<ulong> defaultRoleIds,
        IEnumerable<ulong> sequentialRoleIds)
    {
        var oldConfiguration = await _settings.GetOcRoleConfigurationAsync(guildId);
        var defaults = defaultRoleIds.Distinct().ToArray();
        var defaultSet = defaults.ToHashSet();
        var sequential = sequentialRoleIds
            .Where(roleId => !defaultSet.Contains(roleId))
            .Distinct()
            .ToArray();
        var changed = !oldConfiguration.DefaultRoleIds.SequenceEqual(defaults) ||
                      !oldConfiguration.SequentialRoleIds.SequenceEqual(sequential);
        if (!changed)
            return Result(false, oldConfiguration, 0, 0);

        await _settings.ReplaceOcAddedRolesAsync(guildId, defaults, sequential);
        var sync = await SyncGuildAsync(guildId, oldConfiguration.SequentialRoleIds);
        var updated = await _settings.GetOcRoleConfigurationAsync(guildId);
        return Result(true, updated, sync.Synced, sync.Failed);
    }

    public async Task<CharacterRoleConfigurationResult> ConfigureRemovedRolesAsync(
        ulong guildId,
        IEnumerable<ulong> removedRoleIds)
    {
        var oldConfiguration = await _settings.GetOcRoleConfigurationAsync(guildId);
        var removed = removedRoleIds.Distinct().ToArray();
        if (oldConfiguration.RemovedRoleIds.SequenceEqual(removed))
            return Result(false, oldConfiguration, 0, 0);

        await _settings.ReplaceOcRemovedRolesAsync(guildId, removed);
        var sync = await SyncGuildAsync(guildId, oldConfiguration.SequentialRoleIds);
        var updated = await _settings.GetOcRoleConfigurationAsync(guildId);
        return Result(true, updated, sync.Synced, sync.Failed);
    }

    private async Task<(int Synced, int Failed)> SyncGuildAsync(
        ulong guildId,
        IReadOnlyCollection<ulong> previouslyManagedSequentialRoleIds)
    {
        var guild = _client.GetGuild(guildId);
        if (guild is null) return (0, 0);

        var counts = await _characters.ReindexOcRolesAsync(guildId);
        var configuration = await _settings.GetOcRoleConfigurationAsync(guildId);
        var allSequentialIds = configuration.SequentialRoleIds
            .Concat(previouslyManagedSequentialRoleIds)
            .ToHashSet();
        var memberIds = counts.Keys.Concat(guild.Users
                .Where(user => !user.IsBot && user.Roles.Any(role => allSequentialIds.Contains(role.Id)))
                .Select(user => user.Id))
            .Distinct()
            .ToArray();

        var synced = 0;
        var failed = 0;
        foreach (var memberId in memberIds)
        {
            var characterCount = counts.TryGetValue(memberId, out var count) ? count : 0;
            var result = await SyncMemberCoreAsync(
                guildId,
                memberId,
                characterCount,
                configuration,
                allSequentialIds);
            if (result.Success) synced++;
            else failed++;
        }
        return (synced, failed);
    }

    private async Task<CharacterRoleSyncResult> SyncMemberCoreAsync(
        ulong guildId,
        ulong userId,
        int characterCount,
        CharacterRoleConfiguration configuration,
        IReadOnlyCollection<ulong> allManagedSequentialRoleIds)
    {
        var guild = _client.GetGuild(guildId);
        var member = guild?.GetUser(userId);
        if (guild is null || member is null)
            return new CharacterRoleSyncResult(
                false,
                null,
                characterCount,
                configuration.SequentialRoleIds.Count,
                "Member not found.");

        var desiredSequentialIds = configuration.SequentialRoleIds.Take(characterCount).ToHashSet();
        var managedSequentialIds = allManagedSequentialRoleIds.ToHashSet();
        var rolesToRemove = member.Roles
            .Where(role => managedSequentialIds.Contains(role.Id) && !desiredSequentialIds.Contains(role.Id))
            .Select(role => role.Id)
            .ToHashSet();
        var rolesToAdd = new HashSet<ulong>(desiredSequentialIds);

        if (characterCount > 0)
        {
            rolesToAdd.UnionWith(configuration.DefaultRoleIds);
            rolesToRemove.UnionWith(configuration.RemovedRoleIds);
        }
        // Added roles win if an administrator accidentally places the same role in both lists.
        rolesToRemove.ExceptWith(rolesToAdd);

        var missingRoleIds = rolesToAdd.Where(roleId => guild.GetRole(roleId) is null).ToArray();
        var removableRoles = rolesToRemove
            .Select(guild.GetRole)
            .Where(role => role is not null && member.Roles.Any(memberRole => memberRole.Id == role.Id))
            .Cast<IRole>()
            .ToArray();
        var addableRoles = rolesToAdd
            .Where(roleId => member.Roles.All(role => role.Id != roleId))
            .Select(guild.GetRole)
            .Where(role => role is not null)
            .Cast<IRole>()
            .ToArray();

        try
        {
            if (removableRoles.Length > 0) await member.RemoveRolesAsync(removableRoles);
            if (addableRoles.Length > 0) await member.AddRolesAsync(addableRoles);

            ulong? assignedRoleId = characterCount > 0 && characterCount <= configuration.SequentialRoleIds.Count
                ? configuration.SequentialRoleIds[characterCount - 1]
                : null;
            return missingRoleIds.Length == 0
                ? new CharacterRoleSyncResult(true, assignedRoleId, characterCount, configuration.SequentialRoleIds.Count, null)
                : new CharacterRoleSyncResult(
                    false,
                    assignedRoleId,
                    characterCount,
                    configuration.SequentialRoleIds.Count,
                    "One or more configured Discord roles no longer exist.");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not synchronize OC roles for {member}: {exception}");
            return new CharacterRoleSyncResult(
                false,
                null,
                characterCount,
                configuration.SequentialRoleIds.Count,
                "Check Yoko's Manage Roles permission and role hierarchy.");
        }
    }

    private static CharacterRoleConfigurationResult Result(
        bool changed,
        CharacterRoleConfiguration configuration,
        int membersSynced,
        int membersFailed) =>
        new(
            changed,
            configuration.DefaultRoleIds,
            configuration.SequentialRoleIds,
            configuration.RemovedRoleIds,
            membersSynced,
            membersFailed);
}

internal sealed record CharacterCapacity(int CharacterCount, int RoleCapacity)
{
    public bool IsConfigured => RoleCapacity > 0;
    public bool IsFull => IsConfigured && CharacterCount >= RoleCapacity;
}

internal sealed record CharacterRoleSyncResult(
    bool Success,
    ulong? AssignedRoleId,
    int CharacterCount,
    int RoleCapacity,
    string? Error);

internal sealed record CharacterRoleConfigurationResult(
    bool Changed,
    IReadOnlyList<ulong> DefaultRoleIds,
    IReadOnlyList<ulong> SequentialRoleIds,
    IReadOnlyList<ulong> RemovedRoleIds,
    int MembersSynced,
    int MembersFailed);
