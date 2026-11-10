[CmdletBinding()]
param(
    [ValidateSet('Build', 'Run', 'BuildAndRun')]
    [string]$Action = 'BuildAndRun',
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]*$')]
    [string]$RoundName = 'standalone',
    [switch]$EssentialOnly,
    [switch]$PopupProbeOnly,
    [string]$ExistingBuildDirectory = '',
    [ValidateRange(1, 30)]
    [int]$TimeoutMinutes = 15
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runId = [DateTime]::UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'")
$evidenceRoot = Join-Path $repositoryRoot 'artifacts\ui-review\v4\native-selftest'
$runDirectory = Join-Path $evidenceRoot "$RoundName-$runId"
$buildDirectory = if ([string]::IsNullOrWhiteSpace($ExistingBuildDirectory)) {
    Join-Path $runDirectory 'build'
}
else {
    [IO.Path]::GetFullPath($ExistingBuildDirectory)
}

# Run this self-driven fixture only in a verification environment authorized for the current task.
# It compiles actual production WinUI XAML and never loads the production startup service provider.
# No UI Automation, input injection, user account, persistent preferences or system clipboard is used.
# All captures and build products remain inside an isolated artifacts directory.

function Get-SourceFingerprint {
    $sources = foreach ($sourceDirectory in @('src', 'tools\NativeUiReview')) {
        foreach ($sourceFile in [IO.Directory]::EnumerateFiles((Join-Path $repositoryRoot $sourceDirectory), '*', [IO.SearchOption]::AllDirectories)) {
            $extension = [IO.Path]::GetExtension($sourceFile)
            if ($extension -notin @('.cs', '.xaml', '.csproj', '.targets', '.props', '.manifest')) { continue }
            if ($sourceFile -match '[\\/](bin|obj)[\\/]') { continue }
            [pscustomobject]@{
                path = [IO.Path]::GetRelativePath($repositoryRoot, $sourceFile).Replace('\', '/')
                sha256 = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }
    return @($sources | Sort-Object -Property path)
}

New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$before = Get-SourceFingerprint
$beforeJson = ConvertTo-Json -InputObject $before -Depth 4 -Compress
Set-Content -LiteralPath (Join-Path $runDirectory 'source-fingerprint-before.json') -Value $beforeJson -Encoding utf8

if ($Action -ne 'Run') {
    $buildLog = Join-Path $runDirectory 'build.log'
    $arguments = @(
        'build', (Join-Path $repositoryRoot 'src\App\App.csproj'), '-c', 'Debug',
        '-p:Platform=x64', '-p:WindowsPackageType=None',
        '-p:WindowsAppSDKSelfContained=true', '-p:SelfContained=true',
        '-p:ShipBrowserNativeHost=false', '-p:NativeUiReview=true',
        '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$buildDirectory", '--nologo'
    )
    & dotnet @arguments *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $buildLog -Tail 35 | Write-Host
        throw "The opted-in native review build failed. See $buildLog."
    }
    $afterJson = ConvertTo-Json -InputObject (Get-SourceFingerprint) -Depth 4 -Compress
    Set-Content -LiteralPath (Join-Path $runDirectory 'source-fingerprint-after.json') -Value $afterJson -Encoding utf8
    if ($beforeJson -ne $afterJson) {
        throw "The source changed during compilation; rebuild from a stable snapshot before using this evidence. See $runDirectory."
    }
    Set-Content -LiteralPath (Join-Path $buildDirectory 'source-fingerprint.json') -Value $afterJson -Encoding utf8
    Write-Host "Isolated native review build succeeded. Build directory: $buildDirectory"
}

if ($Action -eq 'Build') {
    Write-Host "Evidence directory: $runDirectory"
    return
}

$candidateRoot = Join-Path $buildDirectory 'bin\App'
$executables = @(Get-ChildItem -LiteralPath $candidateRoot -Filter 'App.exe' -File -Recurse)
if ($executables.Count -ne 1) {
    throw "Expected one opted-in App.exe in the isolated build directory. Found $($executables.Count)."
}
$executable = $executables[0].FullName
if (-not (Test-Path -LiteralPath (Join-Path $executables[0].Directory.FullName 'native-ui-review.marker'))) {
    throw 'The executable is missing the explicitly opted-in native review marker.'
}
$buildFingerprintPath = Join-Path $buildDirectory 'source-fingerprint.json'
if (-not (Test-Path -LiteralPath $buildFingerprintPath) -or (Get-Content -LiteralPath $buildFingerprintPath -Raw).Trim() -ne $beforeJson) {
    throw 'The isolated build does not match the current source fingerprint. Rebuild before running a clean review.'
}
$runtimeArguments = @('--output', ('"{0}"' -f $runDirectory), '--source-root', ('"{0}"' -f $repositoryRoot), '--round', $RoundName)
if ($EssentialOnly) { $runtimeArguments += '--essential' }
if ($PopupProbeOnly) { $runtimeArguments += '--popup-probe' }
$process = Start-Process -FilePath $executable -ArgumentList $runtimeArguments -WorkingDirectory $executables[0].Directory.FullName -WindowStyle Hidden -PassThru
$heldProcessHandle = $process.Handle
$processStartedUtc = $process.StartTime.ToUniversalTime().ToString('O')
[pscustomobject]@{
    processId = $process.Id
    executable = $executable
    processStartedUtc = $processStartedUtc
    handleHeldBeforeWait = $true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'process-start.json') -Encoding utf8
$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
$lastReportedCount = -1
while (-not $process.WaitForExit(1000)) {
    if ([DateTime]::UtcNow -gt $deadline) {
        Stop-Process -Id $process.Id
        throw "The self-driven native review exceeded its bounded timeout. See $runDirectory."
    }
    $progressPath = Join-Path $runDirectory 'progress.txt'
    if (Test-Path -LiteralPath $progressPath) {
        $progress = ([string](Get-Content -LiteralPath $progressPath -Raw -ErrorAction SilentlyContinue)).Trim()
        if ([string]::IsNullOrWhiteSpace($progress)) { continue }
        $count = 0
        if ([int]::TryParse(($progress -split '\s+')[0], [ref]$count) -and $count -ge $lastReportedCount + 25) {
            Write-Host "Native capture progress: $progress"
            $lastReportedCount = $count
        }
    }
}
$process.Refresh()
[pscustomobject]@{
    processId = $process.Id
    executable = $executable
    processStartedUtc = $processStartedUtc
    handleHeldBeforeWait = $true
    nativeExitCode = $process.ExitCode
    observedExitUtc = [DateTime]::UtcNow.ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'process-result.json') -Encoding utf8
if ($process.ExitCode -ne 0) {
    throw "The self-driven native review failed with exit code $($process.ExitCode). See $runDirectory."
}
$manifestPath = Join-Path $runDirectory 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "The fixture exited without a completed native manifest. See $runDirectory."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (-not $manifest.Completed -or -not $manifest.Passed -or $manifest.Captures.Count -eq 0) {
    throw "The native review did not complete successfully. See $manifestPath."
}
$afterRunJson = ConvertTo-Json -InputObject (Get-SourceFingerprint) -Depth 4 -Compress
Set-Content -LiteralPath (Join-Path $runDirectory 'source-fingerprint-after-run.json') -Value $afterRunJson -Encoding utf8
if ($beforeJson -ne $afterRunJson) {
    throw "The source changed during the native review; the evidence is not a clean review round. See $runDirectory."
}
$popupCount = @($manifest.Captures | ForEach-Object { $_.PopupLayers }).Count
Write-Host "Native review passed. Captures: $($manifest.Captures.Count); popup layers: $popupCount; assertions: $($manifest.Assertions.Count)."
Write-Host "Manifest: $manifestPath"
