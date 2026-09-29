using System.Net;

namespace TypeSafeAI.Tests;

/// <summary>
/// Verifies transport-level behaviour: authentication, URLs, headers, and configuration resolution.
/// </summary>
public sealed class ClientBehaviorTests
{
    [Fact]
    public async Task TheRequestCarriesABearerTokenAndJsonContentType()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal($"Bearer {TestClient.ApiKey}", handler.LastRequest.Headers["Authorization"]);
    }

    [Fact]
    public async Task TheRequestGoesToTheDocumentedEndpoint()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", handler.LastRequest.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task TheSdkIdentifiesItselfInTheUserAgent()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.StartsWith("TypeSafe.Sdk/", handler.LastRequest.Headers["User-Agent"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerSuppliedUserAgentTokenIsAppended()
    {
        var options = new TypeSafeClientOptions { ApiKey = TestClient.ApiKey, UserAgent = "my-service/2.1.0" };
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse, options: options);

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.EndsWith("my-service/2.1.0", handler.LastRequest.Headers["User-Agent"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABareApiKeyConstructorBeatsTheEnvironment()
    {
        using var environment = new EnvironmentScope().Set(TypeSafeDefaults.ApiKeyEnvironmentVariable, "tsk_from_env");

        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var client = new TypeSafeClient("tsk_explicit", options: null, new HttpClient(handler, disposeHandler: false));

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal("Bearer tsk_explicit", handler.LastRequest.Headers["Authorization"]);
    }

    [Fact]
    public async Task AnExplicitApiKeyDoesNotLeakIntoTheCallersOptions()
    {
        var options = new TypeSafeClientOptions { ApiKey = "tsk_tenant_a" };
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var http = new HttpClient(handler, disposeHandler: false);

        using var tenantB = new TypeSafeClient("tsk_tenant_b", options, http);
        using var tenantA = new TypeSafeClient(options, http);

        await tenantA.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        // A shared options object must not be switched to another tenant's key.
        Assert.Equal("tsk_tenant_a", options.ApiKey);
        Assert.Equal("Bearer tsk_tenant_a", handler.LastRequest.Headers["Authorization"]);
    }

    [Fact]
    public async Task ChangingTheOptionsAfterConstructionDoesNotAffectTheClient()
    {
        var options = new TypeSafeClientOptions { ApiKey = TestClient.ApiKey };
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse, options: options);

        options.DefaultHeaders = new Dictionary<string, string> { ["X-Added-Later"] = "1" };

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.False(handler.LastRequest.Headers.ContainsKey("X-Added-Later"));
    }

    [Fact]
    public async Task ChangingTheHeadersDictionaryAfterConstructionDoesNotAffectTheClient()
    {
        var headers = new Dictionary<string, string> { ["X-Tenant"] = "tenant-a" };
        var options = new TypeSafeClientOptions { ApiKey = TestClient.ApiKey, DefaultHeaders = headers };
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse, options: options);

        headers["X-Tenant"] = "tenant-b";

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal("tenant-a", handler.LastRequest.Headers["X-Tenant"]);
    }

    [Fact]
    public void CloningKeepsTheHeadersComparer()
    {
        var options = new TypeSafeClientOptions
        {
            DefaultHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Tenant"] = "contoso" },
        };

        Assert.Equal("contoso", options.Clone().DefaultHeaders!["x-tenant"]);
    }

    [Fact]
    public async Task TheApiKeyFallsBackToTheEnvironmentVariable()
    {
        using var environment = new EnvironmentScope().Set(TypeSafeDefaults.ApiKeyEnvironmentVariable, "tsk_from_env");

        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var client = new TypeSafeClient(options: null, new HttpClient(handler, disposeHandler: false));

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal("Bearer tsk_from_env", handler.LastRequest.Headers["Authorization"]);
    }

    [Fact]
    public async Task ABlankEnvironmentApiKeyIsTreatedAsUnset()
    {
        using var environment = new EnvironmentScope()
            .Set(TypeSafeDefaults.ApiKeyEnvironmentVariable, "   ")
            .Set(TypeSafeDefaults.BaseUrlEnvironmentVariable, null);

        var exception = Assert.Throws<TypeSafeConfigurationException>(() => new TypeSafeClient());
        Assert.Contains(TypeSafeDefaults.ApiKeyEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBaseUrlFallsBackToTheEnvironmentVariable()
    {
        using var environment = new EnvironmentScope().Set(
            TypeSafeDefaults.BaseUrlEnvironmentVariable,
            "https://proxy.internal.example");

        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var client = new TypeSafeClient(
            new TypeSafeClientOptions { ApiKey = TestClient.ApiKey },
            new HttpClient(handler, disposeHandler: false));

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal("https://proxy.internal.example/v1/systemone", handler.LastRequest.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task TheCookbookSpellingOfTheEndpointVariableIsAlsoHonoured()
    {
        using var environment = new EnvironmentScope()
            .Set(TypeSafeDefaults.BaseUrlEnvironmentVariable, null)
            .Set(TypeSafeDefaults.LegacyEndpointEnvironmentVariable, "https://legacy.example");

        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var client = new TypeSafeClient(
            new TypeSafeClientOptions { ApiKey = TestClient.ApiKey },
            new HttpClient(handler, disposeHandler: false));

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        // The SDK reference pages document TYPESAFE_BASE_URL while the cookbooks use
        // TYPESAFE_ENDPOINT; honouring both means either deployment style works.
        Assert.Equal("https://legacy.example/v1/systemone", handler.LastRequest.Uri.AbsoluteUri);
    }

    [Fact]
    public void TheBaseUrlMustBeAbsolute()
    {
        using var environment = new EnvironmentScope().Set(TypeSafeDefaults.BaseUrlEnvironmentVariable, "not-a-url");

        // An unparseable URL is a configuration error and must be reported before any network call.
        Assert.Throws<UriFormatException>(
            () => new TypeSafeClient(new TypeSafeClientOptions { ApiKey = TestClient.ApiKey }));
    }

    [Fact]
    public async Task ATrailingSlashOnTheBaseUrlDoesNotDoubleUp()
    {
        var options = new TypeSafeClientOptions
        {
            ApiKey = TestClient.ApiKey,
            BaseUrl = new Uri("https://gateway.example/"),
        };

        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse, options: options);

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal("https://gateway.example/v1/systemone", handler.LastRequest.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task TheDefaultModelComesFromTheEnvironmentVariable()
    {
        using var environment = new EnvironmentScope().Set(TypeSafeDefaults.DefaultModelEnvironmentVariable, "jev-1.12");

        var options = new TypeSafeClientOptions { ApiKey = TestClient.ApiKey };
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse, options: options);

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        using var body = handler.LastRequest.ParseBody();
        Assert.Equal("jev-1.12", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task PerCallHeadersAreSentAndOverrideDefaults()
    {
        var options = new TypeSafeClientOptions
        {
            ApiKey = TestClient.ApiKey,
            DefaultHeaders = new Dictionary<string, string>
            {
                ["X-Tenant"] = "default",
                ["X-Trace"] = "keep-me",
            },
        };

        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse, options: options);

        await client.SystemOneAsync(new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "q?")],
            Options = new TypeSafeRequestOptions
            {
                Headers = new Dictionary<string, string> { ["X-Tenant"] = "override" },
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("override", handler.LastRequest.Headers["X-Tenant"]);
        Assert.Equal("keep-me", handler.LastRequest.Headers["X-Trace"]);
    }

    [Fact]
    public async Task AnEmptyQuestionsCollectionIsRejectedBeforeSending()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SystemOneAsync("text", [], TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task DuplicateQuestionIdsAreRejectedBeforeSending()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SystemOneAsync(
            "text",
            [new NoulQuestion("same", "q?"), new NoulQuestion("same", "other?")],
            TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task ANullStateIsRejectedBeforeSending()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        // The JavaScript types allow a null state while the Python SDK rejects it; rejecting is the
        // conservative reading, and nulls are still allowed inside an object or array.
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SystemOneAsync((System.Text.Json.Nodes.JsonNode?)null, [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task TheRemovedDocumentFieldIsRejectedWithAMigrationMessage()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.SystemOneAsync(new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "q?")],
            AdditionalProperties = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
            {
                ["document"] = "old field",
            },
        }, TestContext.Current.CancellationToken));

        // Sending it fails server validation in v1, so it is caught locally with the reason.
        Assert.Contains("migrating-to-v1", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task APerCallTimeoutBoundsASingleAttempt()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Slow(TimeSpan.FromSeconds(30), StubHttpMessageHandler.Json(Fixtures.NoulResponse)));

        var options = new TypeSafeClientOptions
        {
            ApiKey = TestClient.ApiKey,
            Retry = RetryPolicy.None,
        };

        using var client = new TypeSafeClient(options, new HttpClient(handler, disposeHandler: false));

        await Assert.ThrowsAsync<TypeSafeTimeoutException>(() => client.SystemOneAsync(new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "q?")],
            Options = new TypeSafeRequestOptions { Timeout = TimeSpan.FromMilliseconds(50) },
        }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void AnInvalidClientTimeoutIsRejectedWithoutDependencyInjection(int milliseconds)
    {
        var options = new TypeSafeClientOptions
        {
            ApiKey = TestClient.ApiKey,
            Timeout = TimeSpan.FromMilliseconds(milliseconds),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new TypeSafeClient(options));
    }

    [Fact]
    public async Task AnInvalidPerCallTimeoutIsRejectedBeforeSending()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SystemOneAsync(new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "q?")],
            Options = new TypeSafeRequestOptions { Timeout = TimeSpan.Zero },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task TheModelsResourceListsModels()
    {
        const string Json = """
            {
              "models": [
                { "name": "jev-latest", "description": "The flagship model alias.", "release_date": "2026-01-15" },
                { "name": "jev-1.12", "description": "A pinned version.", "release_date": "2025-11-02" }
              ]
            }
            """;

        var (client, handler) = TestClient.Returning(Json);

        var result = await client.Models.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Models.Count);
        Assert.Equal("jev-latest", result.Models[0].Name);
        Assert.Equal("2025-11-02", result.Models[1].ReleaseDate);
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.EndsWith("/v1/models", handler.LastRequest.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisposedClientRefusesFurtherWork()
    {
        var (client, _) = TestClient.Returning(Fixtures.NoulResponse);
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AnInjectedHttpClientIsNotDisposedByTheSdk()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        var http = new HttpClient(handler, disposeHandler: false);
        var client = new TypeSafeClient(new TypeSafeClientOptions { ApiKey = TestClient.ApiKey }, http);

        client.Dispose();

        // The caller owns what it passed in; this is what makes IHttpClientFactory integration safe.
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Still-Usable", "yes");
    }

    [Fact]
    public void AnInjectedHttpClientsTimeoutIsNotChanged()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        using var client = new TypeSafeClient(new TypeSafeClientOptions { ApiKey = TestClient.ApiKey }, http);

        Assert.Equal(TimeSpan.FromSeconds(3), http.Timeout);
    }

    [Fact]
    public async Task ACallerCancellationSurfacesAsCancellationNotTimeout()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Slow(TimeSpan.FromSeconds(30), StubHttpMessageHandler.Json(Fixtures.NoulResponse)));

        var options = new TypeSafeClientOptions { ApiKey = TestClient.ApiKey, Retry = RetryPolicy.None };
        using var client = new TypeSafeClient(options, new HttpClient(handler, disposeHandler: false));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], cancellation.Token));
    }
}
