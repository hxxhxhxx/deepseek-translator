[CmdletBinding()]
param(
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Repo = 'hxxhxhxx/deepseek-translator',
    [string]$Branch = 'main',
    [string]$Message = '',
    [switch]$DryRun,
    [switch]$SkipWorkflow
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'

if ([string]::IsNullOrWhiteSpace($Message)) {
    $Message = "chore: sync from Gitee $stamp"
}

# 只导出可公开的源码/脚本/文档/打包清单；不包含 releases、tools、缓存、临时文件。
$export = Join-Path $env:TEMP ("deepseek-github-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $export -Force | Out-Null

try {
    $dirs = @('src', 'tests', 'scripts', 'installer', 'docs', 'packaging')
    foreach ($dir in $dirs) {
        $source = Join-Path $root $dir
        if (Test-Path $source) {
            Copy-Item $source (Join-Path $export $dir) -Recurse -Force
        }
    }

    foreach ($file in @('.gitignore', 'DeepSeekTranslator.sln', 'global.json')) {
        $source = Join-Path $root $file
        if (Test-Path $source) {
            Copy-Item $source (Join-Path $export $file) -Force
        }
    }

    $workflowSource = Join-Path $root 'packaging/github/build.yml'
    if (-not $SkipWorkflow -and (Test-Path $workflowSource)) {
        $workflowTarget = Join-Path $export '.github/workflows'
        New-Item -ItemType Directory -Path $workflowTarget -Force | Out-Null
        Copy-Item $workflowSource (Join-Path $workflowTarget 'build.yml') -Force
    }

    Copy-Item (Join-Path $root 'packaging/public-README.md') (Join-Path $export 'README.md') -Force
    Copy-Item (Join-Path $root 'packaging/public/LICENSE') (Join-Path $export 'LICENSE') -Force

    # 去掉构建产物和不需要公开的文件
    Get-ChildItem $export -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('bin', 'obj') } |
        Remove-Item -Recurse -Force
    Remove-Item (Join-Path $export 'packaging/public-README.md') -Force -ErrorAction SilentlyContinue

    $files = Get-ChildItem $export -Recurse -File | Sort-Object FullName
    $totalMb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 2)
    Write-Host "导出 $($files.Count) 个文件，共 $totalMb MB"

    if ($DryRun) {
        $files | ForEach-Object { $_.FullName.Substring($export.Length + 1) }
        return
    }

    if ([string]::IsNullOrWhiteSpace($Token)) {
        throw '缺少 GitHub token：setx GITHUB_TOKEN "<token>" 后重开终端，或运行时传 -Token。token 需要 repo 权限（更新 .github/workflows 还需要 workflow 权限）。'
    }

    $headers = @{
        Authorization          = "Bearer $Token"
        'User-Agent'           = 'deepseek-translator-sync'
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }

    function Invoke-GitHub {
        param([string]$Method, [string]$Uri, $Body)
        if ($null -ne $Body) {
            $json = ConvertTo-Json -InputObject $Body -Depth 12 -Compress
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -Body $json -ContentType 'application/json'
        }
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
    }

    $api = "https://api.github.com/repos/$Repo"
    $ref = $null
    try {
        $ref = Invoke-GitHub -Method Get -Uri "$api/git/ref/heads/$Branch"
    }
    catch {
        Write-Host "分支 $Branch 不存在，将创建首个提交。"
    }

    $parents = @()
    if ($ref -and $ref.object -and $ref.object.sha) {
        $parents = @($ref.object.sha)
    }

    $entries = New-Object System.Collections.ArrayList
    $index = 0
    foreach ($file in $files) {
        $index++
        $relative = $file.FullName.Substring($export.Length + 1).Replace('\', '/')
        $content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.FullName))
        try {
            $blob = Invoke-GitHub -Method Post -Uri "$api/git/blobs" -Body @{ content = $content; encoding = 'base64' }
        }
        catch {
            throw "上传 $relative 失败：$($_.Exception.Message)"
        }
        [void]$entries.Add(@{ path = $relative; mode = '100644'; type = 'blob'; sha = $blob.sha })

        if ($index % 20 -eq 0 -or $index -eq $files.Count) {
            Write-Host "已上传 $index / $($files.Count)"
        }
    }

    $tree = Invoke-GitHub -Method Post -Uri "$api/git/trees" -Body @{ tree = @($entries) }
    $commit = Invoke-GitHub -Method Post -Uri "$api/git/commits" -Body @{
        message = $Message
        tree    = $tree.sha
        parents = $parents
    }

    if ($ref) {
        Invoke-GitHub -Method Patch -Uri "$api/git/refs/heads/$Branch" -Body @{
            sha   = $commit.sha
            force = $false
        } | Out-Null
    }
    else {
        Invoke-GitHub -Method Post -Uri "$api/git/refs" -Body @{
            ref = "refs/heads/$Branch"
            sha = $commit.sha
        } | Out-Null
    }

    Write-Host "完成：https://github.com/$Repo/commit/$($commit.sha)"
}
finally {
    if (Test-Path $export) {
        Remove-Item $export -Recurse -Force -ErrorAction SilentlyContinue
    }
}
