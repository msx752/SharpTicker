using System.Diagnostics;

namespace SharpTicker.Scenarios.Harness;

/// <summary>
/// Runs the docker command line. Used where the exact Docker behaviour matters (stop timeout, kill, inspect) and to
/// build the node image with the normal build cache.
/// </summary>
public static class DockerCli
{
    public static Task<(int ExitCode, string Output)> RunAsync(string arguments, TimeSpan? timeout = null)
        => RunProcessAsync("docker", arguments, timeout);

    /// <summary>Runs any command line tool (git, docker) and returns its exit code and combined output.</summary>
    public static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = new Process { StartInfo = info };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (-1, $"The {fileName} command could not be started: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (-1, $"{fileName} {arguments} did not finish within {(timeout ?? TimeSpan.FromMinutes(1)).TotalSeconds:0} s.");
        }

        lock (output)
            return (process.ExitCode, output.ToString());
    }

    public static async Task<string> RunCheckedAsync(string arguments, TimeSpan? timeout = null)
    {
        var (code, output) = await RunAsync(arguments, timeout);
        if (code != 0)
            throw new InvalidOperationException($"docker {arguments} failed ({code}): {output.Trim()}");
        return output;
    }
}
