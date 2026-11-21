# SharpTicker.Dashboard

A web dashboard for [SharpTicker](https://github.com/msx752/SharpTicker), a background job scheduler for .NET. It lists time and cron jobs, shows their runs and statuses with live updates over SignalR, and lets you add, run, edit and cancel jobs.

## Install

```bash
dotnet add package SharpTicker.Dashboard
```

## Setup

```csharp
using SharpTicker.Dashboard.DependencyInjection;
using SharpTicker.DependencyInjection;

builder.Services.AddSharpTicker(options =>
{
    options.AddDashboard(dashboard =>
    {
        dashboard.WithBasicAuth("admin", builder.Configuration["Scheduler:DashboardPassword"]);
    });
});

var app = builder.Build();
app.UseSharpTicker(); // also maps the dashboard, at /ticker/dashboard by default
app.Run();
```

> **Warning: a dashboard without authentication is open to anyone who can reach it.** Without an authentication mode, or with `WithNoAuth()`, anyone who can reach the dashboard URL can run, change and cancel jobs and read job payloads. The default CORS policy also allows every origin with credentials. Outside local development, choose one of the authentication methods below, and restrict CORS with `SetCorsPolicy` when other sites can reach the dashboard.

## Authentication

| Method | How users sign in |
|---|---|
| `WithBasicAuth(username, password)` | User name and password. |
| `WithApiKey(apiKey)` | An API key, sent as a Bearer token. |
| `WithHostAuthentication(policy)` | Your application's own ASP.NET Core authentication, optionally with an authorization policy name. |
| `WithCustomAuth(validator)` | A `Func<string, bool>` that validates the credential. |
| `WithNoAuth()` | No sign-in. Only for local development. |

`WithSessionTimeout(minutes)` sets the session length (default 60 minutes). `SetBasePath(path)` changes the dashboard path.

## Documentation

[Dashboard guide](https://msx752.github.io/SharpTicker/guide/dashboard.html): authentication, limiting what each user sees, path and hosting, several nodes.
