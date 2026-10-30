using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.BackgroundServices;

internal class SharpTickerStartupValidator : IHostedService
{
    private readonly TickerExecutionContext _executionContext;
    private readonly SharpTickerInitializerHostedService _initializer;
    private readonly ILogger<SharpTickerStartupValidator> _logger;

    public SharpTickerStartupValidator(
        TickerExecutionContext executionContext,
        SharpTickerInitializerHostedService initializer,
        ILogger<SharpTickerStartupValidator> logger)
    {
        _executionContext = executionContext;
        _initializer = initializer;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_initializer.InitializationRequested)
        {
            const string message = "SharpTicker — UseSharpTicker() was not called. Call app.UseSharpTicker() before app.Run() to initialize the scheduler.";
            _logger.LogWarning(message);
            _executionContext.NotifyCoreAction?.Invoke(message, CoreNotifyActionType.NotifyHostExceptionMessage);
        }
        else if (TickerFunctionProvider.TickerFunctions.Count == 0)
        {
            const string message = "SharpTicker — No ticker functions registered. Ensure you have methods decorated with [TickerFunction].";
            _logger.LogWarning(message);
            _executionContext.NotifyCoreAction?.Invoke(message, CoreNotifyActionType.NotifyHostExceptionMessage);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
