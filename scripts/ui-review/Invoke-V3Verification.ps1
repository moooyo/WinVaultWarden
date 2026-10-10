[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Build', 'Tests', 'Fixture')]
    [string]$Action,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$IncludeBrowserNativeHost
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactRoot = Join-Path $repositoryRoot 'artifacts\ui-review\v3'
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

function Invoke-LoggedDotnet {
    param([string]$LogName, [string[]]$Arguments)
    $logPath = Join-Path $artifactRoot $LogName
    & dotnet @Arguments *> $logPath
    $commandExitCode = $LASTEXITCODE
    Get-Content -LiteralPath $logPath -Tail 35
    if ($commandExitCode -ne 0) {
        throw "dotnet command failed with exit code $commandExitCode. See $logPath."
    }
}

Push-Location $repositoryRoot
try {
    switch ($Action) {
        'Build' {
            $hostValue = if ($IncludeBrowserNativeHost) { 'true' } else { 'false' }
            Invoke-LoggedDotnet -LogName "build-$($Configuration.ToLowerInvariant()).log" -Arguments @(
                'build', 'src/App/App.csproj', '-c', $Configuration,
                '-p:Platform=x64', '-p:WindowsPackageType=None',
                '-p:WindowsAppSDKSelfContained=true', '-p:SelfContained=true',
                "-p:ShipBrowserNativeHost=$hostValue", '--nologo'
            )
        }
        'Tests' {
            foreach ($testProject in @('App.Tests', 'Vault.Tests')) {
                Invoke-LoggedDotnet -LogName "$testProject.log" -Arguments @(
                    'test', "tests/$testProject/$testProject.csproj", '-c', 'Debug',
                    '--nologo', '--logger', 'trx',
                    '--results-directory', (Join-Path $artifactRoot $testProject)
                )
            }
        }
        'Fixture' {
            $secretDirectory = Join-Path $repositoryRoot '.secrets\ui-review'
            if (-not (Test-Path -LiteralPath (Join-Path $secretDirectory 'review.password.dpapi'))) {
                throw 'The synthetic review account credential is missing; fixture verification will not create an account.'
            }
            if (-not (Get-NetTCPConnection -LocalPort 18083 -State Listen -ErrorAction SilentlyContinue)) {
                throw 'The isolated review SSH tunnel is not listening on 127.0.0.1:18083.'
            }
            Push-Location $artifactRoot
            try {
                Invoke-LoggedDotnet -LogName 'fixture-verification.log' -Arguments @(
                    'run', '--project', (Join-Path $repositoryRoot 'tools/UiReviewSeed'),
                    '--', '--verify-only', 'http://127.0.0.1:18083', $secretDirectory
                )
            }
            finally {
                Pop-Location
            }
        }
    }
}
finally {
    Pop-Location
}
