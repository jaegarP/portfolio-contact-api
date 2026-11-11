<#
.SYNOPSIS
  Creates (or updates) all Azure resources for the portfolio contact API and deploys the code.

.DESCRIPTION
  Resources (all in one resource group, so they can be deleted together):
    • Storage account        — Functions runtime storage + "ContactMessages" table
    • Function App           — Flex Consumption (serverless) plan, .NET isolated worker
    • Application Insights   — logs, failures, request metrics
    • Communication Services — + Email Service with an Azure-managed sender domain
  Safe to re-run: existing resources are reused, settings are refreshed.

.EXAMPLE
  ./infra/deploy.ps1 -NotifyEmail you@example.com
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $NotifyEmail,                 # where enquiries are delivered
  [string] $ResourceGroup  = "rg-portfolio-contact",
  [string] $Location       = "westeurope",                      # Function App + storage region
  [string] $DataLocation   = "Europe",                          # Communication Services data residency
  [string] $Prefix         = "jpcontact",                       # used to build globally-unique names
  [string] $AllowedOrigins = "https://jaegarp.github.io",
  [switch] $SkipCodeDeploy
)

$ErrorActionPreference = "Continue"   # native tools report via exit codes, checked explicitly below
function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Invoke-Az { $out = & az.cmd @args; if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed" }; $out }

# Deterministic suffix per subscription so names are unique but stable across re-runs
$subId   = Invoke-Az account show --query id -o tsv
$suffix  = ($subId -replace '-', '').Substring(0, 6)
$storage = "$($Prefix)st$suffix".ToLower()
$funcApp = "$Prefix-api-$suffix"
$acs     = "$Prefix-acs-$suffix"
$emailSv = "$Prefix-email-$suffix"

Step "Subscription $subId — names: $funcApp / $storage / $acs"

Step "Registering resource providers (first run on a new subscription can take a minute)"
foreach ($ns in "Microsoft.Web", "Microsoft.Storage", "Microsoft.Communication", "Microsoft.Insights", "Microsoft.OperationalInsights") {
  Invoke-Az provider register --namespace $ns --wait | Out-Null
}

Step "Resource group $ResourceGroup ($Location)"
Invoke-Az group create -n $ResourceGroup -l $Location -o none

Step "Storage account $storage"
if (-not (az storage account show -n $storage -g $ResourceGroup -o none 2>$null; $?)) {
  Invoke-Az storage account create -n $storage -g $ResourceGroup -l $Location --sku Standard_LRS `
    --min-tls-version TLS1_2 --allow-blob-public-access false -o none
}
$storageConn = Invoke-Az storage account show-connection-string -n $storage -g $ResourceGroup --query connectionString -o tsv

Step "Function App $funcApp (Flex Consumption, .NET 10 isolated)"
if (-not (az functionapp show -n $funcApp -g $ResourceGroup -o none 2>$null; $?)) {
  Invoke-Az functionapp create -n $funcApp -g $ResourceGroup --storage-account $storage `
    --flexconsumption-location $Location --runtime dotnet-isolated --runtime-version 10.0 -o none
}

Step "Communication Services + Email (Azure-managed sender domain)"
az extension add --name communication --upgrade --only-show-errors 2>$null | Out-Null
if (-not (az communication email show -n $emailSv -g $ResourceGroup -o none 2>$null; $?)) {
  Invoke-Az communication email create -n $emailSv -g $ResourceGroup --location global --data-location $DataLocation -o none
}
if (-not (az communication email domain show --domain-name AzureManagedDomain --email-service-name $emailSv -g $ResourceGroup -o none 2>$null; $?)) {
  Invoke-Az communication email domain create --domain-name AzureManagedDomain --email-service-name $emailSv -g $ResourceGroup `
    --location global --domain-management AzureManaged -o none
}
$domain   = Invoke-Az communication email domain show --domain-name AzureManagedDomain --email-service-name $emailSv -g $ResourceGroup -o json | ConvertFrom-Json
$sender   = "DoNotReply@$($domain.mailFromSenderDomain)"
if (-not (az communication show -n $acs -g $ResourceGroup -o none 2>$null; $?)) {
  Invoke-Az communication create -n $acs -g $ResourceGroup --location global --data-location $DataLocation -o none
}
Invoke-Az communication update -n $acs -g $ResourceGroup --linked-domains $domain.id -o none
$acsConn = Invoke-Az communication list-key -n $acs -g $ResourceGroup --query primaryConnectionString -o tsv

Step "App settings"
$saltSetting = az functionapp config appsettings list -n $funcApp -g $ResourceGroup --query "[?name=='IpHashSalt'].value" -o tsv
$salt = if ($saltSetting) { $saltSetting } else { [guid]::NewGuid().ToString("N") }
Invoke-Az functionapp config appsettings set -n $funcApp -g $ResourceGroup -o none --settings `
  "AllowedOrigins=$AllowedOrigins" `
  "MaxMessagesPerHour=5" `
  "IpHashSalt=$salt" `
  "MessagesStorageConnectionString=$storageConn" `
  "MessagesTableName=ContactMessages" `
  "CommunicationServicesConnectionString=$acsConn" `
  "SenderEmail=$sender" `
  "NotifyEmail=$NotifyEmail"

Step "CORS: $AllowedOrigins"
$existing = az functionapp cors show -n $funcApp -g $ResourceGroup --query allowedOrigins -o tsv
foreach ($o in $AllowedOrigins.Split(',')) {
  if ($existing -notcontains $o.Trim()) { Invoke-Az functionapp cors add -n $funcApp -g $ResourceGroup --allowed-origins $o.Trim() -o none }
}

if (-not $SkipCodeDeploy) {
  Step "Publishing code"
  Push-Location (Join-Path $PSScriptRoot "..\src\ContactApi")
  try { func azure functionapp publish $funcApp --dotnet-isolated; if ($LASTEXITCODE -ne 0) { throw "publish failed" } }
  finally { Pop-Location }
}

$hostName = Invoke-Az functionapp show -n $funcApp -g $ResourceGroup --query defaultHostName -o tsv
Write-Host "`nDone." -ForegroundColor Green
Write-Host "API endpoint : https://$hostName/api/contact"
Write-Host "Sender       : $sender"
Write-Host "Delivers to  : $NotifyEmail"
