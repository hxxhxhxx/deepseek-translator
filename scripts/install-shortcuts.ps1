param(
    [switch]$StartMenu,
    [switch]$RefreshOnly
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# 找最新版本的发布版 exe（按 v1.2.3 这种版本号排序）
$releases = Get-ChildItem (Join-Path $root "releases") -Directory |
    Where-Object { $_.Name -match '^v\d+(\.\d+)*$' } |
    Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending

$exe = $null
foreach ($release in $releases) {
    $candidate = Join-Path $release.FullName "win-x64\DeepSeekTranslator.exe"
    if (Test-Path $candidate) {
        $exe = $candidate
        break
    }
}

if (-not $exe) {
    throw "找不到发布版 DeepSeekTranslator.exe，请先运行 scripts\build.ps1 -Configuration Release -Publish"
}

$name = "DeepSeek 划词翻译.lnk"
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) $name
$startMenuLink = Join-Path (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs") $name

$targets = @()
$shell = New-Object -ComObject WScript.Shell

if ($RefreshOnly) {
    foreach ($link in @($desktopLink, $startMenuLink)) {
        if (-not (Test-Path $link)) { continue }

        # 只刷新指向“便携版 releases 目录”的快捷方式；
        # 如果指向安装版（%LOCALAPPDATA%\Programs\...），交给安装包管理，不覆盖。
        $current = $shell.CreateShortcut($link)
        $currentTarget = [string]$current.TargetPath
        if ($currentTarget -and
            -not $currentTarget.StartsWith((Join-Path $root "releases"), [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "跳过（快捷方式指向已安装程序）：$link"
            continue
        }

        $targets += $link
    }

    if ($targets.Count -eq 0) {
        Write-Host "没有需要刷新的快捷方式。"
        return
    }
}
else {
    $targets += $desktopLink
    if ($StartMenu) { $targets += $startMenuLink }
}

foreach ($link in $targets) {
    $directory = Split-Path $link -Parent
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = Split-Path $exe -Parent
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = "DeepSeek 全局划词翻译"
    $shortcut.WindowStyle = 1
    $shortcut.Save()

    Write-Host "快捷方式：$link"
    Write-Host "  目标：$exe"
}
