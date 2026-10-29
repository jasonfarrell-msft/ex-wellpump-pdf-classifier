# Azure deployment

The templates deploy one Linux B1 App Service in **Sweden Central**, in the existing
`rg-transocean-classifier-mx01` group, subscription
`bb4b2781-6739-4fa1-994e-4ad6ce55c59c`. They preserve the existing Storage and
Document Intelligence account configuration.

The app serves React, the .NET 10 API, and a durable queue worker. B1 Always On
is approximately **USD 13.14/month** at the verified USD 0.018/hour retail rate
(730 hours). Classification, storage, transactions and data transfer are additional
usage charges. The user explicitly removed application upload and usage caps.
This anonymous public endpoint has no application spending bound; Azure quotas,
hosting capacity, parser execution timeout and retention still apply.

## Current verification status

As of 2026-10-28, infrastructure and application deployment and the complete live
upload/classify/SAS flow succeeded. The two-page synthetic PDF was classified
page by page; each delegated link downloaded the correct one-page PDF. Readiness,
browser ownership isolation, upload-origin enforcement and invalid-file handling
passed.

The user explicitly approved Cognitive Services User on the Document Intelligence
account after the narrow custom role remained denied. Access succeeded after role
propagation. The redundant custom assignment and unused definition were removed,
then the full live flow passed again with exactly five intended runtime grants.
Earlier denied requests remain historical diagnostic evidence, not the current
status. Temporary token-diagnostic settings were removed. No account keys were
retrieved or used.

## Provision

Azure CLI must be signed in to the intended subscription. The deploying identity
needs permission to provision App Service, create the specified Storage child
resources and assign roles at the scopes below. Runtime identities
receive no Owner or Contributor management role.

```sh
az deployment sub what-if \
  --subscription bb4b2781-6739-4fa1-994e-4ad6ce55c59c \
  --name wellpump-demo-7d79-preview --location swedencentral \
  --template-file infra/main.bicep --parameters @infra/main.parameters.json

az deployment sub create \
  --subscription bb4b2781-6739-4fa1-994e-4ad6ce55c59c \
  --name wellpump-demo-7d79 --location swedencentral \
  --template-file infra/main.bicep --parameters @infra/main.parameters.json
```

Use the returned `appUrl`, not an assumed `appname.azurewebsites.net` hostname.
`Cloud__PublicOrigin` is derived from the app's actual default hostname. If adding
a custom hostname, update that setting through Bicep as well.

The provider-verified GA API versions can be newer than locally bundled Bicep type
definitions. BCP081 warnings therefore require live ARM validation, not an
assumption that a locally compiled template has complete schema coverage.

## Build and deploy code

From the application root, install frontend dependencies with `npm ci` if needed.
Then build and publish:

```sh
bash scripts/publish.sh
(cd artifacts/server && zip -qr ../wellpump.zip .)
az webapp deploy \
  --subscription bb4b2781-6739-4fa1-994e-4ad6ce55c59c \
  --resource-group rg-transocean-classifier-mx01 \
  --name app-wellpump-demo-7d79 \
  --src-path artifacts/wellpump.zip --type zip
```

Use an Entra-capable Azure CLI (2.48.1 or later). Do not use publishing profiles,
passwords, account keys, or the legacy `deployment source config-zip` command.
SCM and FTP basic authentication remain disabled. Allow bounded time for managed
identity role propagation before treating an initial dependency 403 as a defect.

## Runtime authorization

All grants target the app's **system-assigned identity**:

| Role | Scope |
|---|---|
| Storage Blob Data Contributor | `uploads` container |
| Storage Blob Delegator | Existing storage account; needed to obtain a user delegation key |
| Storage Queue Data Contributor | Each of `wellpump-jobs` and `wellpump-poison` |
| Cognitive Services User | Existing Document Intelligence account only |

**User-approved broader permissions:** Cognitive Services User includes account-key
export, connection-secret retrieval and broader model operations. It is not an
inference-only role. The app uses managed-identity authentication and only calls
classifier metadata, analyze and result endpoints; it does not retrieve or use
account keys or connection secrets. The successful test does not identify which
additional permission was necessary. The built-in role ID is
`a97b65f3-24c7-4388-baec-2e87135dc908`; its assignment remains account-scoped.

No Key Vault is needed because no application credential is stored. Diagnostic
output uses redacted platform logs, without response headers, cookies, tokens or
SAS URLs. Existing service network and local-auth settings are not changed.

## Verification and removal

Check `/healthz`, `/readyz`, and a synthetic multi-page upload. Verify each page
has a classification and a new-window, five-minute read-only HTTPS user-delegation
SAS link to a one-page PDF. Confirm a second browser cannot read the first
browser's documents. The root application README describes the remaining limits.

This is an anonymous public proof of concept, not a production private-network
deployment. Do not upload sensitive documents.

**Never delete the existing resource group as cleanup.** It contains the user's
Document Intelligence service, Storage account, Event Grid topic and other data.
To retire this app, remove its app/plan and its five runtime role assignments.
The earlier custom role has already been removed. Delete only confirmed application-owned blob
prefixes and the two app queues after deciding whether their contents should be
preserved. Do not delete the Storage account, existing models, unrelated blobs,
or shared policies.
