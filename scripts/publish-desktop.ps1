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
    $bundle = Join-Path $projectRoot "artifacts/$RuntimeIdentifier/Fire User Connect.app/Contents"
    $executablePath = Join-Path $bundle 'MacOS'
    New-Item -ItemType Directory -Path $executablePath -Force | Out-Null
    Get-ChildItem -LiteralPath $outputPath -File | Copy-Item -Destination $executablePath
    Get-ChildItem -LiteralPath $outputPath -Directory | Where-Object Name -NE 'Fire User Connect.app' | Copy-Item -Destination $executablePath -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging/macos/Info.plist') -Destination $bundle
    Write-Host 'Created an unsigned .app bundle. Signing/notarization and permission validation require macOS.'
}
Write-Host "Published files: $outputPath"
