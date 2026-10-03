# Veröffentlicht eine neue CrewStats-Version auf GitHub. Alle Mods aktualisieren sich danach beim
# nächsten Spielstart selbst.
#
# Voraussetzung: GitHub-CLI `gh` installiert und eingeloggt (gh auth login).
# Ablauf: 1. Version in CrewStats\CrewStats.csproj erhöhen (z.B. 1.1.0 -> 1.2.0)
#         2. .\release.ps1 "Was ist neu"

param([Parameter(Mandatory = $true)][string]$Notes)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Externe Programme (git, gh, dotnet) schreiben auch harmlose Meldungen in den Fehlerkanal –
# Windows PowerShell 5 würde deshalb abbrechen. Entscheidend ist nur der Exit-Code.
function Invoke-Native([scriptblock]$Command, [string]$Failure) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | ForEach-Object { "$_" } } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw $Failure }
}

$version = ([xml](Get-Content "$root\CrewStats\CrewStats.csproj")).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"

foreach ($file in "$root\CrewStats\Updater.cs", "$root\installer\CrewStats-Installer.bat") {
    if (Select-String -Path $file -Pattern 'DEIN-GITHUB-NAME' -Quiet) { throw "In $file ist noch kein GitHub-Repo eingetragen." }
}

cmd /c "gh release view $tag >nul 2>&1"
if ($LASTEXITCODE -eq 0) { throw "Release $tag gibt es schon - erst die Version in CrewStats.csproj erhöhen." }

Invoke-Native { dotnet build "$root\CrewStats" -c Release } 'Build fehlgeschlagen.'

# Quellcode-Stand zum Release festhalten
if (git -C $root status --porcelain) {
    Invoke-Native { git -C $root add -A } 'git add fehlgeschlagen.'
    Invoke-Native { git -C $root commit -m "CrewStats $version" } 'git commit fehlgeschlagen.'
}
Invoke-Native { git -C $root push } 'git push fehlgeschlagen.'

Invoke-Native {
    gh release create $tag `
        "$root\CrewStats\bin\Release\net6.0\CrewStats.dll" `
        "$root\installer\CrewStats-Installer.bat" `
        --title "CrewStats $version" --notes $Notes
} 'Release konnte nicht erstellt werden.'
Write-Host "CrewStats $version veröffentlicht." -ForegroundColor Green
