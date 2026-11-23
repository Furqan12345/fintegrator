# Azure App Service deployment

Current live deployment: the UI, API, and continuous Engine WebJob run together in the Windows B1 App Service plan fintegrator-plan in Central US. Azure SQL is shared. The separate Engine deployment package is prepared, but its Azure host has not been provisioned yet.

## Live resources

- UI, API, and Engine: https://fintegrator-web-e07356e8.azurewebsites.net
- Resource group: rg-fintegrator
- App Service plan: fintegrator-plan (Windows B1, one instance, Always On)
- SQL server: fintegrator-e07356e8.database.windows.net
- Database: fintegrator (Basic, 5 DTU, 2 GB)

The UI/API app now owns its accounts: the first visitor completes `/setup`, creates the Admin account, and enrolls TOTP two-factor authentication. Admins then invite Users and Viewers from **Users**. There is no shared Basic-auth or bootstrap API key anymore; the legacy `gateway.credential.local`, `api-key.credential.local`, and `Bootstrap__ApiKey` App Service setting can be removed after the first Admin is created.

Required App Service settings:

- `Auth__Jwt__SigningKey` — at least 32 random characters; rotating it signs everyone out.
- `App__PublicBaseUrl` — the public HTTPS origin, used in invite and password-reset links.
- `Email__Smtp__Host`, `Email__Smtp__Port`, `Email__Smtp__UseSsl`, `Email__Smtp__Username`, `Email__Smtp__Password`, `Email__Smtp__FromAddress` — required for self-service reset and invitation email. Without SMTP, Admins can copy the invite/reset link shown in the UI.
- `Outbound__AllowPrivateNetworks` — leave `false` unless flows must call internal/private-network hosts.

SQL admin and the encryption key remain in DPAPI-protected `*.credential.local` files. Do not commit them or the SQL connection string. Losing the encryption key makes encrypted integration secrets unreadable; rotating it requires a re-encrypt migration first.

## Current deployment

Use the combined package for the existing live app. It keeps the current Engine WebJob running:

~~~powershell
$zip = & 'ops/azure/publish-appservice.ps1'
$webName = (Get-Content -LiteralPath 'ops/azure/webapp-name.local' -Raw).Trim()
az webapp deploy -g rg-fintegrator -n $webName --subscription 2c506250-e4f8-4302-b51b-b39d0220f7ab --src-path $zip --type zip --timeout 600000
~~~

## Prepared split packages

publish-appservice-api-only.ps1 creates the UI/API-only ZIP. Do not deploy it to the live app until a separate Engine host is ready; it intentionally excludes the Engine WebJob.

publish-engine-webjob.ps1 creates the standalone Engine WebJob ZIP. The job includes settings.job with is_singleton enabled to prevent duplicate schedulers after scale-out.

Azure currently allows one B1 worker in Central US and that slot is already used. App Service plan creation is also returning Azure error 51025/429. North Central US has no B1 quota in this subscription. No separate Engine plan or app has been created. A higher-cost Premium P0v4 plan is available, but at Microsoft’s listed $99.28/month it would use the $200 trial credit much faster.

## Cost and limits

The UI/API compute and Engine compute are not isolated in the current deployment. The Basic SQL database also remains a shared 5-DTU bottleneck. Check Azure Cost Management and set a budget against the trial credit. Accounts, roles, and TOTP are suitable for an internal team. Fusetor is single-deployment by design: one deployment serves one customer. Do not expose one deployment to multiple customers; give each customer its own deployment and database.
