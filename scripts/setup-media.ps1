param([string]$RuntimeIdentifier)
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
if (-not $IsWindows) {
    & python3 (Join-Path $PSScriptRoot 'build-native.py') --rid $RuntimeIdentifier --media-only
    if ($LASTEXITCODE) { throw 'Unix media build failed.' }
    return
}
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'media-runtimes.json') -Raw | ConvertFrom-Json
$definition = if ($manifest -is [System.Collections.IDictionary]) { $manifest[$RuntimeIdentifier] } else { $manifest.$RuntimeIdentifier }
if (-not $definition) { throw "No verified media runtime for $RuntimeIdentifier." }
$output = Join-Path $projectRoot "Native/media/$RuntimeIdentifier"
if (Test-Path -LiteralPath (Join-Path $output $definition.executable)) { return }
$cache = Join-Path $projectRoot "Native/sdk/media-$RuntimeIdentifier"
New-Item -ItemType Directory -Path $cache,$output -Force | Out-Null
$archive = Join-Path $cache "runtime.$($definition.archive)"
if ($RuntimeIdentifier -eq 'win-x64') {
    $previous = Join-Path $projectRoot 'Native/sdk/ffmpeg/ffmpeg-9.0.2-essentials.zip'
    if (Test-Path -LiteralPath $previous) { Copy-Item -LiteralPath $previous -Destination $archive }
}
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest $definition.url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $definition.sha256) {
    throw 'Media archive checksum mismatch. Review the pinned release; never run unverified downloads.'
}
if ($definition.archive -eq 'zip') { Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force }
else { & tar -xf $archive -C $cache; if ($LASTEXITCODE) { throw 'Media extraction failed.' } }
$binary = Get-ChildItem -LiteralPath $cache -Recurse -File | Where-Object Name -EQ $definition.executable | Select-Object -First 1
if (-not $binary) { throw 'FFmpeg was not found in the verified archive.' }
Copy-Item -LiteralPath $binary.FullName -Destination $output
Get-ChildItem -LiteralPath $cache -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|README|NOTICE)' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output $_.Name) -Force
}
$definition | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'runtime-source.json')
if (-not $IsWindows) { & chmod +x (Join-Path $output $definition.executable); if ($LASTEXITCODE) { throw 'Could not set media executable permission.' } }
Write-Host "Verified portable FFmpeg prepared for $RuntimeIdentifier. No system installation or PATH changes."
