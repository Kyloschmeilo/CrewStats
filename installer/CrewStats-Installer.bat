<# : CrewStats-Installer - der Batch-Teil startet nur PowerShell mit dem Rest dieser Datei
@echo off
chcp 65001 >nul
set "INSTALLER_BAT=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Expression (Get-Content -LiteralPath '%~f0' -Raw -Encoding UTF8)"
echo.
pause
exit /b
#>

# ==========================================================================================
#  CrewStats-Installer: installiert BepInEx (64-Bit) + die CrewStats-Mod in Among Us (Steam oder Epic Games)
#  und aktualisiert sie. Einfach nochmal starten = reparieren/aktualisieren.
#  Quelle: https://github.com/Kyloschmeilo/CrewStats
# ==========================================================================================

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # sonst sind Downloads in PowerShell 5 extrem langsam
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Host.UI.RawUI.WindowTitle = 'CrewStats Installer'

$Repo          = 'Kyloschmeilo/CrewStats'   # GitHub-Repo mit den Releases (muss zu Updater.cs passen)
$BepInExUrl    = 'https://builds.bepinex.dev/projects/bepinex_be/735/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.735%2B5fef357.zip'
$BepInExSha256 = 'BADEF8112853A00939A0DF6CA143BC0A4E3DC02BD4D21B873302731BFA0E4DF4'
$ConfigName    = 'de.amongusmanager.crewstats.cfg'
$WebhookPattern = '^https://(ptb\.|canary\.)?discord(app)?\.com/api/webhooks/\d+/[\w-]+$'
$WebsitePattern = '^https?://[^/\s]+/api/mod/[\w-]{8,}$'   # Stats-Website (ab CrewStats 1.8.0, nur Auto-Join)

function Step($text) { Write-Host "`n> $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "  OK  $text" -ForegroundColor Green }
function Warn($text) { Write-Host "  !!  $text" -ForegroundColor Yellow }
function Fail($text) { Write-Host "`n  FEHLER: $text" -ForegroundColor Red; exit 1 }

function Get-Machine([string]$path) {
    # Liest aus dem PE-Header, ob eine .exe/.dll 32- oder 64-Bit ist
    $stream = [IO.File]::OpenRead($path)
    try {
        $buffer = New-Object byte[] 4096
        [void]$stream.Read($buffer, 0, $buffer.Length)
        $pe = [BitConverter]::ToInt32($buffer, 0x3C)
        return [BitConverter]::ToUInt16($buffer, $pe + 4)
    } finally { $stream.Dispose() }
}
$X64 = 0x8664
$SteamAppId = '945360'   # Among Us auf Steam

function Test-Admin {
    try { return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    catch { return $false }
}

function New-Game([string]$dir, [string]$store, [string]$launch) {
    return [pscustomobject]@{ Dir = $dir.TrimEnd('\', '/'); Store = $store; Launch = $launch }
}

function Find-SteamGames {
    # Steam: Bibliotheken aus der Registry bzw. libraryfolders.vdf
    $steam = $env:CREWSTATS_STEAM_DIR   # nur zum Testen
    if (-not $steam) {
        try { $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath } catch { }
    }
    if (-not $steam) { return }
    $candidates = @(Join-Path $steam 'steamapps\common\Among Us')
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $candidates += Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Among Us'
        }
    }
    foreach ($dir in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath (Join-Path $dir 'Among Us.exe')) { New-Game $dir 'Steam' "steam://rungameid/$SteamAppId" }
    }
}

function Find-EpicGames {
    # Epic Games: jedes installierte Spiel hat ein Manifest (JSON) mit Ordner und Start-Kennung
    $manifests = $env:CREWSTATS_EPIC_MANIFESTS   # nur zum Testen
    if (-not $manifests -and $env:ProgramData) { $manifests = Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests' }
    if (-not $manifests -or -not (Test-Path -LiteralPath $manifests)) { return }
    foreach ($file in Get-ChildItem -LiteralPath $manifests -Filter '*.item' -File -ErrorAction SilentlyContinue) {
        try { $m = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { continue }
        $dir = [string]$m.InstallLocation
        if (-not $dir -or -not (Test-Path -LiteralPath (Join-Path $dir 'Among Us.exe'))) { continue }
        if ([string]$m.DisplayName -notmatch 'Among Us' -and [string]$m.LaunchExecutable -notmatch 'Among Us\.exe$') { continue }
        $launch = $null
        if ($m.CatalogNamespace -and $m.CatalogItemId -and $m.AppName) {
            $id = [uri]::EscapeDataString("$($m.CatalogNamespace):$($m.CatalogItemId):$($m.AppName)")
            $launch = "com.epicgames.launcher://apps/$($id)?action=launch&silent=true"
        } elseif ($m.AppName) {
            $launch = "com.epicgames.launcher://apps/$($m.AppName)?action=launch&silent=true"
        }
        New-Game $dir 'Epic' $launch
    }
}

function Get-GameAt([string]$dir) {
    # Von Hand gewählter Ordner: Epic-Installationen haben einen Ordner .egstore
    $dir = $dir.TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath (Join-Path $dir '.egstore'))) { return New-Game $dir 'Steam' "steam://rungameid/$SteamAppId" }
    $known = @(Find-EpicGames) | Where-Object { $_.Dir -eq $dir } | Select-Object -First 1
    if ($known) { return $known }
    return New-Game $dir 'Epic' $null
}

function Select-Folder {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true }
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = 'Among-Us-Ordner auswählen (der Ordner mit "Among Us.exe")'
        if ($dialog.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { return $dialog.SelectedPath }
    } catch {
        Warn 'Das Auswahlfenster ging nicht auf - bitte die Adresse einfügen.'
    }
    return ''
}

