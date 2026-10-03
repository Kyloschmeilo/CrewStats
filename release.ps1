# Veröffentlicht eine neue CrewStats-Version auf GitHub. Alle Mods aktualisieren sich danach beim
# nächsten Spielstart selbst.
#
# Voraussetzung: GitHub-CLI `gh` installiert und eingeloggt (gh auth login).
# Ablauf: 1. Version in CrewStats\CrewStats.csproj erhöhen (z.B. 1.1.0 -> 1.2.0)
#         2. .\release.ps1 "Was ist neu"

param([Parameter(Mandatory = $true)][string]$Notes)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$version = ([xml](Get-Content "$root\CrewStats\CrewStats.csproj")).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"

foreach ($file in "$root\CrewStats\Updater.cs", "$root\installer\CrewStats-Installer.bat") {
    if (Select-String -Path $file -Pattern 'DEIN-GITHUB-NAME' -Quiet) { throw "In $file ist noch kein GitHub-Repo eingetragen." }
}

cmd /c "gh release view $tag >nul 2>&1"
if ($LASTEXITCODE -eq 0) { throw "Release $tag gibt es schon - erst die Version in CrewStats.csproj erhöhen." }

dotnet build "$root\CrewStats" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build fehlgeschlagen.' }

# Quellcode-Stand zum Release festhalten
if (git -C $root status --porcelain) {
    git -C $root add -A
    git -C $root commit -m "CrewStats $version"
}
git -C $root push
if ($LASTEXITCODE -ne 0) { throw 'git push fehlgeschlagen.' }

gh release create $tag `
    "$root\CrewStats\bin\Release\net6.0\CrewStats.dll" `
    "$root\installer\CrewStats-Installer.bat" `
    --title "CrewStats $version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw 'Release konnte nicht erstellt werden.' }
Write-Host "CrewStats $version veröffentlicht." -ForegroundColor Green
