#requires -Version 7.0
# Builds or runs a Windows configuration: opencloud.ps1 build|run [config] [args...].
# build.bat and run.bat call it. Without a configuration it is windows-x64-release, and every argument goes to the game.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/common.ps1"

if ($args.Count -lt 1) { throw 'usage: opencloud.ps1 build|run [config] [args...]' }
$command = ([string]$args[0]).ToLower()
$config = 'windows-x64-release'
$rest = @($args | Select-Object -Skip 1)
if ($rest.Count -gt 0 -and ([string]$rest[0]).ToLower() -in 'windows', 'windows-x64', 'windows-x64-release') {
    $config = [string]$rest[0]
    $rest = @($rest | Select-Object -Skip 1)
}
if ($command -notin 'build', 'run') { throw "opencloud: unknown command $command" }

switch ($config.ToLower()) {
    { $_ -in 'windows', 'windows-x64-release' } { $name = 'windows-x64-release'; $buildType = 'Release' }
    'windows-x64' { $name = 'windows-x64'; $buildType = 'Debug' }
    default { throw "opencloud: unknown Windows configuration $config" }
}

$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$state = Join-Path $checkout '.opencloud'
$root = Resolve-WindowsRoot (Join-Path $state 'windows')
$build = Resolve-WindowsBuild $root (Join-Path $checkout "port/build/$name")
$timer = [Diagnostics.Stopwatch]::StartNew()
function Write-Stage([string]$Message) {
    Write-Host ("opencloud: [{0:N1}s] {1}" -f $timer.Elapsed.TotalSeconds, $Message)
}
Write-Stage "$command $name"

# One run per configuration at a time. The lock is released when the process exits, or before the game starts.
$locks = Join-Path $state 'locks'
New-Item -ItemType Directory -Force $locks | Out-Null
$lockFile = Join-Path $locks "$name.lock"
$lock = $null
$announced = $false
while ($null -eq $lock) {
    try {
        $lock = [IO.File]::Open($lockFile, 'OpenOrCreate', 'ReadWrite', 'None')
    } catch [IO.IOException], [UnauthorizedAccessException] {
        if (-not $announced) { Write-Host "opencloud: waiting for $lockFile"; $announced = $true }
        Start-Sleep -Milliseconds 500
    }
}
Write-Stage 'configuration lock acquired; checking dependencies'

if ($env:CLEAN -eq '1' -and (Test-Path -LiteralPath $build)) {
    Write-Stage "cleaning $build"
    Remove-Item -LiteralPath $build -Recurse -Force
}

# Both configurations share the dependencies, so their check and acquisition are serialised by a second lock.
$depsLock = Join-Path $locks 'windows-deps.lock'
$deps = $null
$depsAnnounced = $false
while ($null -eq $deps) {
    try {
        $deps = [IO.File]::Open($depsLock, 'OpenOrCreate', 'ReadWrite', 'None')
    } catch [IO.IOException], [UnauthorizedAccessException] {
        if (-not $depsAnnounced) { Write-Host "opencloud: waiting for $depsLock"; $depsAnnounced = $true }
        Start-Sleep -Milliseconds 500
    }
}

# acquire.ps1 verifies its archives. It runs only when one of the toolchain's files is missing.
$markers = @(
    'deps/llvm/llvm-mingw-20260922-ucrt-x86_64/bin/clang++.exe',
    'deps/ninja/ninja.exe',
    'deps/glslang/bin/glslang.exe',
    'deps/sdl/SDL3-3.4.18/x86_64-w64-mingw32/lib/cmake/SDL3',
    'deps/ffmpeg/ffmpeg-n9.0.2-27-g7e93014c5b-win64-lgpl-shared-9.0/bin/avcodec-63.dll',
    'deps/vulkan/Vulkan-Headers-vulkan-sdk-1.4.363.0/include',
    'deps/vulkan-runtime/VulkanRT-X64-1.4.363.0-Components/x64/vulkan-1.dll',
    'deps/rust/1.99.0-gnullvm/bin/cxxbridge.exe'
)
if (@($markers | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) }).Count -gt 0) {
    Write-Stage 'acquiring missing toolchain dependencies'
    & "$PSScriptRoot/acquire.ps1" -Root $root
    if ($LASTEXITCODE) { $deps.Dispose(); exit $LASTEXITCODE }
}
$deps.Dispose()
Write-Stage 'dependencies ready; checking CMake configuration'

