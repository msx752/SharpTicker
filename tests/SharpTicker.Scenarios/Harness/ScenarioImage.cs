namespace SharpTicker.Scenarios.Harness;

/// <summary>
/// Builds the node image once per test run. When Docker is not running or the build fails, every scenario fails at
/// once with the same message instead of timing out one by one.
/// <para>
/// By default the image is built from the working tree. With <c>SCENARIO_SOURCE_REF</c> set (a commit, e.g. the commit
/// before a fix), the library sources come from that commit and the current scenario host is built against them: a
/// scenario must fail there and pass on the working tree, which shows that it tests the fix.
/// </para>
/// </summary>
public static class ScenarioImage
{
    private static readonly Lazy<Task<string>> Build = new(BuildAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task<string> GetAsync() => Build.Value;

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string ArtifactsFolder => Path.Combine(RepoRoot, "artifacts", "scenarios");

    /// <summary>The commit the library sources come from, or null for the working tree.</summary>
    public static string? SourceRef { get; } = Environment.GetEnvironmentVariable("SCENARIO_SOURCE_REF") is { Length: > 0 } r ? r : null;

    private static async Task<string> BuildAsync()
    {
        var (code, version) = await DockerCli.RunAsync("version --format {{.Server.Version}}", TimeSpan.FromSeconds(30));
        if (code != 0)
            throw new InvalidOperationException($"Docker is not running or not reachable; the scenario tests need it. {version.Trim()}");

        Directory.CreateDirectory(ArtifactsFolder);
        var (context, tag) = SourceRef == null
            ? (RepoRoot, "sharpticker-scenarios-host:dev")
            : await PrepareSourceAsync(SourceRef);

        var logPath = Path.Combine(ArtifactsFolder, $"image-build-{tag.Split(':')[1]}.log");
        var (buildCode, output) = await DockerCli.RunAsync(
            $"build -f \"{Path.Combine(context, "tests", "SharpTicker.Scenarios.Host", "Dockerfile")}\" -t {tag} \"{context}\"",
            TimeSpan.FromMinutes(20));
        await File.WriteAllTextAsync(logPath, output);
        if (buildCode != 0)
            throw new InvalidOperationException($"The scenario node image did not build; see {logPath}.");

        return tag;
    }

    /// <summary>
    /// A detached git worktree of <paramref name="sourceRef"/> under artifacts/scenarios, with the current scenario host,
    /// its Dockerfile and the .dockerignore copied in (the host is newer than the commit).
    /// </summary>
    private static async Task<(string Context, string Tag)> PrepareSourceAsync(string sourceRef)
    {
        var (revCode, rev) = await DockerCli.RunProcessAsync("git", $"-C \"{RepoRoot}\" rev-parse --verify \"{sourceRef}^{{commit}}\"");
        if (revCode != 0)
            throw new InvalidOperationException($"SCENARIO_SOURCE_REF '{sourceRef}' is not a commit: {rev.Trim()}");
        var sha = rev.Trim();
        var shortSha = sha[..8];

        var worktree = Path.Combine(ArtifactsFolder, $"source-{shortSha}");
        if (!Directory.Exists(worktree))
        {
            var (addCode, added) = await DockerCli.RunProcessAsync("git", $"-C \"{RepoRoot}\" worktree add --detach \"{worktree}\" {sha}", TimeSpan.FromMinutes(2));
            if (addCode != 0)
                throw new InvalidOperationException($"git worktree add for {sha} failed: {added.Trim()}");
        }

        var hostFrom = Path.Combine(RepoRoot, "tests", "SharpTicker.Scenarios.Host");
        var hostTo = Path.Combine(worktree, "tests", "SharpTicker.Scenarios.Host");
        if (Directory.Exists(hostTo))
            Directory.Delete(hostTo, recursive: true);
        CopyDirectory(hostFrom, hostTo);
        File.Copy(Path.Combine(RepoRoot, ".dockerignore"), Path.Combine(worktree, ".dockerignore"), overwrite: true);

        return (worktree, $"sharpticker-scenarios-host:{shortSha}");
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj")
                continue;
            CopyDirectory(dir, Path.Combine(to, name));
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SharpTicker.Scenarios.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("SharpTicker.Scenarios.slnx not found above " + AppContext.BaseDirectory);
    }
}
