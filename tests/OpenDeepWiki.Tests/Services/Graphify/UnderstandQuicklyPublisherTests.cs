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

    [Fact]
    public async Task PublishAsync_Disabled_NoOpAndNoStamp()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var graph = Path.Combine(tmp, "graph.json");
        await File.WriteAllTextAsync(graph, "{\"nodes\":[],\"links\":[]}");

        var options = Options.Create(new UnderstandQuicklyOptions { Enabled = false });
        var publisher = new UnderstandQuicklyPublisher(
            options, NullLogger<UnderstandQuicklyPublisher>.Instance);

        var result = await publisher.PublishAsync(MakeResult(graph), "owner/repo");

        Assert.False(result.MetadataStamped);
        Assert.False(result.Dispatched);

        // File untouched.
        var doc = JsonNode.Parse(await File.ReadAllTextAsync(graph));
        Assert.Null(doc!["metadata"]);

        Directory.Delete(tmp, recursive: true);
    }

    [Fact]
    public async Task PublishAsync_EnabledNoToken_StampsButDoesNotDispatch()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
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

        Directory.Delete(tmp, recursive: true);
    }

    [Fact]
    public async Task PublishAsync_EnabledWithToken_DispatchesAndReportsSuccess()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
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
        Assert.Equal("looptech-ai/demo",
            payload.GetProperty("client_payload").GetProperty("repo").GetString());

        Directory.Delete(tmp, recursive: true);
    }
}
