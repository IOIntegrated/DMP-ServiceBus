param(
    [string]$TenantId = "ab117131-4fc5-4454-ba23-f887c802e714",
    [string]$SubscriptionId,
    [string]$ResourceGroupName = "rg-dmp-servicebus",
    [string]$Location = "northeurope",
    [string]$ServiceBusNamespaceName,
    [string]$FunctionAppName,
    [string]$StorageAccountName,
    [string]$RequestQueueName = "requests",
    [string]$ResponseQueueName = "responses",
    [string]$ConnectionId = "dmp-$([guid]::NewGuid().ToString('N'))"
)

$ErrorActionPreference = "Stop"

function Assert-CommandExists {
    param([string]$CommandName)

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found."
    }
}

function Invoke-Az {
    $arguments = $args
    & az @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command '$($arguments[0])' failed with exit code $LASTEXITCODE."
    }
}

function New-UniqueName {
    param(
        [string]$Prefix,
        [int]$MaxLength
    )

    $suffix = (Get-Random -Maximum 999999).ToString("000000")
    $name = "$Prefix$suffix".ToLowerInvariant() -replace "[^a-z0-9-]", ""
    if ($name.Length -gt $MaxLength) {
        return $name.Substring(0, $MaxLength)
    }

    return $name
}

Assert-CommandExists -CommandName "az"
Assert-CommandExists -CommandName "func"

$repoRoot = $PSScriptRoot
$functionProjectPath = Join-Path $repoRoot "FunctionAppNet8"

if (-not (Test-Path (Join-Path $functionProjectPath "FunctionApp.csproj"))) {
    throw "Function project was not found at '$functionProjectPath'."
}

$currentTenantId = az account show --query tenantId --output tsv 2>$null
if ($LASTEXITCODE -ne 0 -or $currentTenantId -ne $TenantId) {
    Invoke-Az login --tenant $TenantId --use-device-code --allow-no-subscriptions --only-show-errors | Out-Null
}

$accounts = Invoke-Az account list --all --query "[?tenantId=='$TenantId' && name!='N/A(tenant level account)']" --output json | ConvertFrom-Json
if (-not $SubscriptionId) {
    if (-not $accounts -or $accounts.Count -eq 0) {
        throw "No Azure subscription is visible in tenant '$TenantId'. Assign a subscription or pass -SubscriptionId for an accessible subscription."
    }

    if ($accounts.Count -gt 1) {
        $accounts | Select-Object name, id, state | Format-Table
        throw "Multiple subscriptions are available. Re-run with -SubscriptionId '<subscription-id>'."
    }

    $SubscriptionId = $accounts[0].id
}

Invoke-Az account set --subscription $SubscriptionId

Write-Host "Registering required Azure resource providers..."
foreach ($provider in @(
    "Microsoft.Storage",
    "Microsoft.ServiceBus",
    "Microsoft.Web",
    "Microsoft.OperationalInsights",
    "Microsoft.Insights"
)) {
    Invoke-Az provider register --namespace $provider --wait
}

$suffix = (Get-Random -Maximum 999999).ToString("000000")
if (-not $ServiceBusNamespaceName) { $ServiceBusNamespaceName = "dmp-sb-$suffix" }
if (-not $FunctionAppName) { $FunctionAppName = "dmp-func-$suffix" }
if (-not $StorageAccountName) { $StorageAccountName = ("dmpfunc$suffix").ToLowerInvariant() }

Write-Host "Creating resource group '$ResourceGroupName' in '$Location'..."
$existingResourceGroupLocation = Invoke-Az group list `
    --query "[?name=='$ResourceGroupName'].location | [0]" `
    --output tsv
if ([string]::IsNullOrWhiteSpace($existingResourceGroupLocation)) {
    Invoke-Az group create --name $ResourceGroupName --location $Location --output none
}
else {
    Write-Host "Using existing resource group in '$existingResourceGroupLocation'."
}

