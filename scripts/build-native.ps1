param(
    [ValidateSet('win-x64','linux-x64','linux-arm64','osx-x64','osx-arm64')]
    [string]$RuntimeIdentifier,
    [string]$SdkPath,
    [string]$CMake = 'cmake',
    [ValidateSet('Auto','MinGW','MSVC')]
    [string]$Toolchain = 'Auto'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue)) {
    $IsWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    $IsMacOS = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)
}
$platform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$hostRuntime = "$platform-$architecture"
if (-not $RuntimeIdentifier) { $RuntimeIdentifier = $hostRuntime }
if ($RuntimeIdentifier -ne $hostRuntime) { throw 'Build on the matching OS and architecture; this script does not cross-compile native libraries.' }
if (-not $IsWindows) {
    $unixArguments = @((Join-Path $PSScriptRoot 'build-native.py'), '--rid', $RuntimeIdentifier, '--cmake', $CMake)
    if ($SdkPath) { $unixArguments += @('--sdk', $SdkPath) }
    & python3 @unixArguments
    if ($LASTEXITCODE) { throw 'Unix native build failed.' }
    return
}

if (-not (Get-Command $CMake -ErrorAction SilentlyContinue) -and $IsWindows) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        $candidate = Join-Path $vsRoot 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
        if (Test-Path -LiteralPath $candidate) { $CMake = $candidate }
    }
}
if (-not (Get-Command $CMake -ErrorAction SilentlyContinue)) { throw 'Install CMake 3.24+ and a C++20 compiler matching the SDK (MinGW-w64 for a MinGW SDK, MSVC for the official Windows SDK).' }

# The official Windows archive exposes a C++ MSVC ABI. MinGW requires the
# complete SDK (and dependencies) built with the matching MinGW toolchain.
$useMinGW = $IsWindows -and ($Toolchain -eq 'MinGW' -or ($Toolchain -eq 'Auto' -and $SdkPath -and (Test-Path (Join-Path $SdkPath 'lib/liblivekit.dll.a'))))
if ($useMinGW -and (-not $SdkPath -or -not (Test-Path (Join-Path $SdkPath 'lib/liblivekit.dll.a')))) {
    throw 'MinGW requires -SdkPath pointing to a MinGW-built LiveKit SDK with lib/liblivekit.dll.a and matching dependencies. The official Windows MSVC archive is ABI-incompatible.'
}
$version = '1.12.2'
$archives = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-runtimes.json') -Raw | ConvertFrom-Json

