param(
    [string]$Subject = "CN=DeepSeek Translator (Internal)",
    [string]$PfxPath,
    [string]$PfxPassword,
    [switch]$Trust
)

$ErrorActionPreference = "Stop"

Write-Host "== Create self-signed code-signing certificate =="
Write-Host "This certificate is only trusted on machines where it is installed as a trusted root."
Write-Host "For public distribution you need a real OV/EV code-signing certificate from a CA."
Write-Host ""

$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(3)

Write-Host "Created certificate:"
Write-Host "  Subject:    $($certificate.Subject)"
Write-Host "  Thumbprint: $($certificate.Thumbprint)"
Write-Host "  Expires:    $($certificate.NotAfter)"
Write-Host ""

if ($PfxPath) {
    if (-not $PfxPassword) {
        $PfxPassword = [guid]::NewGuid().ToString("N")
        Write-Host "Generated PFX password: $PfxPassword"
        Write-Host "(store it somewhere safe; you will need it for CI signing)"
    }

    $directory = Split-Path $PfxPath -Parent
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $secure = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $certificate -FilePath $PfxPath -Password $secure | Out-Null
    Write-Host "Exported PFX: $PfxPath"
    Write-Host ""
}

if ($Trust) {
    # 只影响当前用户：把这张自签证书放进“受信任的根证书颁发机构”
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        "Root", "CurrentUser")
    $store.Open("ReadWrite")
    try {
        $store.Add($certificate)
    }
    finally {
        $store.Close()
    }

    Write-Host "Added to CurrentUser trusted roots (signature will show as valid on this account)."
    Write-Host "Remove later with:"
    Write-Host "  Get-ChildItem Cert:\CurrentUser\Root | Where-Object Thumbprint -eq '$($certificate.Thumbprint)' | Remove-Item"
    Write-Host ""
}

Write-Host "Next:"
Write-Host "  powershell -File .\scripts\build-installer.ps1"
Write-Host "or set DSH_SIGN_THUMBPRINT=$($certificate.Thumbprint) for CI."