function Find-AmongUs {
    if ($env:CREWSTATS_GAME_DIR) { return Get-GameAt $env:CREWSTATS_GAME_DIR }   # nur zum Testen
    $games = @(@(Find-SteamGames) + @(Find-EpicGames) | Where-Object { $_ } | Sort-Object Dir -Unique)
    if ($games.Count -eq 1) { return $games[0] }
    if ($games.Count -gt 1) {
        Write-Host '      Among Us ist mehrfach installiert:'
        for ($i = 0; $i -lt $games.Count; $i++) { Write-Host "      [$($i + 1)] $($games[$i].Store): $($games[$i].Dir)" }
        while ($true) {
            $pick = (Read-Host '      Welches? (Enter = 1)').Trim()
            if (-not $pick) { return $games[0] }
            $n = 0
            if ([int]::TryParse($pick, [ref]$n) -and $n -ge 1 -and $n -le $games.Count) { return $games[$n - 1] }
            Warn "Bitte eine Zahl von 1 bis $($games.Count) eingeben."
        }
    }
    Warn 'Among Us wurde nicht automatisch gefunden (weder über Steam noch über Epic Games).'
    Write-Host '      Steam: Rechtsklick auf Among Us -> Verwalten -> Lokale Dateien durchsuchen.'
    Write-Host '      Epic Games: Bibliothek -> bei Among Us auf ... -> Verwalten -> Ordner-Symbol bei Installation.'
    Write-Host '      Dann oben die Adresse kopieren und hier einfügen - oder nur Enter für ein Auswahlfenster.'
    while ($true) {
        $dir = (Read-Host '      Among-Us-Ordner').Trim().Trim('"')
        if (-not $dir) { $dir = Select-Folder }
        if ($dir -and (Test-Path -LiteralPath (Join-Path $dir 'Among Us.exe'))) { return Get-GameAt $dir }
        Warn 'Dort liegt keine "Among Us.exe". Nochmal versuchen.'
    }
}

