param(
    [string]$Repo = "hexin1225/deepseek-translator",
    [string]$Version,
    [string]$Token,
    [string]$Notes,
    [switch]$SkipAssetUpload
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function Get-GiteeToken {
    if ($Token) { return $Token }
    if ($env:GITEE_TOKEN) { return $env:GITEE_TOKEN }

    # setx 只影响新进程；这里直接读用户级环境变量（注册表），当前会话也能拿到
    $user = [Environment]::GetEnvironmentVariable("GITEE_TOKEN", "User")
    if ($user) { return $user }

    throw "找不到 GITEE_TOKEN：请先执行 setx GITEE_TOKEN ""<私人令牌>"""
}

function Get-AppVersion {
    if ($Version) { return $Version }

    $csproj = Join-Path $root "src\DeepSeekTranslator\DeepSeekTranslator.csproj"
    [xml]$xml = Get-Content $csproj
    $value = [string]($xml.Project.PropertyGroup.Version | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($value)) { throw "无法从 csproj 读取版本号" }
    return $value
}

function Get-ErrorText($errorRecord) {
    $response = $errorRecord.Exception.Response
    if ($response) {
        try {
            $reader = New-Object IO.StreamReader($response.GetResponseStream())
            return $reader.ReadToEnd()
        }
        catch { }
    }
    return $errorRecord.Exception.Message
}

function Test-NotFound($errorRecord) {
    $response = $errorRecord.Exception.Response
    if ($null -eq $response) { return $false }
    try { return [int]$response.StatusCode -eq 404 } catch { return $false }
}

function Get-ReleaseByTag {
    param([string]$Tag)

    try {
        $result = Invoke-RestMethod -Uri "$api/releases/tags/${Tag}?access_token=$giteeToken" `
            -Method Get -TimeoutSec 30
        # 发行版不存在时，Gitee 会返回字符串 "null" 或空数组，这里统一当作“不存在”
        if ($null -eq $result -or $result -eq "null" -or -not $result.id) { return $null }
        return $result
    }
    catch {
        if (Test-NotFound $_) { return $null }
        throw "查询发行版失败：$(Get-ErrorText $_)"
    }
}

function New-Release {
    param([string]$Tag, [string]$Body)

    # 用 JSON 提交，避免表单编码把中文弄乱
    $payload = @{
        access_token     = $giteeToken
        tag_name         = $Tag
        name             = $Tag
        body             = $Body
        target_commitish = "master"
        prerelease       = $false
    } | ConvertTo-Json -Depth 4

    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    return Invoke-RestMethod -Uri "$api/releases" -Method Post -Body $bytes `
        -ContentType "application/json; charset=utf-8" -TimeoutSec 60
}

function Send-Asset {
    param([int]$ReleaseId, [string]$File)

    $curl = Join-Path $env:SystemRoot "System32\curl.exe"
    $responseFile = Join-Path $env:TEMP "gitee-upload-$appVersion.json"

    $code = & $curl -s -S --max-time 900 -X POST "$api/releases/$ReleaseId/attach_files" `
        -F "access_token=$giteeToken" -F "file=@$File" -o $responseFile -w "%{http_code}"

    $body = if (Test-Path $responseFile) { Get-Content $responseFile -Raw } else { "" }
    return [pscustomobject]@{ Code = [string]$code; Body = $body }
}

function Update-ReleaseBody {
    param([int]$ReleaseId, [string]$Tag, [string]$Body)

    # Gitee 的 PATCH 要求同时带 tag_name / name；表单用 UTF-8 百分号编码，中文才不乱
    Add-Type -AssemblyName System.Web
    $payload = "access_token=" + [System.Web.HttpUtility]::UrlEncode($giteeToken) +
               "&tag_name=" + [System.Web.HttpUtility]::UrlEncode($Tag) +
               "&name=" + [System.Web.HttpUtility]::UrlEncode($Tag) +
               "&body=" + [System.Web.HttpUtility]::UrlEncode($Body)

    $bytes = [Text.Encoding]::ASCII.GetBytes($payload)
    Invoke-RestMethod -Uri "$api/releases/$ReleaseId" -Method Patch -Body $bytes `
        -ContentType "application/x-www-form-urlencoded" -TimeoutSec 60 | Out-Null
}

$giteeToken = Get-GiteeToken
$appVersion = Get-AppVersion
$owner, $repoName = $Repo -split "/", 2
$api = "https://gitee.com/api/v5/repos/$owner/$repoName"
$tag = "v$appVersion"

$releaseDir = Join-Path $root "releases\$tag"
$setupPath = Join-Path $releaseDir "DeepSeekTranslator-$appVersion-setup.exe"
$feedPath = Join-Path $releaseDir "latest.json"

if (-not (Test-Path $setupPath)) { throw "找不到安装包：$setupPath（先运行 build-installer.ps1）" }
if (-not (Test-Path $feedPath)) { throw "找不到更新清单：$feedPath（先运行 build-installer.ps1）" }

$assetName = Split-Path $setupPath -Leaf
$releaseNotes = if ($Notes) { $Notes } else { "DeepSeek 划词翻译 $tag" }

Write-Host "== 发布 $tag 到 Gitee =="
Write-Host "   仓库: $Repo"
Write-Host "   安装包: $assetName（$([math]::Round((Get-Item $setupPath).Length / 1MB, 1)) MB）"

# ---- 1. 空仓库先建初始提交 ----
$initialized = $true
try {
    Invoke-RestMethod -Uri "$api/contents/?access_token=$giteeToken" -Method Get -TimeoutSec 30 | Out-Null
}
catch {
    if (Test-NotFound $_) { $initialized = $false }
    else { throw "读取仓库失败：$(Get-ErrorText $_)" }
}

if (-not $initialized) {
    Write-Host "-- 仓库为空，创建初始提交 --"
    $readme = "# DeepSeek 划词翻译`n`nWindows 托盘应用：选中文字后按快捷键或点击鼠标旁的「译」图标翻译。"
    $body = @{
        access_token = $giteeToken
        content      = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($readme))
        message      = "初始化仓库"
        branch       = "master"
    }
    Invoke-RestMethod -Uri "$api/contents/README.md" -Method Post -Body $body -ContentType "application/x-www-form-urlencoded" -TimeoutSec 60 | Out-Null
    Write-Host "   完成"
}

# ---- 2. 发行版：不存在则创建；已存在但已有同名附件则重建（保证只有一个附件） ----
$release = Get-ReleaseByTag -Tag $tag

if ($release -and -not $SkipAssetUpload) {
    $duplicate = $release.assets | Where-Object { $_.name -eq $assetName }
    if ($duplicate) {
        Write-Host "-- 发行版里已有同名附件，删除后重建（id=$($release.id)）--"
        Invoke-RestMethod -Uri "$api/releases/$($release.id)?access_token=$giteeToken" `
            -Method Delete -TimeoutSec 60 | Out-Null
        Start-Sleep -Seconds 2
        $release = $null
    }
}

if ($null -eq $release) {
    Write-Host "-- 创建发行版 $tag --"
    $release = New-Release -Tag $tag -Body $releaseNotes
    Write-Host "   id=$($release.id)"
}
else {
    Write-Host "-- 发行版 $tag 已存在（id=$($release.id)）"
}

if ($Notes) {
    Write-Host "-- 更新发行版说明 --"
    Update-ReleaseBody -ReleaseId $release.id -Tag $tag -Body $Notes
    Write-Host "   完成"
}

# ---- 3. 上传安装包 ----
if (-not $SkipAssetUpload) {
    Write-Host "-- 上传安装包 --"
    $result = Send-Asset -ReleaseId $release.id -File $setupPath

    if ($result.Code -ne "201") {
        Write-Host "   上传返回 HTTP $($result.Code)，尝试重建发行版"

        Invoke-RestMethod -Uri "$api/releases/$($release.id)?access_token=$giteeToken" `
            -Method Delete -TimeoutSec 60 | Out-Null
        Start-Sleep -Seconds 2

        $release = New-Release -Tag $tag -Body $releaseNotes
        Write-Host "   已重建（id=$($release.id)）"

        $result = Send-Asset -ReleaseId $release.id -File $setupPath
        if ($result.Code -ne "201") {
            throw "上传失败（HTTP $($result.Code)）：$($result.Body)"
        }
    }

    $uploaded = $result.Body | ConvertFrom-Json
    Write-Host "   成功：$($uploaded.name)（$([math]::Round($uploaded.size / 1MB, 1)) MB）"
}

# ---- 4. 写入 / 更新 latest.json ----
Write-Host "-- 更新 latest.json --"
$content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($feedPath))

