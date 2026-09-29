using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TypeSafeAI.Internal;

namespace TypeSafeAI;

/// <summary>
/// The default <see cref="ITypeSafeClient"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// Create one client and reuse it. The client is thread-safe, holds a single pooled
/// <see cref="HttpClient"/>, and is designed to serve many concurrent requests.
/// </para>
/// <para>
/// The client owns the <see cref="HttpClient"/> it creates itself and disposes it. An
/// <see cref="HttpClient"/> passed to a constructor belongs to the caller and is left alone, which
/// is what makes the type safe to use with <c>IHttpClientFactory</c>.
/// </para>
/// <para>
/// This type is asynchronous only. Synchronous wrappers are deliberately not provided because
/// blocking on asynchronous I/O risks thread-pool starvation and deadlocks; call
/// <c>SystemOneAsync</c> from an asynchronous method instead.
/// </para>
/// </remarks>
public sealed class TypeSafeClient : ITypeSafeClient, IDisposable
{
    private readonly TypeSafeTransport _transport;
    private readonly TypeSafeClientOptions _options;
    private readonly string _defaultModel;
    private readonly IModelsResource _models;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeSafeClient"/> class.
    /// </summary>
    /// <param name="options">
    /// The client options. When <see langword="null"/>, every setting comes from the environment
    /// and from the SDK defaults. The client keeps a copy, so changing the instance afterwards does
    /// not affect this client.
    /// </param>
    /// <param name="httpClient">
    /// An optional <see cref="HttpClient"/> to use. When <see langword="null"/> the client creates
    /// and owns one. A supplied client is never disposed by this type.
    /// </param>
    /// <exception cref="TypeSafeConfigurationException">No API key could be resolved.</exception>
    /// <exception cref="ArgumentException">A configured URL or timeout is invalid.</exception>
    public TypeSafeClient(TypeSafeClientOptions? options = null, HttpClient? httpClient = null)
        : this(options, apiKey: null, httpClient)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeSafeClient"/> class.
    /// </summary>
    /// <param name="apiKey">
    /// The API key. Takes precedence over <see cref="TypeSafeClientOptions.ApiKey"/> and over
    /// <c>TYPESAFE_API_KEY</c>.
    /// </param>
    /// <param name="options">
    /// The client options. When <see langword="null"/>, every remaining setting comes from the
    /// environment and from the SDK defaults. The client keeps a copy, so <paramref name="apiKey"/>
    /// is never written into this instance and changing it afterwards does not affect this client.
    /// </param>
    /// <param name="httpClient">
    /// An optional <see cref="HttpClient"/> to use. When <see langword="null"/> the client creates
    /// and owns one. A supplied client is never disposed by this type.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="apiKey"/> is empty or whitespace.</exception>
    /// <exception cref="TypeSafeConfigurationException">No API key could be resolved.</exception>
    public TypeSafeClient(string apiKey, TypeSafeClientOptions? options = null, HttpClient? httpClient = null)
        : this(options, apiKey, httpClient)
    {
    }

    private TypeSafeClient(TypeSafeClientOptions? options, string? apiKey, HttpClient? httpClient)
    {
        // A copy, so the explicit key below cannot leak into an options object the caller shares,
        // and later changes to that object do not reach this client.
        _options = options?.Clone() ?? new TypeSafeClientOptions();
        TypeSafeClientOptions.ValidateTimeout(_options.Timeout);

        if (apiKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
            _options.ApiKey = apiKey;
        }

        var resolvedKey = _options.ResolveApiKey();
        var baseUrl = _options.ResolveBaseUrl();

        _defaultModel = _options.ResolveModel();

        var ownsClient = httpClient is null;
        var client = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

        // A per-attempt timeout is applied with a linked CancellationTokenSource so that retries
        // are bounded individually. Disable HttpClient's overlapping timeout only on a client the
        // SDK owns. A supplied client's settings belong to its caller and are left unchanged.
        if (ownsClient)
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }

        _transport = new TypeSafeTransport(client, ownsClient, _options, resolvedKey);
        _models = new ModelsResource(_transport, Combine(baseUrl, "v1/models"));

