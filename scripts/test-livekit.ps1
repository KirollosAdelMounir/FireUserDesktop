param([Parameter(Mandatory)][string]$ServerPath, [int]$Port = 17880, [string]$MediaAddress = '127.0.0.1', [switch]$ForceTcp)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$ServerPath = (Resolve-Path -LiteralPath $ServerPath).Path
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("fireuser-native-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$configurationPath = Join-Path $temporary 'livekit.yaml'
$testSecret = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
@"
port: $Port
bind_addresses: ["127.0.0.1"]
rtc:
  tcp_port: $($Port + 1)
  udp_port: $($Port + 2)
  node_ip: $MediaAddress
  use_external_ip: false
  enable_loopback_candidate: true
  force_tcp: $($ForceTcp.IsPresent.ToString().ToLowerInvariant())
keys:
  native-test: $testSecret
logging:
  level: info
"@ | Set-Content -LiteralPath $configurationPath
$start = @{ FilePath = $ServerPath; ArgumentList = @('--config', ('"' + $configurationPath + '"')); PassThru = $true;
    RedirectStandardOutput = (Join-Path $temporary 'stdout.log'); RedirectStandardError = (Join-Path $temporary 'stderr.log') }
if ($IsWindows) { $start.WindowStyle = 'Hidden' }
$server = Start-Process @start
try {
    Start-Sleep -Seconds 2
    if ($server.HasExited) { throw "The loopback LiveKit server could not start. Inspect $temporary" }
    $env:FIREUSER_LIVEKIT_TEST_URL = "ws://127.0.0.1:$Port"
    $env:FIREUSER_LIVEKIT_TEST_KEY = 'native-test'
    $env:FIREUSER_LIVEKIT_TEST_SECRET = $testSecret
    & dotnet test (Join-Path $projectRoot 'Tests/UserIdentityDesktop.Tests.csproj') -c Release
    $testExitCode = $LASTEXITCODE
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id }
    Remove-Item Env:FIREUSER_LIVEKIT_TEST_URL,Env:FIREUSER_LIVEKIT_TEST_KEY,Env:FIREUSER_LIVEKIT_TEST_SECRET -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $configurationPath -ErrorAction SilentlyContinue
}
if ($testExitCode) { throw "Native integration tests failed. Test-server diagnostics: $temporary" }
