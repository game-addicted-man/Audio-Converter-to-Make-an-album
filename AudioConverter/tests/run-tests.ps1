param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$testOutput = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$executable = Join-Path $testOutput 'IntegrationTests.exe'
$engineSource = Join-Path $ProjectRoot 'src\ConversionEngine.cs'
$testSource = Join-Path $PSScriptRoot 'IntegrationTests.cs'
& $compiler /nologo /langversion:5 /optimize+ /target:exe /platform:anycpu /r:System.Web.Extensions.dll "/out:$executable" $engineSource $testSource
if ($LASTEXITCODE -ne 0) { throw "Test compilation failed with exit code $LASTEXITCODE." }
& $executable $ProjectRoot
exit $LASTEXITCODE