if (-not $SdkPath) {
    $existingCache = Join-Path $projectRoot "Native/build/$RuntimeIdentifier/CMakeCache.txt"
    if (Test-Path -LiteralPath $existingCache) {
        $entry = Select-String -LiteralPath $existingCache -Pattern '^LiveKit_DIR:PATH=(.+)$' | Select-Object -First 1
        if ($entry) {
            $candidate = [IO.Path]::GetFullPath((Join-Path $entry.Matches[0].Groups[1].Value '../../..'))
            if (Test-Path -LiteralPath (Join-Path $candidate 'include/livekit/room.h')) { $SdkPath = $candidate }
        }
    }
}
if (-not $SdkPath) {
    $definition = $archives.$RuntimeIdentifier
    $baseName = "livekit-sdk-$($definition.name)-$version"
    $cache = Join-Path $projectRoot "Native/sdk/$RuntimeIdentifier"
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $archive = Join-Path $cache "$baseName.$($definition.archive)"
    if (-not (Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest "https://github.com/livekit/client-sdk-cpp/releases/download/v$version/$baseName.$($definition.archive)" -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $definition.sha256) { throw 'LiveKit SDK archive checksum mismatch.' }
    $SdkPath = Join-Path $cache $baseName
    if (-not (Test-Path -LiteralPath (Join-Path $SdkPath 'include/livekit/room.h'))) {
        if ($definition.archive -eq 'zip') { Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force }
        else { & tar -xzf $archive -C $cache; if ($LASTEXITCODE) { throw 'SDK extraction failed.' } }
    }
}
$SdkPath = (Resolve-Path -LiteralPath $SdkPath).Path
$audioHeader = Join-Path $projectRoot 'Native/sdk/miniaudio/miniaudio.h'
if (-not (Test-Path -LiteralPath $audioHeader)) {
    New-Item -ItemType Directory -Path (Split-Path $audioHeader) -Force | Out-Null
    Invoke-WebRequest 'https://raw.githubusercontent.com/mackron/miniaudio/0.11.23/miniaudio.h' -OutFile $audioHeader
}
if ((Get-FileHash -LiteralPath $audioHeader -Algorithm SHA256).Hash -ne '7E4F3F13C8FE66DF2080AC3DD12A89193E3C2463CB7F067C798ABD7331CD8EE6') { throw 'miniaudio checksum verification failed.' }
$buildSuffix = if ($useMinGW) { "${RuntimeIdentifier}-mingw" } else { $RuntimeIdentifier }
$buildPath = Join-Path $projectRoot "Native/build/$buildSuffix"
$outputPath = Join-Path $projectRoot "Native/artifacts/$RuntimeIdentifier"
$configureArguments = @('-S', (Join-Path $projectRoot 'Native'), '-B', $buildPath, "-DCMAKE_PREFIX_PATH=$SdkPath", '-DCMAKE_BUILD_TYPE=Release')
if ($useMinGW) {
    if (-not (Get-Command 'g++' -ErrorAction SilentlyContinue)) { throw 'Install MinGW-w64 C++20 and add its bin directory to PATH.' }
    $configureArguments += @('-G','MinGW Makefiles','-DCMAKE_CXX_COMPILER=g++')
} elseif ($IsWindows) { $configureArguments += @('-A','x64') }
& $CMake @configureArguments; if ($LASTEXITCODE) { throw 'CMake configuration failed.' }
& $CMake --build $buildPath --config Release --parallel; if ($LASTEXITCODE) { throw 'Native compilation failed.' }
& $CMake --install $buildPath --config Release --prefix $outputPath; if ($LASTEXITCODE) { throw 'Native installation failed.' }

if ($IsWindows) {
    Copy-Item -Path (Join-Path $SdkPath 'bin/*.dll') -Destination $outputPath
    if (-not $useMinGW) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $crt = Get-ChildItem -Path (Join-Path $vsRoot 'VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT') -Directory | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $crt) { throw 'The compiler redistribution files are missing. Cannot package a standalone native runtime.' }
    Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' -File | Copy-Item -Destination $outputPath
    Copy-Item -Path (Join-Path $outputPath '*.dll') -Destination (Join-Path $buildPath 'Release')
    } else {
        $compilerDirectory = Split-Path (Get-Command 'g++').Source -Parent
        foreach ($runtime in @('libstdc++-6.dll','libgcc_s_seh-1.dll','libwinpthread-1.dll')) {
            $runtimePath = Join-Path $compilerDirectory $runtime
            if (Test-Path $runtimePath) { Copy-Item $runtimePath $outputPath }
        }
        Copy-Item -Path (Join-Path $outputPath '*.dll') -Destination $buildPath
    }
} else {
    $extension = if ($IsMacOS) { '*.dylib' } else { '*.so*' }
    Copy-Item -Path (Join-Path $SdkPath "lib/$extension") -Destination $outputPath
    if ($IsMacOS) { $env:DYLD_LIBRARY_PATH = "$outputPath`:$env:DYLD_LIBRARY_PATH" }
    else { $env:LD_LIBRARY_PATH = "$outputPath`:$env:LD_LIBRARY_PATH" }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'Native/THIRD-PARTY-NOTICES.md') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $SdkPath 'share/livekit/build-info.json') -Destination $outputPath
$ctest = Join-Path (Split-Path (Get-Command $CMake).Source -Parent) $(if ($IsWindows) { 'ctest.exe' } else { 'ctest' })
& $ctest --test-dir $buildPath -C Release --output-on-failure; if ($LASTEXITCODE) { throw 'Native ABI smoke test failed.' }
Write-Host "Native LiveKit C++ artifacts: $outputPath"
