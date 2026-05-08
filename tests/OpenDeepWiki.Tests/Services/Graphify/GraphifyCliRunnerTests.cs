using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using OpenDeepWiki.Services.Graphify;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Graphify;

public class GraphifyCliRunnerTests
{
    [Fact]
    public async Task GenerateAsync_WithOpenAiOverrides_UsesPythonLauncherAndPassesApiKey()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "workspace");
        var outputRoot = Path.Combine(tempRoot, "artifacts");
        var captureFile = Path.Combine(tempRoot, "capture.txt");
        Directory.CreateDirectory(workspace);

        var fakePython = Path.Combine(tempRoot, "fake-python.sh");
        await File.WriteAllTextAsync(fakePython, """
#!/usr/bin/env bash
{
  echo "CALL"
  for arg in "$@"; do
    echo "$arg"
  done
  echo "ENV_OPENAI_API_KEY=$OPENAI_API_KEY"
  echo "ENV_GRAPHIFY_OUT=$GRAPHIFY_OUT"
} >> "$GRAPHIFY_CAPTURE_FILE"

out=""
previous=""
for arg in "$@"; do
  if [ "$previous" = "--out" ]; then
    out="$arg"
  fi
  previous="$arg"
done

if [ -n "$out" ]; then
  mkdir -p "$out/graphify-out"
  echo '{"nodes":[],"edges":[]}' > "$out/graphify-out/graph.json"
  echo '{"communities":{"0":["a"],"1":["b"]}}' > "$out/graphify-out/.graphify_analysis.json"
  echo '# Report' > "$out/graphify-out/GRAPH_REPORT.md"
fi

previous=""
is_cluster_only=""
for arg in "$@"; do
  if [ "$arg" = "cluster-only" ]; then
    is_cluster_only="1"
  fi
  if [ "$previous" = "--graph" ]; then
    dir="$(dirname "$arg")"
    mkdir -p "$dir"
    echo '<html></html>' > "$dir/graph.html"
  fi
  previous="$arg"
done

if [ -n "$is_cluster_only" ]; then
  echo '# Report from cluster-only' > "$dir/GRAPH_REPORT.md"
fi
""");
        File.SetUnixFileMode(
            fakePython,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            Environment.SetEnvironmentVariable("GRAPHIFY_CAPTURE_FILE", captureFile);
            var runner = new GraphifyCliRunner(
                Options.Create(new GraphifyOptions
                {
                    PythonCommand = fakePython,
                    OutputRoot = outputRoot,
                    OpenAiBaseUrl = "https://openai-compatible.example/v1",
                    OpenAiApiKey = "test-openai-key",
                    EnableLlmCommunityLabels = false,
                    TimeoutMinutes = 1
                }),
                NullLogger<GraphifyCliRunner>.Instance);

            var result = await runner.GenerateAsync(new RepositoryWorkspace
            {
                WorkingDirectory = workspace,
                Organization = "owner",
                RepositoryName = "repo",
                BranchName = "main",
                CommitId = "commit"
            });

            var capture = await File.ReadAllTextAsync(captureFile);
            Assert.Contains("https://openai-compatible.example/v1", capture);
            Assert.Contains("ENV_OPENAI_API_KEY=test-openai-key", capture);
            Assert.Contains($"ENV_GRAPHIFY_OUT={Path.Combine(result.OutputRoot, "graphify-out")}", capture);
            Assert.Contains("--backend", capture);
            Assert.Contains("openai", capture);
            Assert.Contains("cluster-only", capture);
            Assert.True(File.Exists(result.GraphJsonPath));
            Assert.True(File.Exists(result.EntryFilePath));
            Assert.True(File.Exists(result.ReportPath));
            var labelsPath = Path.Combine(result.OutputRoot, "graphify-out", ".graphify_labels.json");
            var labels = await File.ReadAllTextAsync(labelsPath);
            Assert.Contains("\"0\": \"Community 0\"", labels);
            Assert.Contains("\"1\": \"Community 1\"", labels);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRAPHIFY_CAPTURE_FILE", null);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    public static IEnumerable<object[]> LlmCommunityLabelResponseFormats()
    {
        yield return
        [
            """
            {"choices":[{"message":{"content":"{\"labels\":{\"0\":\"Tasklet Workflow\",\"1\":\"Arc Client\"}}"}}]}
            """
        ];
        yield return
        [
            """
            {"choices":[{"text":"{\"labels\":{\"0\":\"Tasklet Workflow\",\"1\":\"Arc Client\"}}"}]}
            """
        ];
        yield return
        [
            """
            {"content":"{\"labels\":{\"0\":\"Tasklet Workflow\",\"1\":\"Arc Client\"}}"}
            """
        ];
        yield return
        [
            """
            {"labels":{"0":"Tasklet Workflow","1":"Arc Client"}}
            """
        ];
        yield return
        [
            """
            {"content":"The labels are:\n```json\n{\"labels\":{\"0\":\"Tasklet Workflow\",\"1\":\"Arc Client\"}}\n```"}
            """
        ];
    }

    [Theory]
    [MemberData(nameof(LlmCommunityLabelResponseFormats))]
    public async Task GenerateAsync_WithOpenAiOverrides_UsesLlmCommunityLabels(string responseJson)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "workspace");
        var outputRoot = Path.Combine(tempRoot, "artifacts");
        Directory.CreateDirectory(workspace);

        var fakePython = Path.Combine(tempRoot, "fake-python.sh");
        await File.WriteAllTextAsync(fakePython, """
#!/usr/bin/env bash
out=""
previous=""
for arg in "$@"; do
  if [ "$previous" = "--out" ]; then
    out="$arg"
  fi
  previous="$arg"
done

if [ -n "$out" ]; then
  mkdir -p "$out/graphify-out"
  cat > "$out/graphify-out/graph.json" <<'JSON'
{"nodes":[{"id":"tasklet_node","label":"ClaudeCodeTasklet","source_file":"app/src/tasklet.ts","file_type":"code"},{"id":"arc_node","label":"ArcClient","source_file":"app/src/arc/client.ts","file_type":"code"}],"links":[{"source":"tasklet_node","target":"arc_node"}]}
JSON
  echo '{"communities":{"0":["tasklet_node"],"1":["arc_node"]}}' > "$out/graphify-out/.graphify_analysis.json"
  echo '{"0":"Community 0","1":"Community 1"}' > "$out/graphify-out/.graphify_labels.json"
fi

previous=""
is_cluster_only=""
for arg in "$@"; do
  if [ "$arg" = "cluster-only" ]; then
    is_cluster_only="1"
  fi
  if [ "$previous" = "--graph" ]; then
    dir="$(dirname "$arg")"
    mkdir -p "$dir"
    echo '<html></html>' > "$dir/graph.html"
  fi
  previous="$arg"
done

if [ -n "$is_cluster_only" ]; then
  echo '# Report from cluster-only' > "$dir/GRAPH_REPORT.md"
fi
""");
        File.SetUnixFileMode(
            fakePython,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(responseJson));
            var runner = new GraphifyCliRunner(
                Options.Create(new GraphifyOptions
                {
                    PythonCommand = fakePython,
                    OutputRoot = outputRoot,
                    OpenAiBaseUrl = "https://openai-compatible.example/v1",
                    OpenAiApiKey = "test-openai-key",
                    Model = "test-model",
                    TimeoutMinutes = 1
                }),
                NullLogger<GraphifyCliRunner>.Instance,
                httpClient);

            var result = await runner.GenerateAsync(new RepositoryWorkspace
            {
                WorkingDirectory = workspace,
                Organization = "owner",
                RepositoryName = "repo",
                BranchName = "main",
                CommitId = "commit"
            });

            var labels = await File.ReadAllTextAsync(
                Path.Combine(result.OutputRoot, "graphify-out", ".graphify_labels.json"));
            Assert.Contains("\"0\": \"Tasklet Workflow\"", labels);
            Assert.Contains("\"1\": \"Arc Client\"", labels);
            Assert.True(File.Exists(result.ReportPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GenerateAsync_WhenLlmResponseCannotBeParsed_FallsBackToHeuristicLabels()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "workspace");
        var outputRoot = Path.Combine(tempRoot, "artifacts");
        Directory.CreateDirectory(workspace);

        var fakePython = Path.Combine(tempRoot, "fake-python.sh");
        await File.WriteAllTextAsync(fakePython, """
#!/usr/bin/env bash
out=""
previous=""
for arg in "$@"; do
  if [ "$previous" = "--out" ]; then
    out="$arg"
  fi
  previous="$arg"
done

if [ -n "$out" ]; then
  mkdir -p "$out/graphify-out"
  cat > "$out/graphify-out/graph.json" <<'JSON'
{"nodes":[{"id":"tasklet_node","label":"ClaudeCodeTasklet","source_file":"app/src/tasklet.ts","file_type":"code"},{"id":"arc_node","label":"ArcClient","source_file":"app/src/arc/client.ts","file_type":"code"}],"links":[{"source":"tasklet_node","target":"arc_node"}]}
JSON
  echo '{"communities":{"0":["tasklet_node"],"1":["arc_node"]}}' > "$out/graphify-out/.graphify_analysis.json"
fi

previous=""
is_cluster_only=""
for arg in "$@"; do
  if [ "$arg" = "cluster-only" ]; then
    is_cluster_only="1"
  fi
  if [ "$previous" = "--graph" ]; then
    dir="$(dirname "$arg")"
    mkdir -p "$dir"
    echo '<html></html>' > "$dir/graph.html"
  fi
  previous="$arg"
done

if [ -n "$is_cluster_only" ]; then
  echo '# Report from cluster-only' > "$dir/GRAPH_REPORT.md"
fi
""");
        File.SetUnixFileMode(
            fakePython,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler("""{"unexpected":true}"""));
            var runner = new GraphifyCliRunner(
                Options.Create(new GraphifyOptions
                {
                    PythonCommand = fakePython,
                    OutputRoot = outputRoot,
                    OpenAiBaseUrl = "https://openai-compatible.example/v1",
                    OpenAiApiKey = "test-openai-key",
                    Model = "test-model",
                    TimeoutMinutes = 1
                }),
                NullLogger<GraphifyCliRunner>.Instance,
                httpClient);

            var result = await runner.GenerateAsync(new RepositoryWorkspace
            {
                WorkingDirectory = workspace,
                Organization = "owner",
                RepositoryName = "repo",
                BranchName = "main",
                CommitId = "commit"
            });

            var labels = await File.ReadAllTextAsync(
                Path.Combine(result.OutputRoot, "graphify-out", ".graphify_labels.json"));
            Assert.Contains("ClaudeCodeTasklet", labels);
            Assert.Contains("ArcClient", labels);
            Assert.True(File.Exists(result.ReportPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class StubHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://openai-compatible.example/v1/chat/completions", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-openai-key", request.Headers.Authorization?.Parameter);

            var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("test-model", requestBody);
            Assert.Contains("ClaudeCodeTasklet", requestBody);
            Assert.Contains("ArcClient", requestBody);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
