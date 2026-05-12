using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using OpenDeepWiki.Services.Graphify;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Graphify;

public class UnderstandQuicklyPublisherTests
{
    private static GraphifyRunResult MakeResult(string graphPath, string commit = "deadbeef") =>
        new(
            OutputRoot: Path.GetDirectoryName(graphPath)!,
            EntryFilePath: Path.Combine(Path.GetDirectoryName(graphPath)!, "graph.html"),
            GraphJsonPath: graphPath,
            ReportPath: Path.Combine(Path.GetDirectoryName(graphPath)!, "GRAPH_REPORT.md"),
            CommitId: commit,
            LogOutput: string.Empty);

    private static string MakeTempDir()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    private static void SafeDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task PublishAsync_Disabled_NoOpAndNoStamp()
    {
        var tmp = MakeTempDir();
        try
        {
            var graph = Path.Combine(tmp, "graph.json");
            await File.WriteAllTextAsync(graph, "{\"nodes\":[],\"links\":[]}");

            var options = Options.Create(new UnderstandQuicklyOptions { Enabled = false });
            var publisher = new UnderstandQuicklyPublisher(
                options, NullLogger<UnderstandQuicklyPublisher>.Instance, new HttpClient());

            var result = await publisher.PublishAsync(MakeResult(graph), "owner/repo");

            Assert.False(result.MetadataStamped);
            Assert.False(result.Dispatched);

            // File untouched.
            var doc = JsonNode.Parse(await File.ReadAllTextAsync(graph));
            Assert.Null(doc!["metadata"]);
        }
        finally
        {
            SafeDelete(tmp);
        }
    }

    [Fact]
    public async Task PublishAsync_EnabledNoToken_StampsButDoesNotDispatch()
    {
        var tmp = MakeTempDir();
        try
        {
            var graph = Path.Combine(tmp, "graph.json");
            await File.WriteAllTextAsync(graph, "{\"nodes\":[],\"links\":[]}");

            var options = Options.Create(new UnderstandQuicklyOptions { Enabled = true });
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("dispatch should not be called"));
            var publisher = new UnderstandQuicklyPublisher(
                options, NullLogger<UnderstandQuicklyPublisher>.Instance,
                new HttpClient(handler.Object));

            var result = await publisher.PublishAsync(MakeResult(graph, "abc123"), "owner/repo");

            Assert.True(result.MetadataStamped);
            Assert.False(result.Dispatched);
            Assert.Null(result.Error);

            var doc = JsonNode.Parse(await File.ReadAllTextAsync(graph))!.AsObject();
            var md = doc["metadata"]!.AsObject();
            Assert.Equal("opendeepwiki", md["tool"]!.GetValue<string>());
            Assert.Equal("abc123", md["commit"]!.GetValue<string>());
            Assert.NotNull(md["tool_version"]);
            Assert.EndsWith("Z", md["generated_at"]!.GetValue<string>());
        }
        finally
        {
            SafeDelete(tmp);
        }
    }

    [Fact]
    public async Task PublishAsync_EnabledWithToken_DispatchesAndReportsSuccess()
    {
        var tmp = MakeTempDir();
        try
        {
            var graph = Path.Combine(tmp, "graph.json");
            await File.WriteAllTextAsync(graph, "{\"nodes\":[],\"links\":[]}");

            HttpRequestMessage? captured = null;
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NoContent));

            var options = Options.Create(new UnderstandQuicklyOptions
            {
                Enabled = true,
                Token = "ghp_fake",
            });
            var publisher = new UnderstandQuicklyPublisher(
                options, NullLogger<UnderstandQuicklyPublisher>.Instance,
                new HttpClient(handler.Object));

            var result = await publisher.PublishAsync(MakeResult(graph), "looptech-ai/demo");

            Assert.True(result.MetadataStamped);
            Assert.True(result.Dispatched);
            Assert.NotNull(captured);
            Assert.Equal("https://api.github.com/repos/looptech-ai/understand-quickly/dispatches",
                captured!.RequestUri!.ToString());
            Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
            var body = await captured.Content!.ReadAsStringAsync();
            var payload = JsonDocument.Parse(body).RootElement;
            Assert.Equal("uq-publish", payload.GetProperty("event_type").GetString());
            var clientPayload = payload.GetProperty("client_payload");
            Assert.Equal("looptech-ai/demo", clientPayload.GetProperty("repo").GetString());
            // graph_path must be repo-relative — never an absolute server path.
            var graphPath = clientPayload.GetProperty("graph_path").GetString()!;
            Assert.False(Path.IsPathRooted(graphPath),
                $"graph_path '{graphPath}' must be repo-relative");
            Assert.Equal("graph.json", graphPath);
        }
        finally
        {
            SafeDelete(tmp);
        }
    }

    [Fact]
    public void ToRepoRelativePath_StripsOutputRoot()
    {
        // Use platform-real paths so this test passes on Windows and *nix.
        var root = Path.Combine(Path.GetTempPath(), "uq-rel-test");
        var nested = Path.Combine(root, "graphify-out", "graph.json");
        var direct = Path.Combine(root, "graph.json");

        Assert.Equal("graphify-out/graph.json",
            UnderstandQuicklyPublisher.ToRepoRelativePath(root, nested));
        Assert.Equal("graph.json",
            UnderstandQuicklyPublisher.ToRepoRelativePath(root, direct));
        // Null/empty OutputRoot -> file name fallback (no leaked server path).
        Assert.Equal("graph.json",
            UnderstandQuicklyPublisher.ToRepoRelativePath(null, nested));
        Assert.Equal("graph.json",
            UnderstandQuicklyPublisher.ToRepoRelativePath(string.Empty, nested));
    }
}
