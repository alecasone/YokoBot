using Discord;
using Discord.WebSocket;
using Yoko.Bot.Commands;
using Yoko.Bot.Services;
namespace Yoko.Bot;

internal static class Program
{
    private static readonly TaskCompletionSource ShutdownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly CharacterStore Characters = new(Path.Combine(Environment.CurrentDirectory, "data", "characters.json"));
    private static readonly CharacterSettingsStore CharacterSettings = new(Path.Combine(Environment.CurrentDirectory, "data", "character-settings.json"));
    private static readonly UserStore Users = new(Path.Combine(Environment.CurrentDirectory, "data", "users.json"));
    private static readonly AutoModerationRuleStore AutoModerationRules = new(Path.Combine(Environment.CurrentDirectory, "data", "automod-rules.json"));
    private static readonly VerificationSettingsStore VerificationSettings = new(Path.Combine(Environment.CurrentDirectory, "data", "verification-settings.json"));
    private static readonly string[] Rooms =
    {
        "ATTIC", "BASEMENT", "BEDROOM", "CELLAR", "HALLWAY", "KITCHEN", "LIBRARY", "PANTRY"
    };
    private static readonly string[] Warnings =
    {
        "Be careful.",
        "Do not look behind you.",
        "It knows you are here.",
        "Leave the door closed.",
        "The house remembers.",
        "You should not be alone tonight."
    };

