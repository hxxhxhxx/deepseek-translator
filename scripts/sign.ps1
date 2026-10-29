param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [string]$PfxPath = $env:DSH_SIGN_PFX,
    [string]$PfxPassword = $env:DSH_SIGN_PFX_PASSWORD,
    [string]$Thumbprint = $env:DSH_SIGN_THUMBPRINT,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$Required
)

$ErrorActionPreference = "Stop"

function Get-SigningCertificate {
    if ($PfxPath) {
        if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
        $secure = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
            (Resolve-Path $PfxPath), $secure)
        if (-not $cert.HasPrivateKey) { throw "PFX has no private key: $PfxPath" }
        return $cert
    }

    if ($Thumbprint) {
        $cert = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
        if (-not $cert) { throw "Certificate not found in CurrentUser\My: $Thumbprint" }
        return $cert
    }

    # 自动发现：当前用户证书库里带私钥、主题含 DeepSeek 的一张
    return Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.HasPrivateKey -and $_.Subject -like "*DeepSeek*" } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

$certificate = Get-SigningCertificate
if (-not $certificate) {
    if ($Required) {
        throw "No code-signing certificate found. Run scripts\new-signing-cert.ps1 for an internal cert, or set DSH_SIGN_PFX / DSH_SIGN_THUMBPRINT."
    }

    Write-Warning "No code-signing certificate found - building UNSIGNED. Set DSH_SIGN_PFX or run scripts\new-signing-cert.ps1."
    return
}

Write-Host "Signing with: $($certificate.Subject)"
Write-Host "  thumbprint: $($certificate.Thumbprint)"
Write-Host "  expires:    $($certificate.NotAfter)"

$files = @()
foreach ($item in $Path) {
    if (Test-Path $item -PathType Container) {
        $files += Get-ChildItem $item -Recurse -File -Include *.exe, *.dll
    }
    elseif (Test-Path $item) {
        $files += Get-Item $item
    }
    else {
        throw "Path not found: $item"
    }
}

if ($files.Count -eq 0) {
    Write-Warning "Nothing to sign."
    return
}

$failures = @()

foreach ($file in $files) {
    $signed = $false

    # 先带时间戳签；没有外网/时间戳服务不可达时退回不带时间戳
    foreach ($useTimestamp in @($true, $false)) {
        try {
            $params = @{
                FilePath        = $file.FullName
                Certificate     = $certificate
                HashAlgorithm   = "SHA256"
                ErrorAction     = "Stop"
            }
            if ($useTimestamp -and $TimestampUrl) {
                $params["TimestampServer"] = $TimestampUrl
            }

            $signature = Set-AuthenticodeSignature @params
            if ($signature.Status -eq "Valid" -or $signature.Status -eq "UnknownError") {
                $signed = $true
                break
            }

            Write-Warning "$($file.Name): $($signature.Status) - $($signature.StatusMessage)"
        }
        catch {
            if ($useTimestamp) {
                Write-Warning "Timestamp failed for $($file.Name), retrying without timestamp: $($_.Exception.Message)"
            }
            else {
                Write-Warning "Signing failed for $($file.Name): $($_.Exception.Message)"
            }
        }
    }

    if ($signed) {
        Write-Host ("  signed  {0}" -f $file.Name)
    }
    else {
        $failures += $file.Name
    }
}

if ($failures.Count -gt 0) {
    throw "Failed to sign: $($failures -join ', ')"
}

$files | ForEach-Object {
    $status = (Get-AuthenticodeSignature $_.FullName).Status
    Write-Host ("  verify  {0}: {1}" -f $_.Name, $status)
}
