<#
.SYNOPSIS
    Rebuilds every picture in docs/images from the application itself.

.DESCRIPTION
    Runs `Cascade.exe --docshots`, which generates a sample log of its own and renders the README's stills
    off a real window, then hands the animation frames to ffmpeg. Nothing here reads a real log, a real
    filter set or the developer's settings, so the images are safe to publish and identical on any machine
    at the same DPI.

    Needs ffmpeg on PATH for the animations. Without it the stills are still refreshed and the GIFs are
    left as they are.

.EXAMPLE
    ./scripts/Build-DocImages.ps1
    ./scripts/Build-DocImages.ps1 -KeepWorkDir      # leave the frames behind to look at
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $KeepWorkDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$images = Join-Path $repo 'docs/images'
$work = Join-Path ([IO.Path]::GetTempPath()) ("cascade-docshots-" + [guid]::NewGuid().ToString('N'))

# The frame rate the harness writes at. Both ends have to agree or every animation plays at the wrong speed.
$fps = 10

Write-Host "Building $Configuration..." -ForegroundColor Cyan
& dotnet build (Join-Path $repo 'src/Cascade.App/Cascade.App.csproj') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$exe = Join-Path $repo "src/Cascade.App/bin/$Configuration/net10.0-windows/Cascade.exe"
if (-not (Test-Path $exe)) { throw "Cascade.exe not found at $exe" }

New-Item -ItemType Directory -Path $work -Force | Out-Null
New-Item -ItemType Directory -Path $images -Force | Out-Null

Write-Host "Rendering..." -ForegroundColor Cyan
# Start-Process -Wait, not the call operator: Cascade.exe is a GUI-subsystem binary, and PowerShell does not
# wait for one of those - it would go looking for the pictures before a single one had been written.
$render = Start-Process -FilePath $exe -ArgumentList @('--docshots', $work) -NoNewWindow -Wait -PassThru
if ($render.ExitCode -ne 0) { throw "--docshots failed with exit code $($render.ExitCode)" }

$stills = Get-ChildItem -Path $work -Filter *.png -File
foreach ($png in $stills) {
    Copy-Item $png.FullName (Join-Path $images $png.Name) -Force
}
Write-Host "$($stills.Count) stills" -ForegroundColor Green

$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
$frameDirs = @(Get-ChildItem -Path (Join-Path $work 'frames') -Directory -ErrorAction SilentlyContinue)

if (-not $ffmpeg) {
    Write-Warning "ffmpeg is not on PATH; $($frameDirs.Count) animation(s) left unbuilt."
} else {
    foreach ($dir in $frameDirs) {
        $gif = Join-Path $images ($dir.Name + '.gif')
        # stats_mode=diff weights the palette towards what actually changes between frames, and dither=none
        # keeps text crisp - these are screenshots of flat colours, so there is nothing to dither.
        & ffmpeg -y -loglevel error -framerate $fps -i (Join-Path $dir.FullName 'f%04d.png') `
            -filter_complex "[0:v] split [a][b];[a] palettegen=max_colors=64:stats_mode=diff [p];[b][p] paletteuse=dither=none:diff_mode=rectangle" `
            -loop 0 $gif
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $($dir.Name)" }
        Write-Host ("{0,-16} {1,7:N0} KB" -f ($dir.Name + '.gif'), ((Get-Item $gif).Length / 1KB)) -ForegroundColor Green
    }
}

if ($KeepWorkDir) { Write-Host "Frames left in $work" }
else { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "`ndocs/images:" -ForegroundColor Cyan
Get-ChildItem $images -File | Sort-Object Name |
    ForEach-Object { "{0,-24} {1,7:N0} KB" -f $_.Name, ($_.Length / 1KB) }
