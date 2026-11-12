param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Publish
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$localSdk = Join-Path $root "tools\dotnet8\dotnet.exe"
$dotnet = if (Test-Path $localSdk) { $localSdk } else { "dotnet" }

$project = Join-Path $root "src\DeepSeekTranslator\DeepSeekTranslator.csproj"
[xml]$projectXml = Get-Content $project
$version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { $version = "0.0.0" }

if ($Publish) {
    $output = Join-Path $root "releases\v$version\win-x64"
    if (Test-Path $output) { Remove-Item -Recurse -Force $output }

    # 某些机器/安全软件会拒绝 dotnet publish 直接在 releases 下建目录（PowerShell 写同一目录却正常），
    # 所以先发布到工作区临时目录，再用 PowerShell 复制到 releases。
    $staging = Join-Path $root ".publish-temp"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }

    & $dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -o $staging

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed: $LASTEXITCODE"
    }

    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Copy-Item (Join-Path $staging "*") $output -Recurse -Force
    Remove-Item -Recurse -Force $staging

    Copy-Item (Join-Path $root "README.md") (Join-Path $output "README.md") -Force
    $docsSource = Join-Path $root "docs"
    $docsTarget = Join-Path $output "docs"
    if (Test-Path $docsSource) {
        Copy-Item $docsSource $docsTarget -Recurse -Force
    }

    $zip = Join-Path (Split-Path $output -Parent) "DeepSeekTranslator-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -CompressionLevel Optimal

    $sourceStage = Join-Path (Split-Path $output -Parent) "_source-stage"
    if (Test-Path $sourceStage) { Remove-Item -Recurse -Force $sourceStage }
    robocopy $root $sourceStage /E /XD releases tools bin obj .vs .publish-temp .build-temp github-export .acl-repair secrets .git _source-stage _modlens /XF *.user *.pfx *.p12 secret.dat .publish-*.py .publish-state-*.json notes-*.txt settings-*.png | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy source packaging failed: $LASTEXITCODE" }

    $sourceZip = Join-Path (Split-Path $output -Parent) "DeepSeekTranslator-$version-source.zip"
    if (Test-Path $sourceZip) { Remove-Item $sourceZip -Force }
    Compress-Archive -Path (Join-Path $sourceStage "*") -DestinationPath $sourceZip -CompressionLevel Optimal
    Remove-Item -Recurse -Force $sourceStage

    Write-Host "Published to: $output"
    Write-Host "Zip: $zip"
    Write-Host "Source zip: $sourceZip"

    # 已经创建过的桌面/开始菜单快捷方式跟着最新版本走
    $shortcutScript = Join-Path $root "scripts\install-shortcuts.ps1"
    if (Test-Path $shortcutScript) {
        try {
            & $shortcutScript -RefreshOnly
        }
        catch {
            Write-Warning "更新快捷方式失败：$($_.Exception.Message)"
        }
    }
}
else {
    & $dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed: $LASTEXITCODE"
    }
}

# robocopy 成功时返回 1；这里归零，避免调用方把 1 误判成失败
$global:LASTEXITCODE = 0
