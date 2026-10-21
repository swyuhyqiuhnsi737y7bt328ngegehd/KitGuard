param(
    [string]$Sys = (Join-Path $PSScriptRoot 'KitGuardDrv.sys')
)
$ErrorActionPreference = 'Stop'

$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw '找不到 signtool.exe，请安装 WDK' }

$subject = 'CN=KitGuard Test'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if (-not $cert) {
    Write-Host '创建自签名代码签名证书...'
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(10)
}
Export-Certificate -Cert $cert -FilePath (Join-Path $PSScriptRoot 'KitGuardTest.cer') -Force | Out-Null

Write-Host ('证书指纹: ' + $cert.Thumbprint)
& $signtool.FullName sign /v /fd SHA256 /ph /sha1 $cert.Thumbprint /s My $Sys
if ($LASTEXITCODE -ne 0) { throw 'signtool 签名失败' }
Write-Host '[OK] 驱动已签名'
