param(
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repositoryRoot 'AiMultiWindow.csproj'
$testProject = Join-Path $repositoryRoot 'tests\AiMultiWindow.LogicTests\AiMultiWindow.LogicTests.csproj'

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

Push-Location $repositoryRoot
try {
    if (-not $NoRestore) {
        Invoke-DotNet restore $appProject --nologo
        Invoke-DotNet restore $testProject --nologo
    }

    $restoreOption = if ($NoRestore) { @('--no-restore') } else { @() }
    Invoke-DotNet build $appProject -c Release --nologo @restoreOption
    Invoke-DotNet run --project $testProject -c Release @restoreOption
    Write-Host 'V1 verification completed successfully.' -ForegroundColor Green
}
finally {
    Pop-Location
}