Write-Host "Creating storage account '$StorageAccountName'..."
Invoke-Az storage account create `
    --name $StorageAccountName `
    --resource-group $ResourceGroupName `
    --location $Location `
    --sku Standard_LRS `
    --kind StorageV2 `
    --output none

Write-Host "Creating Service Bus namespace '$ServiceBusNamespaceName'..."
Invoke-Az servicebus namespace create `
    --name $ServiceBusNamespaceName `
    --resource-group $ResourceGroupName `
    --location $Location `
    --sku Standard `
    --output none

Write-Host "Creating session-enabled queues '$RequestQueueName' and '$ResponseQueueName'..."
Invoke-Az servicebus queue create `
    --name $RequestQueueName `
    --namespace-name $ServiceBusNamespaceName `
    --resource-group $ResourceGroupName `
    --enable-session true `
    --output none

Invoke-Az servicebus queue create `
    --name $ResponseQueueName `
    --namespace-name $ServiceBusNamespaceName `
    --resource-group $ResourceGroupName `
    --enable-session true `
    --output none

Write-Host "Creating restricted Service Bus access rule..."
$serviceBusPolicyName = "DMPServiceBusAccess"
Invoke-Az servicebus namespace authorization-rule create `
    --name $serviceBusPolicyName `
    --namespace-name $ServiceBusNamespaceName `
    --resource-group $ResourceGroupName `
    --rights Send Listen `
    --output none

$serviceBusConnectionString = Invoke-Az servicebus namespace authorization-rule keys list `
    --namespace-name $ServiceBusNamespaceName `
    --resource-group $ResourceGroupName `
    --name $serviceBusPolicyName `
    --query primaryConnectionString `
    --output tsv

if ([string]::IsNullOrWhiteSpace($serviceBusConnectionString)) {
    throw "Azure Service Bus did not return a connection string."
}

Write-Host "Creating Function App '$FunctionAppName'..."
Invoke-Az functionapp create `
    --name $FunctionAppName `
    --resource-group $ResourceGroupName `
    --storage-account $StorageAccountName `
    --consumption-plan-location $Location `
    --runtime dotnet-isolated `
    --runtime-version 10 `
    --functions-version 4 `
    --os-type Windows `
    --output none

Write-Host "Configuring Function App settings..."
Invoke-Az functionapp config appsettings set `
    --name $FunctionAppName `
    --resource-group $ResourceGroupName `
    --settings `
        "ServiceBusConnectionString=$serviceBusConnectionString" `
        "RequestQueueName=$RequestQueueName" `
        "ResponseQueueName=$ResponseQueueName" `
    --output none

Write-Host "Publishing Azure Function..."
Push-Location $functionProjectPath
try {
    func azure functionapp publish $FunctionAppName --dotnet-isolated
    if ($LASTEXITCODE -ne 0) {
        throw "Azure Functions deployment failed for '$FunctionAppName'."
    }
}
finally {
    Pop-Location
}

$functionKey = Invoke-Az functionapp function keys list `
    --function-name SendToDMP `
    --name $FunctionAppName `
    --resource-group $ResourceGroupName `
    --query default `
    --output tsv

if ([string]::IsNullOrWhiteSpace($functionKey)) {
    throw "Azure Functions did not return a function key."
}

$functionUrl = "https://$FunctionAppName.azurewebsites.net/api/SendToDMP?code=$functionKey"

Write-Host ""
Write-Host "Azure Service Bus values for Business Central"
Write-Host "---------------------------------------------"
Write-Host "Service Bus Connection String: $serviceBusConnectionString"
Write-Host "Request Queue Name:           $RequestQueueName"
Write-Host "Response Queue Name:          $ResponseQueueName"
Write-Host "Connection Id:                $ConnectionId"
Write-Host ""
Write-Host "Function URL"
Write-Host "------------"
Write-Host $functionUrl