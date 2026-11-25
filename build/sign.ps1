<#
  Code-signing helpers, dot-sourced by publish.ps1 and make-installer.ps1 when they get -Sign.

  By default this uses a SELF-SIGNED certificate ("Glance (self-signed)") that it creates in your user certificate store.
  A self-signed signature does not remove the SmartScreen warning for other people (Windows does not trust the certificate),
  it only proves the files were not changed after signing. To remove the warning you need a certificate from a trusted
  authority (Azure Trusted Signing, an OV/EV certificate, SignPath for open source). To use one, set the thumbprint of a
  certificate that is in your CurrentUser\My store:   $env:GLANCE_SIGN_THUMBPRINT = '<thumbprint>'
  Nothing is added to Trusted Root, and no private key leaves this PC.
#>

function Get-GlanceSigningCert {
    if ($env:GLANCE_SIGN_THUMBPRINT) {
        $c = Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq $env:GLANCE_SIGN_THUMBPRINT | Select-Object -First 1
        if (-not $c) { throw "Certificate $($env:GLANCE_SIGN_THUMBPRINT) not found in CurrentUser\My." }
        return $c
    }
    $subject = 'CN=Glance (self-signed)'
    $c = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
         Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $c) {
        Write-Host "Creating a self-signed code-signing certificate ($subject, 3 years) ..." -ForegroundColor Yellow
        $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My `
             -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(3)
    }
    return $c
}

function Invoke-GlanceSign {
    param([Parameter(Mandatory)][string[]]$Path)
    $cert = Get-GlanceSigningCert
    foreach ($file in $Path) {
        $r = $null
        try { $r = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' }
        catch { }
        if (-not $r -or -not $r.SignerCertificate) {
            # no network for the timestamp server: sign without a timestamp
            $r = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256
        }
        if (-not $r.SignerCertificate) { throw "Signing $file failed: $($r.StatusMessage)" }
        Write-Host ("  signed {0}" -f (Split-Path $file -Leaf))
    }
}