    private static readonly DiscordSocketClient Client = new(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers | GatewayIntents.MessageContent | GatewayIntents.GuildMessages,
        AlwaysDownloadUsers = true,
        LogGatewayIntentWarnings = false
    });
    private static readonly AutoModerationService AutoModerator = new(Client, Users, AutoModerationRules);
    private static readonly VerificationService Verification = new(Client, VerificationSettings, AutoModerator);

    private static bool _commandsRegistered;

    public static async Task Main()
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("DISCORD_BOT_TOKEN is not set. See README.md for setup instructions.");
            Environment.ExitCode = 1;
            return;
        }

        Client.Log += LogAsync;
        Client.Ready += RegisterCommandsAsync;
        Client.Ready += AutoModerator.StartAsync;
        Client.UserJoined += AutoModerator.HandleUserJoinedAsync;
        Client.SlashCommandExecuted += HandleSlashCommandAsync;
        Client.AutocompleteExecuted += HandleAutocompleteAsync;
        Client.MessageReceived += HandleMessageReceivedAsync;

        await Client.LoginAsync(TokenType.Bot, token);
        await Client.StartAsync();
        await ShutdownSignal.Task;
        AutoModerator.Stop();
        await Client.StopAsync();
        await Client.LogoutAsync();
    }

    private static async Task RegisterCommandsAsync()
    {
        if (_commandsRegistered) return;

        var pingCommand = new SlashCommandBuilder()
            .WithName("ping")
            .WithDescription("Checks whether Yoko is online.")
            .Build();

        var shutdownCommand = new SlashCommandBuilder()
            .WithName("shutdown")
            .WithDescription("Consults the Ouija board, then shuts Yoko down.")
            .WithDefaultMemberPermissions(GuildPermission.Administrator)
            .Build();

        ApplicationCommandProperties[] commands =
            [pingCommand, shutdownCommand, .. CharacterCommands.Build(), CharacterAdminCommands.Build(),
             AutoModerationCommands.Build(), VerificationCommands.VerifyCommand(), VerificationCommands.AdminCommand(),
             DebugCommands.Build()];

        var testGuildIdText = Environment.GetEnvironmentVariable("DISCORD_TEST_GUILD_ID");
        if (ulong.TryParse(testGuildIdText, out var testGuildId))
        {
            await Client.Rest.BulkOverwriteGuildCommands(commands, testGuildId);
            Console.WriteLine($"Registered commands in test guild {testGuildId}.");
        }
        else
        {
            await Client.Rest.BulkOverwriteGlobalCommands(commands);
            Console.WriteLine("Registered commands globally. Discord may take up to an hour to show them.");
        }

        _commandsRegistered = true;
    }

    private static async Task HandleSlashCommandAsync(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case "ping":
                await command.RespondAsync($"Pong! `{Client.Latency} ms`");
                break;
            case "shutdown":
                await PerformShutdownAsync(command);
                break;
            case "character":
                await CharacterCommands.HandleAsync(command, Characters, CharacterSettings);
                break;
            case "charadmin":
                await CharacterAdminCommands.HandleAsync(command, CharacterSettings);
                break;
            case "automod":
                await AutoModerationCommands.HandleAsync(command, AutoModerationRules);
                break;
            case "verify":
                await VerificationCommands.HandleVerifyAsync(command, Verification);
                break;
            case "verifyadmin":
                await VerificationCommands.HandleAdminAsync(command, VerificationSettings);
                break;
            case "debug":
                await DebugCommands.HandleAsync(command, AutoModerator);
                break;
            default:
                await command.RespondAsync("I don't know that command yet.", ephemeral: true);
                break;
        }
    }

    private static async Task PerformShutdownAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();

        var room = Rooms[Random.Shared.Next(Rooms.Length)];
        var warning = Warnings[Random.Shared.Next(Warnings.Length)];

        await command.ModifyOriginalResponseAsync(message =>
            message.Content = "The planchette begins to move...\n\n" + RenderBoard(room, -1));

        for (var index = 0; index < room.Length; index++)
        {
            await Task.Delay(650);
            var currentIndex = index;
            await command.ModifyOriginalResponseAsync(message =>
                message.Content = RenderBoard(room, currentIndex));
        }

        await Task.Delay(900);
        await command.ModifyOriginalResponseAsync(message =>
            message.Content = $"The board spelled **{room}**.\n\n*{warning}*");

        await Task.Delay(2500);
        await command.ModifyOriginalResponseAsync(message => message.Content = "**Goodbye.**");
        await Task.Delay(1000);
        ShutdownSignal.TrySetResult();
    }

    private static string RenderBoard(string room, int currentIndex)
    {
        var spelled = currentIndex < 0 ? "..." : room[..(currentIndex + 1)];
        var currentLetter = currentIndex < 0 ? "?" : room[currentIndex].ToString();

        return "```\n" +
               "       YES                 NO\n\n" +
               "  A B C D E F G H I J K L M\n" +
               "   N O P Q R S T U V W X Y Z\n\n" +
               $"             ◇ {currentLetter} ◇\n\n" +
               "            GOODBYE\n" +
               "```\n" +
               $"The planchette hovers over **{currentLetter}**...  `{spelled}`";
    }

    private static Task LogAsync(LogMessage message)
    {
        Console.WriteLine($"[{message.Severity}] {message.Source}: {message.Message} {message.Exception}");
        return Task.CompletedTask;
    }

    private static Task HandleAutocompleteAsync(SocketAutocompleteInteraction interaction) =>
        interaction.Data.CommandName switch
        {
            "character" => CharacterCommands.HandleAutocompleteAsync(interaction, Characters, CharacterSettings),
            "charadmin" => CharacterAdminCommands.HandleAutocompleteAsync(interaction, CharacterSettings),
            "automod" => AutoModerationCommands.HandleAutocompleteAsync(interaction, AutoModerationRules),
            "verify" or "verifyadmin" => VerificationCommands.HandleAutocompleteAsync(interaction, VerificationSettings),
            _ => interaction.RespondAsync([])
        };

    private static async Task HandleMessageReceivedAsync(SocketMessage message)
    {
        await AutoModerator.RecordMessageAsync(message);
        if (await AutoModerator.HandleApprovalMessageAsync(message)) return;
        if (await AutoModerationCommands.HandleWizardMessageAsync(message, AutoModerationRules)) return;
        if (await VerificationCommands.HandleWizardMessageAsync(message, VerificationSettings)) return;
        await CharacterCommands.HandleFilloutMessageAsync(message, Characters);
    }
}
