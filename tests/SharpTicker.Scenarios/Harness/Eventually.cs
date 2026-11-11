namespace SharpTicker.Scenarios.Harness;

/// <summary>What a failing eventual assertion reports about the scenario around it.</summary>
public interface IScenarioState
{
    string LogFolder { get; }

    /// <summary>The state of every container of the scenario, e.g. "node-0: running; node-1: exited (137)".</summary>
    Task<string> DescribeContainersAsync();
}

public sealed class EventuallyTimeoutException(string message) : Exception(message);

/// <summary>Polls a condition until it holds or a deadline passes.</summary>
public static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Polls <paramref name="condition"/> every second (or <paramref name="pollInterval"/>) until it returns true.
    /// On timeout throws with <paramref name="describe"/> (the last observed state), the log folder and the state of
    /// every container, so a node that crashed is named in the failure.
    /// </summary>
    public static async Task UntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        Func<string> describe,
        IScenarioState? state,
        TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        while (true)
        {
            try
            {
                if (await condition())
                    return;
                lastError = null;
            }
            catch (Exception ex)
            {
                // A node that is restarting answers with errors for a while; keep polling and report the last one.
                lastError = ex;
            }

            if (DateTime.UtcNow >= deadline)
                break;
            await Task.Delay(interval);
        }

        var message = $"Condition not met within {timeout.TotalSeconds:0.#} s: {describe()}";
        if (lastError != null)
            message += $"{Environment.NewLine}Last error: {lastError.GetType().Name}: {lastError.Message}";
        if (state != null)
        {
            string containers;
            try
            {
                containers = await state.DescribeContainersAsync();
            }
            catch (Exception ex)
            {
                containers = $"(could not read: {ex.Message})";
            }

            message += $"{Environment.NewLine}Containers: {containers}{Environment.NewLine}Logs: {state.LogFolder}";
        }

        throw new EventuallyTimeoutException(message);
    }
}
