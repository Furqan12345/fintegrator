# Agent handover — Fintegrator Azure deployment

**Updated:** 2026-10-02  
**Repository:** `C:\Users\FurqanIrfan\Documents\fintegrator-main`  
**Live app:** [Fintegrator](https://fintegrator-web-e07356e8.azurewebsites.net)  
**Azure provider page:** [AI Providers](https://fintegrator-web-e07356e8.azurewebsites.net/ai-providers)

## Current status

The multi-provider AI feature and the provider-error fix are deployed to the existing Azure App Service. The latest deployment completed successfully on 2026-10-02. `/health/live` and authenticated `/health/ready` both returned HTTP 200 Healthy.

One Azure OpenAI provider is saved with model `gpt-5.6-luna`, endpoint path `/openai/v1/chat/completions`, and `HasApiKey = true`; the key value was not read or printed.

The user's Test connection failure was reproduced and fixed. The controller had local duplicate exception classes that shadowed the AI service exceptions, turning provider HTTP errors into generic 500s. After removing those duplicates, the provider's HTTP 400 became visible. Azure GPT-5.6 Chat Completions requires `max_completion_tokens` instead of `max_tokens`; GPT-5.6 tool calls also require `reasoning_effort = none`. The provider-error parser now returns Azure's message with the API key redacted. After deployment, the saved provider's Test connection returned HTTP 200, “Provider connection succeeded.”

## Azure resources and topology

| Resource | Current value |
|---|---|
| Subscription ID | `2c506250-e4f8-4302-b51b-b39d0220f7ab` (Azure subscription 1) |
| Resource group | `rg-fintegrator` |
| App Service | `fintegrator-web-e07356e8` |
| App URL | `https://fintegrator-web-e07356e8.azurewebsites.net` |
| Plan | `fintegrator-plan`, Windows B1, Central US, one instance |
| SQL server | `fintegrator-e07356e8.database.windows.net` |
| SQL database | `fintegrator`, Basic, 5 DTU, 2 GB |

The UI, API, and continuous Engine WebJob run in the **same App Service** and share Azure SQL. The Engine is not on a separate host, so a compute spike can affect the UI/API and background worker together. The Basic SQL database is also shared. This setup is a private pilot, not an isolated or highly available production topology.

## How deployments are performed

Use the combined package script for the existing live app:

```powershell
$zip = & '.\ops\azure\publish-appservice.ps1'
```

The script publishes the API, Engine, and Blazor client in Release mode, places the Engine under `App_Data/jobs/continuous/engine`, prepares the web startup asset reference, removes development settings, and creates a ZIP under `artifacts/appservice/<timestamp>/`.

Deploy that ZIP to the existing app, explicitly selecting the subscription:

```powershell
$webName = (Get-Content -LiteralPath 'ops/azure/webapp-name.local' -Raw).Trim()
az webapp deploy -g rg-fintegrator -n $webName --subscription 2c506250-e4f8-4302-b51b-b39d0220f7ab --src-path $zip --type zip --timeout 600000
```

Do **not** deploy `publish-appservice-api-only.ps1` to this live App Service; it omits the Engine WebJob. A separate Engine host has not been provisioned. Use `ops/azure/README.md` as the operational reference.

Azure CLI access is through the current Windows user's saved login. Verify the intended subscription before deploying:

```powershell
az account show --subscription 2c506250-e4f8-4302-b51b-b39d0220f7ab --query "{name:name,id:id}" -o json
az webapp show -g rg-fintegrator -n fintegrator-web-e07356e8 --subscription 2c506250-e4f8-4302-b51b-b39d0220f7ab --query state -o tsv
```

App Service restarts can temporarily return 503 for some static assets. Wait for startup to finish, then retry the app checks instead of starting another deployment.

## Health and browser verification

`/health/live` is unauthenticated. `/health/ready` requires the app's HTTP Basic gateway credentials and checks the database and Engine.

```powershell
Invoke-WebRequest 'https://fintegrator-web-e07356e8.azurewebsites.net/health/live'
$credential = Import-Clixml -LiteralPath 'ops/azure/gateway.credential.local'
Invoke-WebRequest 'https://fintegrator-web-e07356e8.azurewebsites.net/health/ready' -Credential $credential -Authentication Basic
```

The gateway username is `admin`. The password is stored in the DPAPI-protected file and should not be printed.

The browser verification helper is `ops/azure/verify-browser.cjs`. It uses the bundled Playwright runtime and installed Chrome. Read the gateway password from the DPAPI-protected credential file into the environment without printing it:

```powershell
$credential = Import-Clixml -LiteralPath 'ops/azure/gateway.credential.local'
$env:FINTEGRATOR_GATEWAY_PASSWORD = $credential.GetNetworkCredential().Password
node ops/azure/verify-browser.cjs
Remove-Item Env:FINTEGRATOR_GATEWAY_PASSWORD -ErrorAction SilentlyContinue
```

For readiness, use the same credential with PowerShell's Basic authentication support to request `/health/ready`. Do not paste or log the password.

## Credentials and sensitive settings

Local DPAPI-protected files are under `ops/azure`, including:

- `gateway.credential.local`
- `sql-admin.credential.local`
- `api-key.credential.local`
- `encryption-key.credential.local`
- `gateway-hash.local`

These are bound to the Windows user account. Do not print their contents, commit them, copy them into deployment artifacts, or expose App Service settings/connection strings in logs. The combined publishing script removes development config and the latest ZIP was checked to contain no credential files.

The encryption key must be preserved: losing it makes stored integration and AI provider secrets unreadable. AI provider API keys are encrypted in the application database and returned to the browser only as a `HasApiKey` flag.

## AI provider feature now in the app

- Sidebar **AI Providers** page: create, edit, test, set default, and delete provider entries.
- Provider setup accepts OpenAI-compatible and Azure OpenAI endpoints, model/deployment names, and API keys.
- The flow page's **Build with AI** modal lists configured providers and submits the selected provider ID to the API. The default provider is preselected when available.
- The old `AiProviderSettings` row is migrated in place: provider name and default marker are added, its existing encrypted key is retained, and the previous unique index is replaced by a provider-name index. SQL Server and SQLite paths are covered.
- `GET /api/ai/providers` returns one configured Azure OpenAI provider. Provider API keys remain encrypted and are never returned.
- The flow builder sends `max_completion_tokens` for GPT-5.x/o-series models; GPT-5.6 tool calls set `reasoning_effort` to `none`. Provider error details are limited, API-key-redacted, and displayed in the UI.
- The user’s Test connection succeeded after the 2026-10-02 deployment. The agent did not run a full flow-generation request.
- Azure GPT-5.6 API guidance: [Microsoft reasoning-model documentation](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning?tabs=python).


## Verification completed for the deployed package

Latest deployed ZIP: `artifacts/appservice/20261002-160950/fintegrator-appservice.zip`  
Azure deployment ID: `b1e7d381eff4446f98269d94b722a837`  
Azure reported deployment state: `Succeeded`.

After deployment:

- `/health/live`: HTTP 200, `Healthy`.
- `/health/ready`: HTTP 200, `Healthy` (database and Engine).
- Saved provider Test connection: HTTP 200, “Provider connection succeeded.”
- `dotnet test tests/SimpleIPaaS.Tests/SimpleIPaaS.Tests.csproj --no-restore`: 218 passed.
- `dotnet build src/SimpleIPaaS.Client/SimpleIPaaS.Client.csproj --no-restore`: succeeded with 0 warnings and 0 errors.

## Checkout caution

The repository contains many pre-existing uncommitted changes spanning dead-letter recovery, fan-out, AI flow building, Azure operations, and UI changes. Preserve them. Do not run `git reset --hard`, broad cleanup, or restore unrelated files as part of follow-up work.

In this environment the actual checkout is `C:\Users\FurqanIrfan\Documents\fintegrator-main`; the nested path `...\fintegrator-main\fintegrator-main` shown by some task context does not exist.