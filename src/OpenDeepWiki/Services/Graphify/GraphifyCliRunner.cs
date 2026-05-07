using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.Services.Graphify;

public class GraphifyOptions
{
    public string Command { get; set; } = "graphify";

    public string PythonCommand { get; set; } = "python3";

    public string? Backend { get; set; }

    public string? Model { get; set; }

    public string? OpenAiBaseUrl { get; set; }

    public string? OpenAiApiKey { get; set; }

    public string? OutputRoot { get; set; }

    public int TimeoutMinutes { get; set; } = 60;

    public int MaxLogBytes { get; set; } = 200_000;
}

public sealed record GraphifyRunResult(
    string OutputRoot,
    string EntryFilePath,
    string GraphJsonPath,
    string ReportPath,
    string CommitId,
    string LogOutput);

public interface IGraphifyCliRunner
{
    Task<GraphifyRunResult> GenerateAsync(
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken = default);
}

public class GraphifyCliRunner : IGraphifyCliRunner
{
    private readonly GraphifyOptions _options;
    private readonly ILogger<GraphifyCliRunner> _logger;

    public GraphifyCliRunner(
        IOptions<GraphifyOptions> options,
        ILogger<GraphifyCliRunner> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GraphifyRunResult> GenerateAsync(
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace.WorkingDirectory) ||
            !Directory.Exists(workspace.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Repository workspace does not exist: {workspace.WorkingDirectory}");
        }

        var artifactRoot = ResolveArtifactRoot(workspace);
        Directory.CreateDirectory(artifactRoot);

        var graphifyOut = Path.Combine(artifactRoot, "graphify-out");
        var graphJsonPath = Path.Combine(graphifyOut, "graph.json");
        var entryFilePath = Path.Combine(graphifyOut, "graph.html");
        var reportPath = Path.Combine(graphifyOut, "GRAPH_REPORT.md");

        using var timeoutCts = CreateTimeoutTokenSource(cancellationToken);
        var token = timeoutCts.Token;

        var logBuilder = new StringBuilder();

        await RunGraphifyCommandAsync(
            workspace.WorkingDirectory,
            BuildExtractArguments(workspace.WorkingDirectory, artifactRoot),
            logBuilder,
            token);

        if (!File.Exists(graphJsonPath))
        {
            throw new FileNotFoundException("Graphify did not produce graph.json", graphJsonPath);
        }

        await RunGraphifyCommandAsync(
            workspace.WorkingDirectory,
            ["export", "html", "--graph", graphJsonPath],
            logBuilder,
            token);

        if (!File.Exists(entryFilePath))
        {
            throw new FileNotFoundException("Graphify did not produce graph.html", entryFilePath);
        }

        return new GraphifyRunResult(
            artifactRoot,
            entryFilePath,
            graphJsonPath,
            reportPath,
            workspace.CommitId,
            TrimLog(logBuilder.ToString()));
    }

    private string[] BuildExtractArguments(string workspacePath, string artifactRoot)
    {
        var args = new List<string> { "extract", workspacePath, "--out", artifactRoot };

        var backend = ResolveBackend();
        if (!string.IsNullOrWhiteSpace(backend))
        {
            args.Add("--backend");
            args.Add(backend);
        }

        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            args.Add("--model");
            args.Add(_options.Model);
        }

        return args.ToArray();
    }

    private string? ResolveBackend()
    {
        if (!string.IsNullOrWhiteSpace(_options.Backend))
        {
            return _options.Backend;
        }

        return HasOpenAiOverrides() ? "openai" : null;
    }

    private async Task RunGraphifyCommandAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        StringBuilder logBuilder,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateGraphifyStartInfo(workingDirectory, arguments);

        _logger.LogInformation(
            "Running Graphify command. Command: {Command}, Arguments: {Arguments}, WorkingDirectory: {WorkingDirectory}",
            startInfo.FileName,
            string.Join(" ", startInfo.ArgumentList.Select(MaskArgumentForLog)),
            workingDirectory);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start Graphify command: {startInfo.FileName}");
        }

        var stdoutTask = DrainStreamAsync(process.StandardOutput, logBuilder, cancellationToken);
        var stderrTask = DrainStreamAsync(process.StandardError, logBuilder, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Graphify command failed with exit code {process.ExitCode}: {TrimLog(logBuilder.ToString())}");
        }
    }

    private ProcessStartInfo CreateGraphifyStartInfo(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var usePythonLauncher = !string.IsNullOrWhiteSpace(_options.OpenAiBaseUrl);
        var startInfo = new ProcessStartInfo
        {
            FileName = usePythonLauncher ? _options.PythonCommand : _options.Command,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (usePythonLauncher)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(BuildOpenAiBaseUrlLauncher());
            startInfo.ArgumentList.Add(_options.OpenAiBaseUrl!);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(_options.OpenAiApiKey))
        {
            startInfo.Environment["OPENAI_API_KEY"] = _options.OpenAiApiKey;
        }

        return startInfo;
    }

    private bool HasOpenAiOverrides()
    {
        return !string.IsNullOrWhiteSpace(_options.OpenAiBaseUrl) ||
               !string.IsNullOrWhiteSpace(_options.OpenAiApiKey);
    }

    private static string BuildOpenAiBaseUrlLauncher()
    {
        return """
import sys
from graphify import llm
llm.BACKENDS["openai"]["base_url"] = sys.argv[1]
from graphify.__main__ import main
sys.argv = ["graphify"] + sys.argv[2:]
main()
""";
    }

    private async Task DrainStreamAsync(
        StreamReader reader,
        StringBuilder logBuilder,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (logBuilder.Length < _options.MaxLogBytes)
            {
                logBuilder.AppendLine(line);
            }
        }
    }

    private CancellationTokenSource CreateTimeoutTokenSource(CancellationToken cancellationToken)
    {
        var timeoutMinutes = _options.TimeoutMinutes <= 0 ? 60 : _options.TimeoutMinutes;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        return cts;
    }

    private string ResolveArtifactRoot(RepositoryWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(_options.OutputRoot))
        {
            return Path.Combine(
                Directory.GetParent(workspace.WorkingDirectory)?.FullName ?? workspace.WorkingDirectory,
                "graphify",
                SanitizePathComponent(workspace.BranchName));
        }

        return Path.Combine(
            _options.OutputRoot,
            SanitizePathComponent(workspace.Organization),
            SanitizePathComponent(workspace.RepositoryName),
            SanitizePathComponent(workspace.BranchName));
    }

    private string TrimLog(string value)
    {
        if (value.Length <= _options.MaxLogBytes)
        {
            return value;
        }

        return value[^_options.MaxLogBytes..];
    }

    private static string SanitizePathComponent(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(ch => invalidChars.Contains(ch) || ch is '/' or '\\' ? '_' : ch)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private static string MaskArgumentForLog(string argument)
    {
        return argument.Contains("key", StringComparison.OrdinalIgnoreCase) ? "***" : argument;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup after cancellation/timeout.
        }
    }
}
