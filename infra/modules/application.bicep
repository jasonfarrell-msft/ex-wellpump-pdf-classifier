param location string
param appServiceName string
param appServicePlanName string
param storageAccountName string
param documentIntelligenceName string
param classifierRoleDefinitionId string
param tags object
param runtimeSettings object = {}

resource storage 'Microsoft.Storage/storageAccounts@2026-09-01' existing = {
  name: storageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2026-09-01' existing = {
  parent: storage
  name: 'default'
}

resource uploads 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-09-01' = {
  parent: blobService
  name: 'uploads'
  properties: {
    publicAccess: 'None'
  }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2026-09-01' existing = {
  parent: storage
  name: 'default'
}

resource jobs 'Microsoft.Storage/storageAccounts/queueServices/queues@2026-09-01' = {
  parent: queueService
  name: 'wellpump-jobs'
  properties: {}
}

resource poison 'Microsoft.Storage/storageAccounts/queueServices/queues@2026-09-01' = {
  parent: queueService
  name: 'wellpump-poison'
  properties: {}
}

resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: documentIntelligenceName
}

resource plan 'Microsoft.Web/serverfarms@2026-08-01' = {
  name: appServicePlanName
  location: location
  kind: 'linux'
  tags: tags
  sku: {
    name: 'B1'
    tier: 'Basic'
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

var settings = union(
  {
    ASPNETCORE_ENVIRONMENT: 'Production'
    ASPNETCORE_URLS: 'http://0.0.0.0:8080'
    PORT: '8080'
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    WEBSITE_HTTPLOGGING_RETENTION_DAYS: '3'
  },
  runtimeSettings
)

resource app 'Microsoft.Web/sites@2026-08-01' = {
  name: appServiceName
  location: location
  kind: 'app,linux'
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      healthCheckPath: '/healthz'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      remoteDebuggingEnabled: false
      use32BitWorkerProcess: false
    }
  }
}

resource appSettings 'Microsoft.Web/sites/config@2026-08-01' = {
  parent: app
  name: 'appsettings'
  properties: union(settings, {
    Cloud__PublicOrigin: 'https://${app.properties.defaultHostName}'
  })
}

resource scmPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2026-08-01' = {
  parent: app
  name: 'scm'
  properties: {
    allow: false
  }
}

resource ftpPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2026-08-01' = {
  parent: app
  name: 'ftp'
  properties: {
    allow: false
  }
}

var blobContributorRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)
var blobDelegatorRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'db58b8e5-c6ad-4a2a-8342-4190687cbf4a'
)
var queueContributorRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
)

resource blobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(uploads.id, app.id, blobContributorRole)
  scope: uploads
  properties: {
    roleDefinitionId: blobContributorRole
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource blobDelegator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, app.id, blobDelegatorRole)
  scope: storage
  properties: {
    roleDefinitionId: blobDelegatorRole
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource jobQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(jobs.id, app.id, queueContributorRole)
  scope: jobs
  properties: {
    roleDefinitionId: queueContributorRole
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource poisonQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(poison.id, app.id, queueContributorRole)
  scope: poison
  properties: {
    roleDefinitionId: queueContributorRole
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource classifierUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documentIntelligence.id, app.id, classifierRoleDefinitionId)
  scope: documentIntelligence
  properties: {
    roleDefinitionId: classifierRoleDefinitionId
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output appUrl string = 'https://${app.properties.defaultHostName}'
output principalId string = app.identity.principalId
output appServiceId string = app.id
output uploadsContainerId string = uploads.id
