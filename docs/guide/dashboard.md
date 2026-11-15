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
- **Ticker types of your own** (`AddSharpTicker<TTimeTicker, TCronTicker>`): the time ticker lists, the occurrence lists of a cron, and adding and updating a ticker work with them. The public properties your types add are shown in the dashboard's answers and are read from the bodies of add and update; a property marked `[JsonIgnore]` is kept out of both. An edit in the dashboard keeps the stored value of each property of your own type that its body does not send. The in-memory store does not keep these properties. Up to 1.5.2 these requests failed with a `NotSupportedException` ([#95](https://github.com/msx752/SharpTicker/issues/95)). The dashboard reads and writes such types through reflection. In a trimmed or AOT application, where the serializer has reflection switched off, it does not, and these requests fail as they did up to 1.5.2.

The dashboard uses three words. A **function** is the code you registered. A **ticker** is a scheduled entry that points at a function: a time ticker runs once, a cron ticker repeats. A **job** is one run of a ticker, with its status, and the actions that stop or count runs (cancel, statistics) say job.

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
- The scheduler controls (stop, start, restart), the host status and the node list are not tied to a ticker and are not limited by the rule, so every signed-in user can stop the scheduler. The authorization policy of `WithHostAuthentication` covers the whole dashboard, not single endpoints. To limit these paths (`/api/ticker-host/...`), add your own check with `PreDashboardMiddleware`.
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

Every instance that shares the store can serve the dashboard. It reads from the shared store, so every node shows the same jobs.

The **Lock holders** panel (called Machines up to 1.5.2) lists the node identifiers that locked the jobs in the store, each with the number of those jobs and how long ago it last locked one, latest first. A job keeps the name after it ended, so the name of a node that stopped or was replaced stays in the list as long as its jobs are stored. The one marked Current is the node that serves the dashboard.

- With Redis, each name also shows whether that node has a heartbeat: `heartbeat` when it wrote one within the heartbeat TTL (`NodeHeartbeatInterval` plus 20 seconds), `no heartbeat` otherwise. An instance with the background services off (`DisableBackgroundServices`), one that serves only the dashboard for example, writes its heartbeat too, so `heartbeat` says that the instance runs, not that it runs jobs.
- The EF Core, MongoDB and in-memory stores keep no record of which nodes run, so there the panel shows the names and their latest lock only. With the EF Core lease on, a node renews the lock of the jobs it runs, so a name that runs a long job keeps a recent latest lock.

Up to 1.5.2 the panel was ordered by the number of jobs and showed neither the latest lock nor the heartbeat. A stable `NodeIdentifier` keeps the list short; see [Operations](operations.md#scheduler-options).
