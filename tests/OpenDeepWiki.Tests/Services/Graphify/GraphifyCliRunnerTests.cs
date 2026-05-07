using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
for arg in "$@"; do
  if [ "$previous" = "--graph" ]; then
    dir="$(dirname "$arg")"
    mkdir -p "$dir"
    echo '<html></html>' > "$dir/graph.html"
  fi
  previous="$arg"
done
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
            Assert.True(File.Exists(result.GraphJsonPath));
            Assert.True(File.Exists(result.EntryFilePath));
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
}
