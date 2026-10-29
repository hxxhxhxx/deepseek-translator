$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$localSdk = Join-Path $root "tools\dotnet8\dotnet.exe"
$dotnet = if (Test-Path $localSdk) { $localSdk } else { "dotnet" }

Write-Host "== Build Release =="
& $dotnet build (Join-Path $root "src\DeepSeekTranslator\DeepSeekTranslator.csproj") -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== Core / DeepSeek client smoke tests =="
& $dotnet run --project (Join-Path $root "tests\CoreSmoke\CoreSmoke.csproj") -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== UIA / clipboard / hotkey smoke test =="
& $dotnet run --project (Join-Path $root "tests\CaptureSmoke\CaptureSmoke.csproj") -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== Windows OCR self-test =="
$exe = Join-Path $root "src\DeepSeekTranslator\bin\Release\net8.0-windows10.0.19041.0\win-x64\DeepSeekTranslator.exe"
if (Test-Path $exe) {
    & cmd.exe /c start /wait "" "$exe" --ocr-selftest
    Write-Host "OCR output: $env:LOCALAPPDATA\DeepSeekTranslator\ocr-selftest.txt"
}
