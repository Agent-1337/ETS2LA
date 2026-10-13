$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$toolsPath = Join-Path $projectRoot '.tools'
$buildPath = Join-Path $projectRoot '.build'
$publishPath = Join-Path $projectRoot 'publish'
$previousPath = Join-Path $projectRoot 'publish.previous'
$stagingPath = Join-Path $buildPath ('staging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $toolsPath, $buildPath -Force | Out-Null
$buildLock = $null
$transcribing = $false

function Invoke-BuildCommand([string] $command, [string[]] $commandArgs) {
    Write-Host "$command $($commandArgs -join ' ')"
    # Windows PowerShell treats native stderr as ErrorRecords when redirecting it.
    # Keep those records in the log and judge native commands by their exit code.
    $savedErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $command @commandArgs 2>&1 | Tee-Object -FilePath (Join-Path $buildPath 'build.log') -Append | Out-Host
    } finally { $ErrorActionPreference = $savedErrorPreference }
    if ($LASTEXITCODE -ne 0) { throw "$command failed with exit code $LASTEXITCODE." }
}

function Remove-BuildDirectory([string] $path) {
    # Only these exact generated directories may be removed, never an unchecked computed path.
    $resolved = [IO.Path]::GetFullPath($path).TrimEnd('\')
    if ($resolved -ne $previousPath -and $resolved -ne $stagingPath) {
        throw "Refusing to remove an unexpected path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

try {
    $buildLock = [IO.File]::Open((Join-Path $buildPath 'build.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    Set-Content -LiteralPath (Join-Path $buildPath 'build.log') -Value '' -Encoding Unicode
    Start-Transcript -Path (Join-Path $buildPath 'setup.log') -Force | Out-Null
    $transcribing = $true
    foreach ($process in Get-Process ETS2LA -ErrorAction SilentlyContinue) {
        if ($process.Path -eq (Join-Path $publishPath 'ETS2LA.exe')) {
            throw 'Close ETS2LA before rebuilding. You can launch the previous build without rebuilding.'
        }
    }

    # Prefer this checkout's SDK, then a system SDK. A runtime alone cannot compile C#.
    $dotnet = Join-Path $toolsPath 'dotnet\dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) {
        $systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($systemDotnet -and ((& $systemDotnet.Source --list-sdks) -match '^1[0-9]\.')) {
            $dotnet = $systemDotnet.Source
        } else {
            Write-Host 'Installing the .NET 10 SDK locally (first build only)...'
            $installer = Join-Path $toolsPath 'dotnet-install.ps1'
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
            & $installer -Channel 10.0 -InstallDir (Join-Path $toolsPath 'dotnet') -NoPath
            if (!(Test-Path -LiteralPath $dotnet)) { throw '.NET SDK installation failed.' }
        }
    }
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    $bunCommand = Get-Command bun -ErrorAction SilentlyContinue
    $bun = Join-Path $toolsPath 'bun\bun-windows-x64\bun.exe'
    if ($bunCommand) { $bun = $bunCommand.Source }
    if (!(Test-Path -LiteralPath $bun)) {
        Write-Host 'Installing Bun locally (first build only)...'
        $bunArchive = Join-Path $toolsPath 'bun-windows-x64.zip'
        Invoke-WebRequest 'https://github.com/oven-sh/bun/releases/latest/download/bun-windows-x64.zip' -OutFile $bunArchive -UseBasicParsing
        Expand-Archive -LiteralPath $bunArchive -DestinationPath (Join-Path $toolsPath 'bun') -Force
        if (!(Test-Path -LiteralPath $bun)) { throw 'Bun installation failed.' }
        Remove-Item -LiteralPath $bunArchive
    }
    $env:PATH = "$(Split-Path -Parent $bun);$env:PATH"

    & (Join-Path $PSScriptRoot 'setup-perception.ps1')

    if (!(Test-Path -LiteralPath 'TruckLib\TruckLib\TruckLib.csproj')) {
        Invoke-BuildCommand 'git' @('submodule', 'update', '--init', '--recursive')
    }
    Write-Host 'Restoring C# dependencies and translation tools...'
    Invoke-BuildCommand $dotnet @('tool', 'restore')
    Invoke-BuildCommand $dotnet @('restore', 'ETS2LA.sln', '-r', 'win-x64', '-p:SelfContained=true')
    Write-Host 'Compiling the entire solution in Release mode...'
    Invoke-BuildCommand $dotnet @('build', 'ETS2LA.sln', '-c', 'Release', '-r', 'win-x64', '--no-restore', '--no-incremental', '-p:SelfContained=true', '-p:SkipTranslationExtraction=true', '-v:minimal', '-clp:ErrorsOnly;Summary')
    Write-Host 'Publishing a self-contained Windows x64 application...'
    Invoke-BuildCommand $dotnet @('publish', 'ETS2LA\ETS2LA.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-build', '--no-restore', '-o', $stagingPath)

    # Preserve downloaded plugins, libraries, and models on every rebuild. On the
    # first local build, reuse the installed application's plugin directories.
    $installedPath = Join-Path $env:LOCALAPPDATA 'ETS2LA\current'
    foreach ($directory in @('Plugins', 'Libraries')) {
        $source = Join-Path $publishPath $directory
        if (!(Test-Path -LiteralPath $source) -or !(Get-ChildItem -LiteralPath $source -Force | Select-Object -First 1)) {
            $source = Join-Path $projectRoot $directory
        }
        if (!(Test-Path -LiteralPath $source)) { $source = Join-Path $installedPath $directory }
        if (Test-Path -LiteralPath $source) {
            Write-Host "Preserving $directory from $source"
            Copy-Item -LiteralPath $source -Destination (Join-Path $stagingPath $directory) -Recurse -Force
        }
    }

    foreach ($required in @('ETS2LA.exe', 'ETS2LA.dll', 'ETS2LA.UI.dll', 'coreclr.dll', 'libdeflate.dll', 'Photino.Native.dll', 'WebView2Loader.dll', 'wwwroot\index.html', 'wwwroot\css\app.css', 'wwwroot\Visualization\index.html', 'Assets\SDKs', 'Assets\Perception\grounding-dino.onnx', 'Assets\Perception\vocab.txt', 'Assets\Perception\tessdata\eng.traineddata', 'Assets\Perception\tessdata\fra.traineddata')) {
        if (!(Test-Path -LiteralPath (Join-Path $stagingPath $required))) { throw "Published build is missing $required." }
    }
    # Mark this as a local build instead of pretending it is an installed release.
    Set-Content -LiteralPath (Join-Path $stagingPath 'Assets\DistributionSource.txt') -Value 'Local' -NoNewline
    Remove-BuildDirectory $previousPath
    if (Test-Path -LiteralPath $publishPath) { Move-Item -LiteralPath $publishPath -Destination $previousPath }
    try { Move-Item -LiteralPath $stagingPath -Destination $publishPath }
    catch {
        if ((Test-Path -LiteralPath $previousPath) -and !(Test-Path -LiteralPath $publishPath)) {
            Move-Item -LiteralPath $previousPath -Destination $publishPath
        }
        throw
    }
    Write-Host "Build ready: $publishPath\ETS2LA.exe"
    exit 0
} catch {
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    if ($buildLock) { $buildLock.Dispose() }
    Remove-BuildDirectory $stagingPath
}
