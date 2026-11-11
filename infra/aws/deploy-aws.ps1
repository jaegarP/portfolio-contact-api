<#
.SYNOPSIS
  Deploys the AWS version of the contact API (Lambda + DynamoDB + SES) with AWS SAM.

.DESCRIPTION
  1. Checks you are signed in to AWS (aws configure / aws sso login / aws login).
  2. Makes sure your email address is verified in Amazon SES (sends a verification email
     the first time - click the link in it, then re-run this script).
  3. sam build + sam deploy (creates or updates the CloudFormation stack).
  4. Prints the API URL to paste into the website.
  Safe to re-run any time to redeploy.

.EXAMPLE
  ./infra/aws/deploy-aws.ps1 -NotifyEmail you@example.com
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $NotifyEmail,
  [string] $Region         = "eu-west-1",           # Ireland; change to e.g. eu-west-2 (London) or af-south-1 (Cape Town)
  [string] $StackName      = "portfolio-contact-api",
  [string] $AllowedOrigins = "https://jaegarp.github.io"
)

$ErrorActionPreference = "Stop"
function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Aws { $out = & aws @args; if ($LASTEXITCODE -ne 0) { throw "aws $($args -join ' ') failed" }; $out }

Step "Checking AWS sign-in"
$identity = Aws sts get-caller-identity --output json | ConvertFrom-Json
Write-Host "Account $($identity.Account) as $($identity.Arn)"

Step "Amazon SES: verifying $NotifyEmail (region $Region)"
$status = aws sesv2 get-email-identity --email-identity $NotifyEmail --region $Region --query VerificationStatus --output text 2>$null
if ($LASTEXITCODE -ne 0) {
  Aws sesv2 create-email-identity --email-identity $NotifyEmail --region $Region | Out-Null
  Write-Host "A verification email has been sent to $NotifyEmail." -ForegroundColor Yellow
  Write-Host "Click the link in it, then run this script again." -ForegroundColor Yellow
  exit 0
}
if ($status -ne "SUCCESS") {
  Write-Host "SES status is '$status'. Click the verification link sent to $NotifyEmail, then re-run." -ForegroundColor Yellow
  exit 0
}
Write-Host "Verified."

# IP-hash salt: stable across redeploys (so rate limits survive), derived from account + stack name.
$sha  = [System.Security.Cryptography.SHA256]::Create()
$salt = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("$($identity.Account)/$StackName/contact")))) -replace '-', ''

Push-Location $PSScriptRoot
try {
  Step "sam build"
  sam build --template-file template.yaml
  if ($LASTEXITCODE -ne 0) { throw "sam build failed" }

  Step "sam deploy ($StackName in $Region)"
  $overrides = @("NotifyEmail=$NotifyEmail", "AllowedOrigins=$AllowedOrigins", "IpHashSalt=$salt")
  sam deploy --stack-name $StackName --region $Region --resolve-s3 --capabilities CAPABILITY_IAM `
    --no-confirm-changeset --no-fail-on-empty-changeset --parameter-overrides $overrides
  if ($LASTEXITCODE -ne 0) { throw "sam deploy failed" }
}
finally { Pop-Location }

$url = Aws cloudformation describe-stacks --stack-name $StackName --region $Region `
  --query "Stacks[0].Outputs[?OutputKey=='ContactApiUrl'].OutputValue" --output text
Write-Host "`nDone." -ForegroundColor Green
Write-Host "AWS API endpoint: $url"
Write-Host "Add it to contact.apiUrls in the website's js/content.js"
