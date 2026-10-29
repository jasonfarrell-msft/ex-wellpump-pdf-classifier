targetScope = 'subscription'

param resourceGroupName string = 'rg-transocean-classifier-mx01'
param location string = 'swedencentral'
param appServiceName string = 'app-wellpump-demo-7d79'
param appServicePlanName string = 'asp-wellpump-demo-7d79'
param storageAccountName string = 'stwellpumpdocsswmx01'
param documentIntelligenceName string = 'doc-wellpump-swc-mx01'
param environmentName string = 'wellpump-demo-7d79'
param sessionId string
param deployedBy string
param createdAt string

@description('Non-secret application settings. Authentication is exclusively managed identity.')
param runtimeSettings object = {}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' existing = {
  name: resourceGroupName
}

var classifierRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'a97b65f3-24c7-4388-baec-2e87135dc908'
)

module application './modules/application.bicep' = {
  name: 'wellpump-application'
  scope: resourceGroup
  params: {
    location: location
    appServiceName: appServiceName
    appServicePlanName: appServicePlanName
    storageAccountName: storageAccountName
    documentIntelligenceName: documentIntelligenceName
    classifierRoleDefinitionId: classifierRoleDefinitionId
    runtimeSettings: runtimeSettings
    tags: {
      'app-onboard-skill': 'true'
      'app-onboard-session-id': sessionId
      'created-at': createdAt
      environment: environmentName
      'deployed-by': deployedBy
    }
  }
}

output appUrl string = application.outputs.appUrl
output principalId string = application.outputs.principalId
output appServiceId string = application.outputs.appServiceId
output uploadsContainerId string = application.outputs.uploadsContainerId
