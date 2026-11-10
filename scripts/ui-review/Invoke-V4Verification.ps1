[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Build', 'Tests')]
    [string]$Action,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('App.Tests', 'Vault.Tests', 'Api.Tests', 'Crypto.Tests', 'BrowserNativeHost.Tests')]
    [string[]]$Suites = @(),
    [string]$Filter = '',
    [switch]$IncludeBrowserNativeHost
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactRoot = Join-Path $repositoryRoot 'artifacts\ui-review\v4'
$runId = [DateTime]::UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'")
$buildOutputRoot = Join-Path $artifactRoot "verification-build\$($Action.ToLowerInvariant())-$runId"

# Run only in a verification environment authorized for the current task.
# Build and test artifacts are independent of the historical v3 evidence.
# Examples:
#   -Action Build
#   -Action Tests -Filter TargetUI
#   -Action Tests -Suites App.Tests
#   -Action Tests

function Write-FailureTail {
    param([string]$LogPath)

    # Keep console diagnostics bounded and suppress common credential assignments.
    foreach ($line in Get-Content -LiteralPath $LogPath -Tail 35) {
        $redacted = $line -replace '(?i)(\b(?:masterPassword|password|access_token|refresh_token|privateKey|cookie)\b\s*[:=]\s*).+', '$1[REDACTED]'
        $redacted = $redacted -replace '(?i)(\bBearer\s+)\S+', '$1[REDACTED]'
        Write-Host $redacted
    }
}

function Invoke-LoggedDotnet {
    param([string]$LogName, [string[]]$Arguments)

    $logPath = Join-Path $artifactRoot "$LogName-$runId.log"
    & dotnet @Arguments *> $logPath
    $commandExitCode = $LASTEXITCODE
    if ($commandExitCode -ne 0) {
        Write-FailureTail -LogPath $logPath
        throw "dotnet command failed with exit code $commandExitCode. See $logPath."
    }

    return $logPath
}

function Write-TestSummary {
    param([string]$Suite, [string]$ResultPath, [string]$LogPath)

    if (-not (Test-Path -LiteralPath $ResultPath)) {
        throw "The successful test command did not create its TRX report. See $LogPath."
    }

    [xml]$report = Get-Content -LiteralPath $ResultPath -Raw
    $counters = $report.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if ($null -eq $counters -or [int]($counters.GetAttribute('total')) -eq 0) {
        throw "No tests were reported for $Suite. Check the filter and $LogPath."
    }

    Write-Host ("{0}: passed={1}, failed={2}, skipped={3}, total={4}. Log: {5}" -f
        $Suite, $counters.GetAttribute('passed'), $counters.GetAttribute('failed'),
        $counters.GetAttribute('notExecuted'), $counters.GetAttribute('total'), $LogPath)
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
Push-Location $repositoryRoot
try {
    switch ($Action) {
        'Build' {
            $hostValue = if ($IncludeBrowserNativeHost) { 'true' } else { 'false' }
            $logPath = Invoke-LoggedDotnet -LogName "build-$($Configuration.ToLowerInvariant())" -Arguments @(
                'build', 'src/App/App.csproj', '-c', $Configuration,
                '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$buildOutputRoot",
                '-p:Platform=x64', '-p:WindowsPackageType=None',
                '-p:WindowsAppSDKSelfContained=true', '-p:SelfContained=true',
                "-p:ShipBrowserNativeHost=$hostValue", '--nologo'
            )
            Write-Host "Build succeeded. Log: $logPath"
        }
        'Tests' {
            $selectedSuites = if ($Suites.Count -gt 0) {
                $Suites | Select-Object -Unique
            }
            elseif ([string]::IsNullOrWhiteSpace($Filter)) {
                @('App.Tests', 'Vault.Tests', 'Api.Tests', 'Crypto.Tests', 'BrowserNativeHost.Tests')
            }
            else {
                @('App.Tests')
            }

            $testFilter = $Filter
            $scope = if ([string]::IsNullOrWhiteSpace($Filter)) { 'all' } else { 'filtered' }
            if ($Filter -eq 'TargetUI') {
                if (@($selectedSuites | Where-Object { $_ -ne 'App.Tests' }).Count -gt 0) {
                    throw 'TargetUI is an App.Tests subset. Use an explicit VSTest filter for other suites.'
                }

                # Cover presentation changes and the security/lifecycle guards they rely on.
                $targetClasses = @(
                    'Xaml', 'Source', 'Sizing', 'ViewModel', 'VaultWorkspaceContextTests',
                    'GeneratorDialogFlowTests', 'SendEditorDialogLifecycleTests', 'SubmitAcceleratorTests',
                    'SensitiveActionVerificationServiceTests', 'SensitiveViewModelClearTests',
                    'ItemRepromptGateTests', 'VaultRepromptCoverageTests', 'UserVerificationServiceTests',
                    'DeviceApprovalDecisionServiceTests', 'SendSessionOwnershipTests',
                    'CancelWinsTaskTests', 'UiCoordinationPrimitivesTests', 'AppLifecycleCoordinatorTests'
                )
                $testFilter = ($targetClasses | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
                $scope = 'target-ui'
            }

            # Keep suites sequential because they share referenced projects and output directories.
            foreach ($testProject in $selectedSuites) {
                $resultDirectory = Join-Path $artifactRoot "$testProject\$scope-$runId"
                $arguments = @(
                    'test', "tests/$testProject/$testProject.csproj", '-c', $Configuration,
                    '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$buildOutputRoot",
                    '--nologo', '--logger', 'trx;LogFileName=results.trx',
                    '--results-directory', $resultDirectory
                )
                if (-not [string]::IsNullOrWhiteSpace($testFilter)) {
                    $arguments += @('--filter', $testFilter)
                }

                $logPath = Invoke-LoggedDotnet -LogName "$testProject-$scope" -Arguments $arguments
                Write-TestSummary -Suite $testProject -ResultPath (Join-Path $resultDirectory 'results.trx') -LogPath $logPath
            }
        }
    }
}
finally {
    Pop-Location
}
