param(
    [switch]$SkipPublish,
    [switch]$SkipSign,
    [string]$PfxPath = $env:DSH_SIGN_PFX,
    [string]$PfxPassword = $env:DSH_SIGN_PFX_PASSWORD,
    [string]$Thumbprint = $env:DSH_SIGN_THUMBPRINT,
    [string]$Repo = "hexin1225/deepseek-translator",
    [string]$Notes = "",
    [switch]$Mandatory,
    [switch]$PublishToGitee
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $root "src\DeepSeekTranslator\DeepSeekTranslator.csproj"
$iss = Join-Path $root "installer\DeepSeekTranslator.iss"
$icon = Join-Path $root "src\DeepSeekTranslator\Assets\app.ico"

if (-not (Test-Path $iss)) { throw "Installer script not found: $iss" }

# 版本号以 csproj 为准
[xml]$projectXml = Get-Content $project
$version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { throw "Cannot read <Version> from $project" }

$source = Join-Path $root "releases\v$version\win-x64"
$output = Join-Path $root "releases\v$version"
$setup = Join-Path $output "DeepSeekTranslator-$version-setup.exe"

Write-Host "== Build installer v$version =="

if (-not $SkipPublish) {
    Write-Host "-- publish --"
    & (Join-Path $root "scripts\build.ps1") -Configuration Release -Publish
    # build.ps1 里的 robocopy 会把 $LASTEXITCODE 留成 1，这里不用它判断，直接看产物
    if (-not (Test-Path (Join-Path $source "DeepSeekTranslator.exe"))) {
        throw "Publish failed: missing $source\DeepSeekTranslator.exe"
    }
}

$appExe = Join-Path $source "DeepSeekTranslator.exe"
$appDll = Join-Path $source "DeepSeekTranslator.dll"
if (-not (Test-Path $appExe)) { throw "Publish output not found: $appExe" }

$signArgs = @{}
if ($PfxPath) { $signArgs["PfxPath"] = $PfxPath }
if ($PfxPassword) { $signArgs["PfxPassword"] = $PfxPassword }
if ($Thumbprint) { $signArgs["Thumbprint"] = $Thumbprint }

if (-not $SkipSign) {
    Write-Host "-- sign application binaries --"
    & (Join-Path $root "scripts\sign.ps1") -Path $appExe, $appDll @signArgs
}

# 定位 Inno Setup 命令行编译器
$isccCandidates = @(
    $env:ISCC_PATH,
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6: winget install JRSoftware.InnoSetup"
}

Write-Host "-- compile installer --"
& $iscc `
    "/DAppVersion=$version" `
    "/DSourceDir=$source" `
    "/DOutputDir=$output" `
    "/DIconFile=$icon" `
    "/Qp" `
    $iss

if ($LASTEXITCODE -ne 0) { throw "ISCC failed: $LASTEXITCODE" }
if (-not (Test-Path $setup)) { throw "Setup was not created: $setup" }

if (-not $SkipSign) {
    Write-Host "-- sign installer --"
    & (Join-Path $root "scripts\sign.ps1") -Path $setup @signArgs
}

$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "Setup: $setup ($sizeMb MB)"
$signature = Get-AuthenticodeSignature $setup
Write-Host "Signature: $($signature.Status)"

# ---- 生成自动更新清单 latest.json ----
$setupName = Split-Path $setup -Leaf
$assetUrl = "https://gitee.com/$Repo/releases/download/v$version/$setupName"
$sha256 = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()

$feed = [ordered]@{
    version   = $version
    url       = $assetUrl
    sha256    = $sha256
    size      = (Get-Item $setup).Length
    mandatory = $Mandatory.IsPresent
    notes     = $Notes
    published = (Get-Date).ToString("o")
}

$feedPath = Join-Path $output "latest.json"
$json = $feed | ConvertTo-Json
# 不带 BOM，避免个别 JSON 解析器把 BOM 当内容
[System.IO.File]::WriteAllText($feedPath, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "Update feed: $feedPath"
Write-Host "  version: $version"
Write-Host "  sha256 : $sha256"
Write-Host ""
Write-Host "把这两个文件传到 Gitee（https://gitee.com/$Repo）："
Write-Host "  1) $setupName  -> Releases，标签 v$version"
Write-Host "  2) latest.json -> 仓库根目录（master 分支）"
Write-Host ""
Write-Host "客户端读取：https://gitee.com/$Repo/raw/master/latest.json"

if ($PublishToGitee) {
    Write-Host ""
    & (Join-Path $root "scripts\gitee-publish.ps1") -Repo $Repo -Version $version -Notes $Notes
}
