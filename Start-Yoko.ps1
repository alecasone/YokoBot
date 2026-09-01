[CmdletBinding()]
param(
    [switch]$NoRestart,
    [switch]$SkipRestore,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $scriptRoot "Yoko.Bot.csproj"
$settingsPath = Join-Path $scriptRoot "local.settings.json"
$settingsTemplatePath = Join-Path $scriptRoot "local.settings.example.json"
$locationPushed = $false

function Write-Section {
    param([string]$Message)

    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Get-LocalSettingsIssues {
    param([string]$Path)

    $issues = [System.Collections.Generic.List[string]]::new()

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $issues.Add("local.settings.json does not exist.")
        return $issues
    }

    try {
        $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        $issues.Add("local.settings.json is not valid JSON. Check commas, quotes, and braces.")
        return $issues
    }

    $discordToken = [string]$settings.discordBotToken
    if ([string]::IsNullOrWhiteSpace($discordToken) -or $discordToken.Trim() -match '^paste-') {
        $issues.Add("discordBotToken needs the real Discord bot token.")
    }

    foreach ($idSetting in @(
        @{ Name = "discordTestGuildId"; Description = "Discord test server ID" },
        @{ Name = "discordDefaultChannelId"; Description = "Discord default channel ID" }
    )) {
        $idValue = [string]$settings.($idSetting.Name)
        if ([string]::IsNullOrWhiteSpace($idValue)) {
            continue
        }

        [UInt64]$parsedId = 0
        if (-not [UInt64]::TryParse($idValue.Trim(), [ref]$parsedId)) {
            $issues.Add("$($idSetting.Description) must contain digits only, or be an empty string.")
        }
    }

    $githubToken = [string]$settings.githubPagesToken
    if (-not [string]::IsNullOrWhiteSpace($githubToken) -and $githubToken.Trim() -match '^paste-') {
        $issues.Add("githubPagesToken still contains template text. Paste a token, or use an empty string if this PC will not publish the site.")
    }

    return $issues
}

function Open-LocalSettingsEditor {
    param([string]$Path)

    Write-Host "Opening local.settings.json in Notepad. Save it, then close Notepad to continue." -ForegroundColor Yellow
    Start-Process -FilePath "notepad.exe" -ArgumentList ('"{0}"' -f $Path) -Wait
}

try {
    try {
        $Host.UI.RawUI.WindowTitle = "Yoko Bot Host"
    }
    catch {
        # Some non-interactive terminals do not expose a window title.
    }

    Push-Location -LiteralPath $scriptRoot
    $locationPushed = $true

    Write-Host "Yoko Bot launcher" -ForegroundColor Magenta
    Write-Host "Working folder: $scriptRoot"

    Write-Section "Checking .NET"
    if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
        Write-Host "The .NET 8 SDK is not installed or is not on PATH." -ForegroundColor Red
        Write-Host "Install it from: https://dotnet.microsoft.com/download/dotnet/8.0"
        exit 10
    }

    $resolvedSdkOutput = @(& dotnet --version 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "This repository requires a .NET SDK compatible with global.json (currently .NET 8.0.412)." -ForegroundColor Red
        Write-Host "Install the .NET 8 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0"
        if ($resolvedSdkOutput.Count -gt 0) {
            Write-Host ($resolvedSdkOutput -join [Environment]::NewLine) -ForegroundColor DarkYellow
        }
        exit 11
    }
    Write-Host "Using .NET SDK $($resolvedSdkOutput[-1])." -ForegroundColor Green

    Write-Section "Checking local settings"
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        if (-not (Test-Path -LiteralPath $settingsTemplatePath -PathType Leaf)) {
            Write-Host "Neither local.settings.json nor its template could be found." -ForegroundColor Red
            exit 12
        }

        Copy-Item -LiteralPath $settingsTemplatePath -Destination $settingsPath
        Write-Host "Created the ignored local.settings.json file from the safe template." -ForegroundColor Yellow
    }

    while ($true) {
        $settingsIssues = @(Get-LocalSettingsIssues -Path $settingsPath)
        if ($settingsIssues.Count -eq 0) {
            break
        }

        Write-Host "local.settings.json needs attention:" -ForegroundColor Red
        foreach ($issue in $settingsIssues) {
            Write-Host "  - $issue" -ForegroundColor Red
        }

        if ($ValidateOnly) {
            exit 13
        }

        $settingsChoice = (Read-Host "Press Enter to edit it, or type Q to quit").Trim()
        if ($settingsChoice -match '^(q|quit)$') {
            exit 13
        }

        Open-LocalSettingsEditor -Path $settingsPath
    }
    Write-Host "Local settings are ready. Secret values were not displayed." -ForegroundColor Green

    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        Write-Host "Yoko.Bot.csproj was not found beside this launcher." -ForegroundColor Red
        exit 14
    }

    if ($ValidateOnly) {
        Write-Section "Ready"
        Write-Host "This computer is ready to host Yoko." -ForegroundColor Green
        exit 0
    }

    if (-not $SkipRestore) {
        Write-Section "Restoring dependencies"
        & dotnet restore $projectPath --nologo
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Dependency restore failed. Check the messages above and try again." -ForegroundColor Red
            exit $LASTEXITCODE
        }
    }

    while ($true) {
        Write-Section "Starting Yoko"
        Write-Host "Keep this window open while the bot is online. Press Ctrl+C to stop it." -ForegroundColor Yellow
        & dotnet run --project $projectPath --configuration Release --no-restore
        $botExitCode = $LASTEXITCODE

        if ($botExitCode -eq 0) {
            Write-Host "Yoko stopped cleanly. The launcher will not restart it." -ForegroundColor Green
            break
        }

        Write-Host "Yoko exited unexpectedly with code $botExitCode." -ForegroundColor Red
        if ($NoRestart) {
            exit $botExitCode
        }

        Write-Host "Restarting in 5 seconds. Press Ctrl+C to cancel." -ForegroundColor Yellow
        Start-Sleep -Seconds 5
    }
}
finally {
    if ($locationPushed) {
        Pop-Location
    }
}

