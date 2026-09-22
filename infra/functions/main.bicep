targetScope = 'resourceGroup'

@description('Short environment name, used in every resource name.')
param environmentName string

param location string = resourceGroup().location

@description('Name of an existing storage account holding the snapshots and the lifecycle table.')
param storageAccountName string

@description('Resource group of that storage account.')
param storageAccountResourceGroup string = resourceGroup().name

@description('Name of an existing App Service plan. Shared rather than created per app — an idle plan per service is the most common avoidable cost in a Functions estate.')
param appServicePlanName string

param appServicePlanResourceGroup string = resourceGroup().name

@description('Base URL of the freight forwarder API.')
param forwarderBaseUrl string

@secure()
param forwarderApiToken string

@secure()
param serviceBusConnectionString string

@secure()
param devEndpointApiKey string

param shipmentQueueName string = 'shipment-commands'
param callbackTopicName string = 'forwarder-callbacks'

param tags object = {
  workload: 'freight-hub'
  environment: environmentName
}

var namePrefix = 'vw-freight-${environmentName}'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
  scope: resourceGroup(storageAccountResourceGroup)
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' existing = {
  name: appServicePlanName
  scope: resourceGroup(appServicePlanResourceGroup)
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-insights'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
  }
}

var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${namePrefix}-func'
  location: location
  tags: tags
  kind: 'functionapp'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      ftpsState: 'FtpsOnly'
      http20Enabled: true
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'AzureWebJobsStorage'
          value: storageConnectionString
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: insights.properties.ConnectionString
        }
        {
          name: 'ServiceBusConnection'
          value: serviceBusConnectionString
        }
        {
          name: 'ShipmentQueueName'
          value: shipmentQueueName
        }
        {
          name: 'CallbackTopicName'
          value: callbackTopicName
        }
        {
          name: 'AccountSubscriptionName'
          value: 'account-callbacks'
        }
        {
          name: 'BookingSubscriptionName'
          value: 'booking-callbacks'
        }
        {
          name: 'PickupSubscriptionName'
          value: 'pickup-callbacks'
        }
        {
          name: 'DeliverySubscriptionName'
          value: 'delivery-callbacks'
        }
        {
          name: 'FreightHub__StorageConnectionString'
          value: storageConnectionString
        }
        {
          name: 'FreightHub__SnapshotContainerName'
          value: 'shipments'
        }
        {
          name: 'FreightHub__LifecycleTableName'
          value: 'shipmentlifecycle'
        }
        {
          name: 'FreightHub__ForwarderBaseUrl'
          value: forwarderBaseUrl
        }
        {
          name: 'FreightHub__ForwarderApiToken'
          value: forwarderApiToken
        }
        {
          name: 'FreightHub__DevEndpointApiKey'
          value: devEndpointApiKey
        }
      ]
    }
  }
}

output functionAppName string = functionApp.name
output applicationInsightsName string = insights.name
