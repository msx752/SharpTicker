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

## Limiting what a user sees

By default every signed-in user sees every job. To limit that, for example one team per function prefix, register a rule. It needs `WithHostAuthentication`, because it needs the signed-in user; the application fails to start with another mode.

```csharp
public sealed class TeamRule : ITickerVisibilityRule
{
    public Expression<Func<TimeTickerEntity, bool>>? TimeTickers(ClaimsPrincipal user)
        => IsAdmin(user) ? null : t => t.Function.StartsWith(Prefix(user));

    public Expression<Func<CronTickerEntity, bool>>? CronTickers(ClaimsPrincipal user)
        => IsAdmin(user) ? null : c => c.Function.StartsWith(Prefix(user));

    public bool CanSeeFunction(ClaimsPrincipal user, string functionName)
        => IsAdmin(user) || functionName.StartsWith(Prefix(user), StringComparison.Ordinal);

    private static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole("admin");
    private static string Prefix(ClaimsPrincipal user) => user.FindFirst("team")?.Value + ".";
}

dashboard.WithHostAuthentication("SchedulerUsers").WithVisibilityRule<TeamRule>();
```

A predicate returning `null` allows everything. The rule is built from stored fields such as `Function` and `Description`, so it needs no schema change, and it is resolved for every request, so it can use your services.

| Where | What the user gets |
|---|---|
| Lists, pages, charts, status counts, statistics | Only the tickers the rule allows. |
| A single ticker or cron (occurrences, request data, update, toggle, run, delete, cancel) | The answer for a ticker that does not exist. |
| Adding or updating a ticker | Refused when its function (or the function of a child) is not allowed by `CanSeeFunction`. |
| Registered functions | Only the allowed ones. |
| Live notifications | Sent only to the users who may see the ticker; the occurrence updates of a cron only reach users who may see that cron. |

Notes:

- The predicates run in the database on EF Core and MongoDB, and in memory on Redis and the in-memory store. For live notifications they always run in memory, so write them so that both give the same answer (compare text with an explicit ordinal comparison).
- The scheduler controls (stop, start, restart), the host status and the node list are not tied to a ticker and are not limited by the rule. Protect them with an authorization policy if only some users may use them.
- A notification that only carries an id (a ticker was deleted, added or cancelled) goes to every connection.

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