function Assert-Writable([string]$dir) {
    # Epic Games installiert nach "C:\Program Files" - dort darf man nur mit Administratorrechten etwas ändern
    $probe = Join-Path $dir ".installer-test-$PID"
    try { [IO.File]::WriteAllText($probe, 'x'); Remove-Item -LiteralPath $probe -Force; return } catch { }
    if ((Test-Admin) -or -not $env:INSTALLER_BAT) { Fail "Der Installer darf in $dir nichts ändern." }
    Warn "In $dir darf der Installer nur mit Administratorrechten etwas ändern (bei Epic Games normal)."
    Write-Host '      Windows fragt gleich nach - danach geht es in einem neuen Fenster weiter.'
    try { Start-Process -FilePath $env:ComSpec -ArgumentList "/c `"`"$env:INSTALLER_BAT`"`"" -Verb RunAs }
    catch { Fail 'Ohne Administratorrechte geht es nicht. Rechtsklick auf den Installer -> Als Administrator ausführen.' }
    exit 0
}

function Grant-UserWrite([string]$dir) {
    # Nach einer Installation als Administrator: BepInEx schreibt beim Spielen Logs/Cache, die Mods aktualisieren sich
    # selbst - das muss auch ohne Administratorrechte gehen
    if (-not (Test-Admin) -or -not (Test-Path -LiteralPath $dir)) { return }
    & icacls $dir /grant '*S-1-5-32-545:(OI)(CI)M' /T /Q | Out-Null
    if ($LASTEXITCODE -eq 0) { Ok 'Schreibrechte für BepInEx gesetzt (für Logs und Auto-Update).' }
    else { Warn "Konnte die Schreibrechte für $dir nicht setzen - Auto-Update geht dann evtl. nicht." }
}

function Start-Game($game) {
    if ($game.Launch) { Start-Process $game.Launch }
    else { Warn 'Starte Among Us bitte selbst über den Epic Games Launcher.' }
}

function Wait-GameClosed {
    while (Get-Process 'Among Us' -ErrorAction SilentlyContinue) {
        Warn 'Among Us läuft noch. Bitte schließen und dann Enter drücken.'
        [void](Read-Host)
    }
}

function Install-BepInEx([string]$game) {
    $winhttp = Join-Path $game 'winhttp.dll'
    $coreclr = Join-Path $game 'dotnet\coreclr.dll'
    $ready = (Test-Path $winhttp) -and (Test-Path $coreclr) -and (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.Core.dll')) `
        -and (Get-Machine $winhttp) -eq $X64 -and (Get-Machine $coreclr) -eq $X64
    if ($ready) { Ok 'BepInEx (64-Bit) ist schon installiert.'; return }

    $temp = Join-Path $env:TEMP "crewstats-bepinex-$PID"
    New-Item -ItemType Directory -Force $temp | Out-Null
    try {
        $zip = Join-Path $temp 'bepinex.zip'
        if ($env:CREWSTATS_BEPINEX_ZIP) { Copy-Item $env:CREWSTATS_BEPINEX_ZIP $zip }   # nur zum Testen
        else { Write-Host '      Lade BepInEx herunter (ca. 34 MB)...'; Invoke-WebRequest $BepInExUrl -OutFile $zip -UseBasicParsing }
        if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $BepInExSha256) { Fail 'Der BepInEx-Download ist beschädigt oder wurde verändert. Bitte später nochmal versuchen.' }
        Expand-Archive $zip (Join-Path $temp 'x') -Force
        $new = Join-Path $temp 'x'

        # Vorhandenes (z.B. altes 32-Bit-)BepInEx sichern statt löschen
        $old = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt', 'dotnet', 'BepInEx\core', 'BepInEx\interop', 'BepInEx\cache') |
            Where-Object { Test-Path (Join-Path $game $_) }
        if ($old) {
            $backup = Join-Path $game ('_backup_bepinex_' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
            foreach ($item in $old) {
                $target = Join-Path $backup $item
                New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
                Move-Item (Join-Path $game $item) $target
            }
            Ok "Altes BepInEx gesichert in $(Split-Path $backup -Leaf)"
        }
        foreach ($item in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt', 'dotnet') {
            Copy-Item (Join-Path $new $item) (Join-Path $game $item) -Recurse -Force
        }
        New-Item -ItemType Directory -Force (Join-Path $game 'BepInEx\plugins') | Out-Null
        Copy-Item (Join-Path $new 'BepInEx\core') (Join-Path $game 'BepInEx\core') -Recurse -Force
        if (-not (Test-Path (Join-Path $game 'BepInEx\patchers'))) { Copy-Item (Join-Path $new 'BepInEx\patchers') (Join-Path $game 'BepInEx\patchers') -Recurse }
        Ok 'BepInEx 6.0.0-be.735 (64-Bit) installiert.'
    } finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Install-Mod([string]$game) {
    $plugins = Join-Path $game 'BepInEx\plugins'
    $dll = Join-Path $plugins 'CrewStats.dll'
    $fresh = "$dll.new"
    if ($env:CREWSTATS_MOD_DLL) {   # nur zum Testen
        Copy-Item $env:CREWSTATS_MOD_DLL $fresh; $version = 'lokal'
    } else {
        try {
            $release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ 'User-Agent' = 'CrewStats-Installer' }
        } catch { Fail "Konnte die neueste Version nicht von GitHub abrufen: $($_.Exception.Message)" }
        $asset = $release.assets | Where-Object name -eq 'CrewStats.dll' | Select-Object -First 1
        if (-not $asset) { Fail "Das neueste Release ($($release.tag_name)) enthält keine CrewStats.dll." }
        Invoke-WebRequest $asset.browser_download_url -OutFile $fresh -UseBasicParsing -Headers @{ 'User-Agent' = 'CrewStats-Installer' }
        $version = $release.tag_name
    }
    $head = [IO.File]::ReadAllBytes($fresh)
    if ($head.Length -lt 10000 -or $head[0] -ne 0x4D -or $head[1] -ne 0x5A) { Remove-Item $fresh; Fail 'Der Mod-Download ist keine gültige DLL.' }
    Remove-Item "$dll.old" -ErrorAction SilentlyContinue
    Move-Item $fresh $dll -Force
    Ok "CrewStats $version installiert."
}

function Set-ConfigUrl([string]$game, [string]$section, [string]$key, [string]$label, [string[]]$intro, [string]$skipWarning, [bool]$website = $false) {
    $configDir = Join-Path $game 'BepInEx\config'
    $config = Join-Path $configDir $ConfigName
    $keyPattern = "(?m)^[ \t]*$key[ \t]*=[^\r\n]*"
    $current = ''
    if (Test-Path $config) {
        $line = Select-String -Path $config -Pattern "^\s*$key\s*=\s*(.*)$" | Select-Object -First 1
        if ($line) { $current = $line.Matches[0].Groups[1].Value.Trim() }
    }
    foreach ($text in $intro) { Write-Host "      $text" }
    if ($current) { Write-Host '      Es ist schon eine URL eingetragen. Enter = behalten.' } else { Write-Host '      Enter = überspringen.' }
    while ($true) {
        $url = (Read-Host "      $label").Trim()
        if (-not $url) { if ($current) { Ok "$label behalten." } else { Warn $skipWarning }; return }
        $url = $url.TrimEnd('/')
        if ($url -match $WebhookPattern) { break }
        if ($website -and $url -match $WebsitePattern) { break }
        if ($website) { Warn 'Das ist keine Auto-Join-URL - kopiere sie genau so, wie /autojoin sie anzeigt.' }
        else { Warn 'Das ist keine Discord-Webhook-URL (https://discord.com/api/webhooks/...).' }
    }
    New-Item -ItemType Directory -Force $configDir | Out-Null
    if (Test-Path $config) {
        $text = Get-Content $config -Raw -Encoding UTF8
        if ($text -match $keyPattern) { $text = $text -replace $keyPattern, "$key = $url" }
        else { $text = $text.TrimEnd() + "`r`n`r`n[$section]`r`n`r`n$key = $url`r`n" }
    } else {
        $text = "[$section]`r`n`r`n$key = $url`r`n"   # BepInEx ergänzt den Rest beim ersten Start
    }
    [IO.File]::WriteAllText($config, $text, (New-Object Text.UTF8Encoding $false))
    Ok "$label gespeichert."
}

function Set-Webhook([string]$game) {
    Set-ConfigUrl $game 'Discord' 'WebhookUrl' 'Webhook-URL' @(
        'Die Webhook-URL brauchst du nur, wenn DU hostest (Rundenauswertung + Auto-Mute).',
        'Du bekommst sie vom Server-Admin. Sie ist geheim - nicht weitergeben!'
    ) 'Ohne Webhook-URL werden keine Runden gesendet.'
}

function Set-AutoJoin([string]$game) {
    Set-ConfigUrl $game 'AutoJoin' 'Url' 'Auto-Join-URL' @(
        'Mit Auto-Join tritt Among Us im Hauptmenü automatisch der neuesten Lobby aus dem Discord bei.',
        'Die URL zeigt dir der Bot im Discord mit /autojoin.'
    ) 'Ohne Auto-Join-URL gibt es kein automatisches Beitreten.' $true
}

function Uninstall([string]$game) {
    $plugins = Join-Path $game 'BepInEx\plugins'
    Remove-Item (Join-Path $plugins 'CrewStats.dll*') -Force -ErrorAction SilentlyContinue
    Ok 'CrewStats entfernt.'
    $others = Get-ChildItem $plugins -Filter '*.dll' -Recurse -ErrorAction SilentlyContinue
    # The Other Roles (Epic) liegt abgeschaltet nicht in plugins, braucht BepInEx aber trotzdem
    $tor = Test-Path (Join-Path $game 'BepInEx\theotherroles\dateien.txt')
    if ($others -or $tor) { Warn 'Andere Mods gefunden - BepInEx bleibt aktiv.'; return }
    Remove-Item (Join-Path $game 'winhttp.dll') -Force -ErrorAction SilentlyContinue
    Ok 'BepInEx deaktiviert - Among Us startet wieder ganz normal.'
}

# ==========================================================================================

Write-Host ''
Write-Host '  ================================' -ForegroundColor Red
Write-Host '     CrewStats für Among Us' -ForegroundColor White
Write-Host '  ================================' -ForegroundColor Red
Write-Host '  Rundenstatistik, Discord-Auto-Mute und /rundensettings für den Among Us Manager.'
Write-Host ''
Write-Host '  [1] Installieren / Aktualisieren  (Enter)'
Write-Host '  [2] Deinstallieren'
$choice = (Read-Host '  Auswahl').Trim()

Step 'Suche Among Us...'
$found = Find-AmongUs
$game = $found.Dir
Ok "$game ($($found.Store))"
Assert-Writable $game
Wait-GameClosed

if ($choice -eq '2') {
    Step 'Deinstalliere...'
    Uninstall $game
    exit 0
}

if ((Get-Machine (Join-Path $game 'Among Us.exe')) -ne $X64) {
    Fail "Dein Among Us ist veraltet (32-Bit). Bitte zuerst über $(if ($found.Store -eq 'Epic') { 'Epic Games' } else { 'Steam' }) aktualisieren."
}

Step 'BepInEx (Mod-Loader)'
Install-BepInEx $game

Step 'CrewStats-Mod'
Install-Mod $game
Grant-UserWrite (Join-Path $game 'BepInEx')

Step 'Discord-Webhook'
Set-Webhook $game

Step 'Auto-Join'
Set-AutoJoin $game

Write-Host ''
Write-Host '  Fertig!' -ForegroundColor Green
Write-Host '  - Der erste Start dauert 1-2 Minuten (BepInEx bereitet alles vor).'
Write-Host '  - Auto-Mute an/aus: im Spiel ESC -> Reiter "Allgemein" -> "Auto-Mute".'
Write-Host '  - Auto-Join: im Hauptmenü F5 = neueste Lobby sofort beitreten, F6 = automatisch an/aus.'
Write-Host '  - Verknüpfe einmal deinen Friendcode im Discord: /verknuepfen'
Write-Host '  - Die Mod aktualisiert sich ab jetzt selbst.'
if ((Read-Host "`n  Among Us jetzt starten? (j/n)").Trim() -match '^(j|ja|y|yes)$') { Start-Game $found }