$existingSha = $null
try {
    $existing = Invoke-RestMethod -Uri "$api/contents/latest.json?access_token=$giteeToken" -Method Get -TimeoutSec 30
    $existingSha = $existing.sha
}
catch {
    if (-not (Test-NotFound $_)) { throw "读取 latest.json 失败：$(Get-ErrorText $_)" }
}

$body = @{
    access_token = $giteeToken
    content      = $content
    message      = "发布更新清单 $tag"
    branch       = "master"
}

if ($existingSha) {
    $body["sha"] = $existingSha
    Invoke-RestMethod -Uri "$api/contents/latest.json" -Method Put -Body $body `
        -ContentType "application/x-www-form-urlencoded" -TimeoutSec 60 | Out-Null
    Write-Host "   已更新"
}
else {
    Invoke-RestMethod -Uri "$api/contents/latest.json" -Method Post -Body $body `
        -ContentType "application/x-www-form-urlencoded" -TimeoutSec 60 | Out-Null
    Write-Host "   已创建"
}

# ---- 5. 验证 ----
Write-Host "-- 验证 --"
$stamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$remote = Invoke-RestMethod -Uri "https://gitee.com/$Repo/raw/master/latest.json?t=$stamp" -Method Get -TimeoutSec 30

if ($remote.version -ne $appVersion) {
    throw "清单版本不符：远程 $($remote.version)，期望 $appVersion"
}

Write-Host "   清单版本：$($remote.version)"
Write-Host "   SHA256：$($remote.sha256)"
Write-Host "   安装包地址：$($remote.url)"

$curl = Join-Path $env:SystemRoot "System32\curl.exe"
$assetUrl = "https://gitee.com/$Repo/releases/download/$tag/$assetName"
$headCode = & $curl -s -I -L --max-time 120 -o NUL -w "%{http_code}" $assetUrl
if ($headCode -eq "200") {
    Write-Host "   安装包直链：HTTP 200"
}
else {
    Write-Warning "安装包直链返回 HTTP $headCode"
}

Write-Host ""
Write-Host "完成：https://gitee.com/$Repo/releases/tag/$tag"
