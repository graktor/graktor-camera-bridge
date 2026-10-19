# Publishes a single self-contained executable per OS (no .NET runtime install required on the
# branch PC) -- same reasoning as the main desktop app's per-platform builds. Run from this
# directory; all four can be built from Windows since this is plain .NET, not Electron.
$targets = @(
    @{ Rid = "win-x64";   Out = "win-x64" },
    @{ Rid = "osx-x64";   Out = "osx-x64" },
    @{ Rid = "osx-arm64"; Out = "osx-arm64" },
    @{ Rid = "linux-x64"; Out = "linux-x64" }
)

foreach ($t in $targets) {
    $outDir = "./publish/$($t.Out)"
    dotnet publish -c Release -r $t.Rid --self-contained true -p:PublishSingleFile=true -o $outDir
    Write-Host "Published: $PSScriptRoot\publish\$($t.Out)\ZendoriaCameraBridge$(if ($t.Rid -eq 'win-x64') { '.exe' } else { '' })"
}
