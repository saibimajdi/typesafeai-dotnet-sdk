using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TypeSafeAI.DependencyInjection;

namespace TypeSafeAI.Tests;

/// <summary>
/// Verifies the <c>IHttpClientFactory</c> registration path.
/// </summary>
public sealed class DependencyInjectionTests
{
    /// <summary>
    /// Builds a container with the SDK registered over a scripted transport.
    /// </summary>
    /// <param name="handler">The stub handler to route requests through.</param>
    /// <param name="configure">An optional options callback.</param>
    /// <param name="configuration">Optional configuration to bind.</param>
    /// <returns>The built provider.</returns>
    private static ServiceProvider Build(
        StubHttpMessageHandler handler,
        Action<TypeSafeClientOptions>? configure = null,
        IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var builder = configuration is null
            ? services.AddTypeSafeClient(configure)
            : services.AddTypeSafeClient(configuration, configure);

        builder.ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider();
    }

    private static Action<TypeSafeClientOptions> WithKey => options => options.ApiKey = TestClient.ApiKey;

    [Fact]
    public async Task TheClientResolvesFromTheContainerAndWorks()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var provider = Build(handler, WithKey);

        var client = provider.GetRequiredService<ITypeSafeClient>();
        var result = await client.SystemOneAsync(
            "text",
            [new NoulQuestion("is_urgent", "q?")],
            TestContext.Current.CancellationToken);

        Assert.Equal(0.92, result.Noul("is_urgent").Probability);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", handler.LastRequest.Uri.AbsoluteUri);
    }

    [Fact]
    public void TheClientIsRegisteredAsBothTheInterfaceAndTheConcreteType()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var provider = Build(handler, WithKey);

        Assert.Same(
            provider.GetRequiredService<ITypeSafeClient>(),
            provider.GetRequiredService<TypeSafeClient>());
    }

    [Fact]
    public void TheClientIsASingletonSoOneInstanceServesTheApplication()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var provider = Build(handler, WithKey);

        Assert.Same(provider.GetRequiredService<ITypeSafeClient>(), provider.GetRequiredService<ITypeSafeClient>());
    }

    [Fact]
    public void ConfigurationSectionValuesAreBound()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TypeSafe:ApiKey"] = "tsk_from_configuration",
                ["TypeSafe:BaseUrl"] = "https://configured.example",
                ["TypeSafe:Model"] = "jev-1.12",
                ["TypeSafe:Timeout"] = "00:00:05",
                ["TypeSafe:UserAgent"] = "configured-agent/1.0",
                ["TypeSafe:DefaultHeaders:X-Tenant"] = "contoso",
            })
            .Build();

        using var provider = Build(handler, configure: null, configuration);
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TypeSafeClientOptions>>().Value;

        Assert.Equal("tsk_from_configuration", options.ApiKey);
        // Uri normalises a bare authority to include the root path.
        Assert.Equal("https://configured.example/", options.BaseUrl!.AbsoluteUri);
        Assert.Equal("jev-1.12", options.Model);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Timeout);
        Assert.Equal("configured-agent/1.0", options.UserAgent);
        Assert.Equal("contoso", options.DefaultHeaders!["X-Tenant"]);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("not-a-bool", true)]
    public void UseEnvironmentFallbackIsBound(string configured, bool expected)
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TypeSafe:ApiKey"] = "tsk_from_configuration",
                ["TypeSafe:UseEnvironmentFallback"] = configured,
            })
            .Build();

        using var provider = Build(handler, configure: null, configuration);
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TypeSafeClientOptions>>().Value;

        // An unparseable value is left untouched, like every other bound key.
        Assert.Equal(expected, options.UseEnvironmentFallback);
    }

    [Fact]
    public async Task BoundConfigurationReachesTheWire()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TypeSafe:ApiKey"] = "tsk_from_configuration",
                ["TypeSafe:Model"] = "jev-1.12",
                ["TypeSafe:DefaultHeaders:X-Tenant"] = "contoso",
            })
            .Build();

        using var provider = Build(handler, configure: null, configuration);
        var client = provider.GetRequiredService<ITypeSafeClient>();

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal("Bearer tsk_from_configuration", handler.LastRequest.Headers["Authorization"]);
        Assert.Equal("contoso", handler.LastRequest.Headers["X-Tenant"]);

        using var body = handler.LastRequest.ParseBody();
        Assert.Equal("jev-1.12", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void TheConfigureCallbackRunsAfterConfigurationBinding()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TypeSafe:Model"] = "from-config" })
            .Build();

        using var provider = Build(handler, options => options.Model = "from-callback", configuration);
        var resolved = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TypeSafeClientOptions>>().Value;

        Assert.Equal("from-callback", resolved.Model);
    }

    [Fact]
    public void AnInvalidTimeoutIsRejectedWhenOptionsAreResolved()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var provider = Build(handler, options =>
        {
            options.ApiKey = TestClient.ApiKey;
            options.Timeout = TimeSpan.Zero;
        });

        var exception = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
            () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TypeSafeClientOptions>>().Value);

        Assert.Contains("Timeout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeBaseUrlIsRejectedWhenOptionsAreResolved()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var provider = Build(handler, options =>
        {
            options.ApiKey = TestClient.ApiKey;
            options.BaseUrl = new Uri("/relative", UriKind.Relative);
        });

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
            () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TypeSafeClientOptions>>().Value);
    }

    [Fact]
    public void AMissingApiKeyFailsWhenTheClientIsResolved()
    {
        using var environment = new EnvironmentScope().Set(TypeSafeDefaults.ApiKeyEnvironmentVariable, null);

        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var provider = Build(handler, configure: null);

        Assert.Throws<TypeSafeConfigurationException>(() => provider.GetRequiredService<ITypeSafeClient>());
    }

    [Fact]
    public void TheRegisteredHandlerIsReusedAcrossClientResolutions()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        using var provider = Build(handler, WithKey);

        // The container owns the handler, and the SDK must not dispose the HttpClient it was given.
        _ = provider.GetRequiredService<ITypeSafeClient>();
        _ = provider.GetRequiredService<ITypeSafeClient>();

        Assert.Equal(0, handler.Attempts);
        handler.Requests.Clear();
    }

    [Fact]
    public void TheHttpClientNameIsStable()
    {
        // Callers need a documented name so they can attach their own handlers through the returned
        // IHttpClientBuilder without racing the SDK's registration.
        Assert.Equal("TypeSafe", TypeSafeServiceCollectionExtensions.HttpClientName);
        Assert.Equal("TypeSafe", TypeSafeServiceCollectionExtensions.ConfigurationSectionName);
    }

    [Fact]
    public async Task AdditionalHandlersCanBeAttachedToTheReturnedBuilder()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));
        var services = new ServiceCollection();
        services.AddLogging();

        var observed = 0;

        services.AddTypeSafeClient(WithKey)
            .AddHttpMessageHandler(() => new CountingHandler(() => observed++))
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ITypeSafeClient>();

        await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal(1, observed);
    }

    /// <summary>
    /// A pass-through handler used to prove the returned builder really does allow chaining.
    /// </summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        private readonly Action _onSend;

        public CountingHandler(Action onSend) => _onSend = onSend;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _onSend();
            return base.SendAsync(request, cancellationToken);
        }
    }
}
