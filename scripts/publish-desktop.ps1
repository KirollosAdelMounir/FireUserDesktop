param([string]$RuntimeIdentifier, [string]$SdkPath, [ValidateSet('Auto','MinGW','MSVC')][string]$Toolchain = 'Auto')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue)) {
    $IsWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    $IsMacOS = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)
}
if (-not $RuntimeIdentifier) {
    $platform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
    $RuntimeIdentifier = "$platform-$([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant())"
}
& (Join-Path $PSScriptRoot 'build-native.ps1') -RuntimeIdentifier $RuntimeIdentifier -SdkPath $SdkPath -Toolchain $Toolchain
$outputPath = Join-Path $projectRoot "artifacts/$RuntimeIdentifier"
& dotnet publish (Join-Path $projectRoot 'UserIdentityDesktop.csproj') -c Release -r $RuntimeIdentifier --self-contained true -o $outputPath "-p:NativeToolchain=$Toolchain" "-p:LiveKitSdkPath=$SdkPath"
if ($LASTEXITCODE) { throw 'Desktop publish failed.' }
if ($RuntimeIdentifier.StartsWith('osx-')) {
    & python3 (Join-Path $PSScriptRoot 'package-macos.py') --source $outputPath
    if ($LASTEXITCODE) { throw 'macOS bundle packaging failed.' }
    Write-Host 'Created an unsigned .app bundle. Signing/notarization and permission validation require macOS.'
}
Write-Host "Published files: $outputPath"
