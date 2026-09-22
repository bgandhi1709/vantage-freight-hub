using '../main.bicep'

param environmentName = 'uat'
param storageAccountName = readEnvironmentVariable('STORAGE_ACCOUNT_NAME', 'vwfreightuatsa')
param appServicePlanName = readEnvironmentVariable('APP_SERVICE_PLAN_NAME', 'vw-shared-uat-plan')
param forwarderBaseUrl = readEnvironmentVariable('FORWARDER_BASE_URL', 'https://forwarder-uat.example.test/v1')

// Secrets come from the pipeline's environment, never from this file.
param forwarderApiToken = readEnvironmentVariable('FORWARDER_API_TOKEN', '')
param serviceBusConnectionString = readEnvironmentVariable('SERVICE_BUS_CONNECTION', '')
param devEndpointApiKey = readEnvironmentVariable('DEV_ENDPOINT_API_KEY', '')
