# Builds the single-file, self-contained executable into .\dist, and copies it to .\MixLane.exe
# Refuses a version that's already in Versions\Archive: raise <Version> in the csproj first.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "MixLane.csproj"
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$archive = Join-Path $root "Versions\Archive\MixLane-$version.exe"
if (Test-Path $archive) { throw "Version $version is already archived. Raise <Version> in the csproj, then publish again." }

dotnet publish $proj -c Release -o (Join-Path $root "dist") --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $root "dist\MixLane.exe"
if (Test-Path $exe) {
    # Every build is kept: the current one at the top level, and a copy per version in Versions\Archive.
    New-Item -ItemType Directory -Force (Split-Path $archive) | Out-Null
    Copy-Item $exe $archive
    Copy-Item $exe (Join-Path $root "MixLane.exe")
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Output ""
    Write-Output "Built: $exe  ($mb MB)"
    Write-Output "Self-contained - copy it anywhere, no .NET runtime required."
} else {
    Write-Error "Publish did not produce $exe"
}
