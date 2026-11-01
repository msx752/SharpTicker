using System;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using SharpTicker.Dashboard.Authentication;

namespace SharpTicker.Dashboard;

public class DashboardOptionsBuilder
{
    internal string BasePath { get; set; } = "/ticker/dashboard";
    internal Action<CorsPolicyBuilder> CorsPolicyBuilder { get; set; }
    internal string BackendDomain { get; set; }
    internal string GroupName { get; set; }
    
    // Clean authentication system
    internal AuthConfig Auth { get; set; } = new();

    /// <summary>The registered visibility rule, if any.</summary>
    internal Type? VisibilityRuleType { get; set; }
    
    // Custom Middleware Integration
    public Action<IApplicationBuilder> CustomMiddleware { get; set; }
    public Action<IApplicationBuilder> PreDashboardMiddleware { get; set; }
    public Action<IApplicationBuilder> PostDashboardMiddleware { get; set; }
    
    internal JsonSerializerOptions DashboardJsonOptions { get; set; }

    /// <summary>Tracks whether dashboard middleware has been applied to prevent double registration.</summary>
    internal bool MiddlewareApplied { get; set; }
    
    public void SetCorsPolicy(Action<CorsPolicyBuilder> corsPolicyBuilder)
        => CorsPolicyBuilder = corsPolicyBuilder;
    
    public void SetBasePath(string basePath)
        => BasePath = basePath;
    
    public void SetBackendDomain(string backendDomain)
        => BackendDomain = backendDomain;

    /// <summary>Set OpenAPI group name for dashboard endpoints</summary>
    public void SetGroupName(string groupName)
        => GroupName = groupName;
    
    /// <summary>Configure no authentication (public dashboard)</summary>
    public DashboardOptionsBuilder WithNoAuth()
    {
        Auth.Mode = AuthMode.None;
        return this;
    }

    /// <summary>Enable Basic Authentication with username/password</summary>
    public DashboardOptionsBuilder WithBasicAuth(string username, string password)
    {
        Auth.Mode = AuthMode.Basic;
        Auth.BasicCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return this;
    }
    
    /// <summary>Enable API Key authentication (sent as Bearer token)</summary>
    public DashboardOptionsBuilder WithApiKey(string apiKey)
    {
        Auth.Mode = AuthMode.ApiKey;
        Auth.ApiKey = apiKey;
        return this;
    }
    
    /// <summary>Use the host application's existing authentication system</summary>
    /// <param name="policy">Optional authorization policy name to require (e.g., "AdminPolicy"). If null or empty, uses the default policy.</param>
    public DashboardOptionsBuilder WithHostAuthentication(string? policy = null)
    {
        Auth.Mode = AuthMode.Host;
        Auth.HostAuthorizationPolicy = policy;
        return this;
    }
    
    /// <summary>Configure custom authentication with validation function</summary>
    public DashboardOptionsBuilder WithCustomAuth(Func<string, bool> validator)
    {
        Auth.Mode = AuthMode.Custom;
        Auth.CustomValidator = validator;
        return this;
    }
    
    /// <summary>
    /// Limit what each signed-in user sees and can act on, see <see cref="ITickerVisibilityRule"/>. It needs
    /// <see cref="WithHostAuthentication"/>; the application fails to start when it is combined with another mode.
    /// </summary>
    // Internal until every endpoint and the live notifications apply the rule: a rule that is registered but only
    // applied to some of them would give a false sense of protection. It becomes public with the change that finishes that.
    internal DashboardOptionsBuilder WithVisibilityRule<TRule>() where TRule : class, ITickerVisibilityRule
    {
        VisibilityRuleType = typeof(TRule);
        return this;
    }

    /// <summary>Set session timeout in minutes</summary>
    public DashboardOptionsBuilder WithSessionTimeout(int minutes)
    {
        Auth.SessionTimeoutMinutes = minutes;
        return this;
    }
    
    /// <summary>Validate the authentication configuration</summary>
    internal void Validate()
    {
        Auth.Validate();

        if (VisibilityRuleType != null && Auth.Mode != AuthMode.Host)
            throw new InvalidOperationException(
                $"The visibility rule {VisibilityRuleType.Name} needs the signed-in user of the host application. " +
                $"Call WithHostAuthentication() on the dashboard, the current authentication mode is {Auth.Mode}.");
    }
}
