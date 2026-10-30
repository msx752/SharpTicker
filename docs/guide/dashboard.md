# Dashboard

`SharpTicker.Dashboard` adds a web UI to watch, run, edit and cancel jobs, with live updates over SignalR.

```bash
dotnet add package SharpTicker.Dashboard
```

```csharp
using SharpTicker.Dashboard.DependencyInjection;

builder.Services.AddSharpTicker(options =>
{
    options.AddDashboard(dashboard =>
    {
        dashboard.WithBasicAuth("admin", builder.Configuration["Scheduler:DashboardPassword"]);
    });
});
```

The dashboard is served by the application at `/ticker/dashboard`. `UseSharpTicker()` maps it; no extra call is needed.

> [!WARNING]
> **The dashboard is public unless you choose an authentication mode.** Anyone who can reach it can run, change and cancel jobs and read job payloads. The default CORS policy also allows every origin with credentials. Always call one of the `With...Auth` methods below outside local development, and restrict CORS with `SetCorsPolicy` when the dashboard is reachable from other sites.

## Working with jobs

- **Time tickers:** add one, edit it, duplicate it with the copy icon, cancel one that is running, or use **Run again** to add a new row with the same function, request, retries and description that starts at once.
- **Cron tickers:** add, edit, duplicate, enable or disable, and **Run Now** to add an occurrence and run it immediately. Each cron has its occurrences and a graph of their results.
- **Retry intervals** are entered in seconds, one per retry, and are saved with the ticker.
- **A refused action** keeps its dialog open with what you typed, and a notification shows the reason, such as an invalid cron expression.

## Authentication

Pick one:

| Method | How users sign in |
|---|---|
| `WithBasicAuth(username, password)` | User name and password. |
| `WithApiKey(apiKey)` | An API key, sent as a Bearer token. |
| `WithHostAuthentication(policy)` | Your application's own ASP.NET Core authentication; optionally an authorization policy name. |
| `WithCustomAuth(validator)` | A `Func<string, bool>` that validates the credential. |
| `WithNoAuth()` | No sign-in. Only for local development. |

`WithSessionTimeout(minutes)` sets the session length (default 60 minutes).

`WithHostAuthentication` fits best when the application already has sign-in, because the dashboard then uses the same users, cookies or tokens and your authorization policies.

## Path and hosting

```csharp
options.AddDashboard(dashboard =>
{
    dashboard.SetBasePath("/admin/jobs");
    dashboard.WithHostAuthentication("SchedulerAdmins");
});
```

| Option | Purpose |
|---|---|
| `SetBasePath(path)` | URL path of the dashboard. Default `/ticker/dashboard`. |
| `SetCorsPolicy(policy => ...)` | Replaces the default CORS policy. |
| `SetBackendDomain(domain)` | Backend address, when the UI and the API are served from different hosts. |
| `SetGroupName(name)` | OpenAPI group name of the dashboard endpoints. |
| `PreDashboardMiddleware`, `PostDashboardMiddleware`, `CustomMiddleware` | Hooks to add your own middleware around the dashboard. |

`SetBasePath`, `SetCorsPolicy`, `SetBackendDomain` and `SetGroupName` return nothing, so call them as separate statements as shown above. The `With...` methods can be chained.

## Several nodes

Every instance that shares the store can serve the dashboard. It reads from the shared store, so every node shows the same jobs, and it lists the nodes that are currently running.