# A stale cache is deleted before configuring: CMake would otherwise reuse paths that no longer exist.
function Read-CacheValue([string]$File, [string]$Key) {
    if (-not (Test-Path -LiteralPath $File)) { return $null }
    $match = Select-String -LiteralPath $File -Pattern "^$([regex]::Escape($Key)):[A-Z]+=(.*)$" | Select-Object -First 1
    if ($match) { return $match.Matches[0].Groups[1].Value.Trim() }
    return $null
}
$source = $checkout.Replace('\', '/')
$buildPath = $build.Replace('\', '/')
$ffmpegDir = "$($root.Replace('\', '/'))/deps/ffmpeg/ffmpeg-n9.0.2-27-g7e93014c5b-win64-lgpl-shared-9.0"
$cache = Join-Path $build 'CMakeCache.txt'
$cachedSource = Read-CacheValue $cache 'CMAKE_HOME_DIRECTORY'
$cachedBuild = Read-CacheValue $cache 'CMAKE_CACHEFILE_DIR'
$cachedFfmpeg = Read-CacheValue $cache 'OC_FFMPEG_DIR'
$stale = ($null -ne $cachedSource -and $cachedSource -ne $source) -or ($null -ne $cachedBuild -and $cachedBuild -ne $buildPath)
if ($stale) {
    foreach ($file in 'CMakeCache.txt', 'CMakeFiles', 'build.ninja', '.ninja_log', '.ninja_deps') {
        Remove-Item -LiteralPath (Join-Path $build $file) -Recurse -Force -ErrorAction SilentlyContinue
    }
}
# ninja re-runs CMake from the existing cache on its own, so a cache configured without OC_FFMPEG_DIR must be reconfigured here.
$reconfigure = $cachedFfmpeg -ne $ffmpegDir
if ($stale -or $reconfigure -or -not (Test-Path -LiteralPath (Join-Path $build 'build.ninja'))) {
    Write-Stage 'configuring CMake'
    & "$PSScriptRoot/configure.ps1" -Root $root -BuildDirectory $build -BuildType $buildType
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
} else {
    Write-Stage 'using cached CMake configuration'
}

# build.ps1 runs ninja, which builds only what changed.
$jobs = @{}
if ($env:JOBS) { $jobs = @{ Jobs = [int]$env:JOBS } }
& "$PSScriptRoot/build.ps1" -Root $root -BuildDirectory $build @jobs
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if ($command -eq 'build') {
    $lock.Dispose()
    Write-Stage "built $build"
    exit 0
}

# A fast source-metadata/version check skips extraction for current data. The explicit
# dc2data check command still performs a full output hash audit.
# The retail image by name, else any disc image in rom/pal, else the PS2 build's extracted disc.
$source = Join-Path $checkout 'rom/pal/Dark Chronicle (PAL).iso'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    $source = Get-ChildItem -LiteralPath (Join-Path $checkout 'rom/pal') -Filter '*.iso' -File -ErrorAction Ignore |
        Sort-Object Name | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $source) {
    $source = Join-Path $checkout 'rom/pal/extracted/iso'
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { $source = $null }
}

# dc2data takes <game-root>\data and publishes <game-root>\data.zip beside data.json.
$data = Join-Path $checkout 'port/run/data'
$dataArchive = Join-Path $checkout 'port/run/data.zip'
Write-Stage 'checking game data'
# Every port configuration extracts into the same port\run\data.zip, so they take one lock for it. The game holds it shared while it runs.
$dataLockFile = Join-Path $locks 'port-data.lock'
if (-not $source) {
    # Without a disc an existing data.zip is used; otherwise the game starts without its data.
    if (-not (Test-Path -LiteralPath $dataArchive -PathType Leaf)) {
        [Console]::Error.WriteLine('opencloud: no PAL disc found; the game starts without data and its file loads fail')
    }
} else {
    $dataLock = $null
    $dataAnnounced = $false
    while ($null -eq $dataLock) {
        try {
            $dataLock = [IO.File]::Open($dataLockFile, 'OpenOrCreate', 'ReadWrite', 'None')
        } catch [IO.IOException], [UnauthorizedAccessException] {
            if (-not $dataAnnounced) { Write-Host "opencloud: waiting for $dataLockFile"; $dataAnnounced = $true }
            Start-Sleep -Milliseconds 500
        }
    }
    $dc2data = Join-Path $build 'dc2data.exe'
    & $dc2data current $source $data
    if ($LASTEXITCODE) {
        Write-Stage 'extracting game data'
        & $dc2data extract $source $data
        if ($LASTEXITCODE) { $dataLock.Dispose(); exit $LASTEXITCODE }
    }
    $dataLock.Dispose()
}

$lock.Dispose()

# The game reads data.zip while it runs, so it holds the data lock shared; another configuration's extraction waits for it.
$dataShared = $null
$dataSharedAnnounced = $false
while ($null -eq $dataShared) {
    try {
        $dataShared = [IO.File]::Open($dataLockFile, 'OpenOrCreate', 'Read', 'Read')
    } catch [IO.IOException], [UnauthorizedAccessException] {
        if (-not $dataSharedAnnounced) { Write-Host "opencloud: waiting for $dataLockFile"; $dataSharedAnnounced = $true }
        Start-Sleep -Milliseconds 500
    }
}

if (-not $source -and (Test-Path -LiteralPath $dataArchive -PathType Leaf)) {
    & (Join-Path $build 'dc2data.exe') check $data
    if ($LASTEXITCODE) { $dataShared.Dispose(); exit $LASTEXITCODE }
}

Push-Location $checkout
# Script runs log at debug to the console (docs/LOGGING.md); a DC2_LOG of the user's own still wins.
if (-not $env:DC2_LOG) { $env:DC2_LOG = 'debug' }
Write-Stage 'starting opencloud'
& (Join-Path $build 'opencloud.exe') --run-dir (Join-Path $checkout 'port/run') @rest
$code = $LASTEXITCODE
Pop-Location
$dataShared.Dispose()
exit $code
