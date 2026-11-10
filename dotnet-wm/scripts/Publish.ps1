[CmdletBinding()]
param(
    [ValidateSet('Release', 'NativeAot')]
    [string]$Mode = 'Release',
    [ValidateSet('All', 'Host', 'Cli', 'DebugUi', 'SpatialSwitcher')]
    [string]$App = 'All',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputRoot = 'artifacts',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRootPath = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}

if ($Mode -eq 'NativeAot' -and $SelfContained) {
    throw 'NativeAot is already self-contained; omit -SelfContained.'
}
if ($Mode -eq 'NativeAot' -and $App -eq 'Cli') {
    throw 'NativeAot publishing is not supported for the CLI.'
}
if ($Mode -eq 'NativeAot' -and $RuntimeIdentifier -ne 'win-x64') {
    throw 'NativeAot publishing is currently supported only for win-x64.'
}

$selectedApps = if ($App -eq 'All' -and $Mode -eq 'NativeAot') {
    @('Host', 'DebugUi', 'SpatialSwitcher')
} elseif ($App -eq 'All') {
    @('Host', 'Cli', 'DebugUi', 'SpatialSwitcher')
} else {
    @($App)
}

$deployment = if ($Mode -eq 'NativeAot') {
    'native-aot'
} elseif ($SelfContained) {
    'self-contained'
} else {
    'framework-dependent'
}

function Invoke-Publish([string]$Project, [string]$Destination, [bool]$Aot) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $publishAotValue = if ($Aot) { 'true' } else { 'false' }
    $arguments = @(
        'publish', $Project,
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', ([string]($SelfContained -or $Aot)).ToLowerInvariant(),
        '--output', $Destination,
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        "-p:NuGetLockFilePath=obj\publish-$deployment-$RuntimeIdentifier.lock.json",
        "-p:PublishAot=$publishAotValue"
    )
    if ($SelfContained -or $Aot) { $arguments += '-p:RuntimeFrameworkVersion=10.0.14' }
    if ($Aot -and $Project -notlike '*WinManager.Host.csproj') {
        $arguments += '-p:AvaloniaSingleFileAot=true'
    }

    Write-Host "Publishing $Project ($deployment, $RuntimeIdentifier) to $Destination"
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project with exit code $LASTEXITCODE."
    }
}

foreach ($selectedApp in $selectedApps) {
    $appName = switch ($selectedApp) {
        'Host' { 'host' }
        'Cli' { 'cli' }
        'DebugUi' { 'debug-ui' }
        'SpatialSwitcher' { 'spatial-switcher' }
    }
    $destination = Join-Path $outputRootPath (Join-Path $RuntimeIdentifier (Join-Path $deployment $appName))
    $project = Join-Path $repositoryRoot "src\WinManager.$selectedApp\WinManager.$selectedApp.csproj"
    Invoke-Publish $project $destination ($Mode -eq 'NativeAot')

    if ($Mode -eq 'NativeAot') {
        $executableName = [IO.Path]::GetFileNameWithoutExtension($project) + '.exe'
        $executablePath = Join-Path $destination $executableName
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "Single-file publish did not produce the expected executable: $executablePath"
        }

        $unexpectedFiles = @(Get-ChildItem -LiteralPath $destination -File -Recurse |
            Where-Object { $_.FullName -ne $executablePath })
        if ($unexpectedFiles.Count -gt 0) {
            $unexpectedPaths = $unexpectedFiles | ForEach-Object { $_.FullName }
            throw "Single-file verification failed; unexpected files remain in '$destination': $($unexpectedPaths -join ', '). Move or remove stale files from this output directory, then publish again."
        }
        Write-Host "Verified single-file output: $executablePath"
    }

    if ($selectedApp -eq 'Host' -and $Mode -eq 'Release') {
        $recoveryProject = Join-Path $repositoryRoot 'src\WinManager.Recovery\WinManager.Recovery.csproj'
        Invoke-Publish $recoveryProject $destination $false
    }
}