        SystemOneUri = Combine(baseUrl, "v1/systemone");
    }

    /// <summary>
    /// Gets the resource exposing the models available to the account.
    /// </summary>
    public IModelsResource Models => _models;

    /// <summary>
    /// Gets the absolute URI that System One requests are sent to.
    /// </summary>
    internal Uri SystemOneUri { get; }

    /// <inheritdoc />
    public Task<SystemOneResult> SystemOneAsync(
        SystemOneRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SystemOneAsync(
            request.State,
            request.Questions,
            request.Model,
            request.AdditionalProperties,
            request.Options,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SystemOneResult> SystemOneAsync(
        string state,
        IEnumerable<Question> questions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        return SystemOneAsync(
            JsonValue.Create(state),
            questions,
            model: null,
            additionalBodyProperties: null,
            requestOptions: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SystemOneResult> SystemOneAsync(
        JsonNode? state,
        IEnumerable<Question> questions,
        CancellationToken cancellationToken = default) =>
        SystemOneAsync(
            state,
            questions,
            model: null,
            additionalBodyProperties: null,
            requestOptions: null,
            cancellationToken);

    /// <inheritdoc />
    public Task<SystemOneResult> SystemOneAsync<TState>(
        TState state,
        IEnumerable<Question> questions,
        JsonTypeInfo<TState> stateTypeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stateTypeInfo);

        return SystemOneAsync(
            JsonSerializer.SerializeToNode(state, stateTypeInfo),
            questions,
            model: null,
            additionalBodyProperties: null,
            requestOptions: null,
            cancellationToken);
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("Serializing the state with reflection is unavailable under trimming. Use the JsonTypeInfo overload instead.")]
    [RequiresDynamicCode("Serializing the state with reflection is unavailable under ahead-of-time compilation. Use the JsonTypeInfo overload instead.")]
    public Task<SystemOneResult> SystemOneAsync<TState>(
        TState state,
        IEnumerable<Question> questions,
        CancellationToken cancellationToken = default) =>
        SystemOneAsync(
            // Deliberately not the SDK's own options: those apply a snake_case policy that would
            // silently rename the caller's properties. The state is the caller's data, so its
            // property names are preserved exactly as declared.
            JsonSerializer.SerializeToNode(state),
            questions,
            model: null,
            additionalBodyProperties: null,
            requestOptions: null,
            cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.Dispose();
    }

    private async Task<SystemOneResult> SystemOneAsync(
        JsonNode? state,
        IEnumerable<Question> questions,
        string? model,
        IReadOnlyDictionary<string, JsonNode?>? additionalBodyProperties,
        TypeSafeRequestOptions? requestOptions,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(questions);

        var effective = Merge(requestOptions, model, additionalBodyProperties);
        var effectiveModel = effective?.Model ?? _defaultModel;

        var body = TypeSafeRequestWriter.Write(state, effectiveModel, questions, effective?.AdditionalBodyProperties);

        var response = await _transport
            .SendAsync(HttpMethod.Post, SystemOneUri, body, "systemone", effective, cancellationToken)
            .ConfigureAwait(false);

        return TypeSafeResponseReader.ReadSystemOne(response, Logger);
    }

    private ILogger Logger => _options.LoggerFactory?.CreateLogger("TypeSafe.Sdk") ?? NullLogger.Instance;

    /// <summary>
    /// Folds the request-level model and body fields into the per-call options.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when nothing was overridden, so the common path allocates
    /// nothing and inherits every client setting.
    /// </remarks>
    private static TypeSafeRequestOptions? Merge(
        TypeSafeRequestOptions? requestOptions,
        string? model,
        IReadOnlyDictionary<string, JsonNode?>? additionalBodyProperties)
    {
        if (model is null && additionalBodyProperties is null)
        {
            return requestOptions;
        }

        return new TypeSafeRequestOptions
        {
            Model = model ?? requestOptions?.Model,
            Retry = requestOptions?.Retry,
            Timeout = requestOptions?.Timeout,
            Headers = requestOptions?.Headers,
            AdditionalBodyProperties = additionalBodyProperties ?? requestOptions?.AdditionalBodyProperties,
        };
    }

    /// <summary>
    /// Combines a base URL with a relative path, tolerating a trailing slash on either side.
    /// </summary>
    private static Uri Combine(Uri baseUrl, string relativePath)
    {
        var root = baseUrl.AbsoluteUri.EndsWith('/')
            ? baseUrl.AbsoluteUri
            : string.Concat(baseUrl.AbsoluteUri, "/");

        return new Uri(new Uri(root, UriKind.Absolute), relativePath);
    }
}
