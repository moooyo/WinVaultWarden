[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^winvaultwarden-v3-[a-f0-9]{32}$')]
    [string]$PipeName
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$ciphertextPath = Join-Path $repositoryRoot '.secrets\ui-review\review.password.dpapi'
$pipeOptions = [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly
$pipe = [IO.Pipes.NamedPipeServerStream]::new(
    $PipeName, [IO.Pipes.PipeDirection]::Out, 1,
    [IO.Pipes.PipeTransmissionMode]::Byte, $pipeOptions)
$timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))
$plaintext = $null
try {
    $pipe.WaitForConnectionAsync($timeout.Token).GetAwaiter().GetResult()
    $ciphertext = [IO.File]::ReadAllBytes($ciphertextPath)
    $plaintext = [Security.Cryptography.ProtectedData]::Unprotect(
        $ciphertext, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $pipe.Write($plaintext, 0, $plaintext.Length)
    $pipe.Flush()
}
finally {
    if ($null -ne $plaintext) {
        [Array]::Clear($plaintext, 0, $plaintext.Length)
    }
    $timeout.Dispose()
    $pipe.Dispose()
}
