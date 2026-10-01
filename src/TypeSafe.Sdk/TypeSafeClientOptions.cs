using Microsoft.Extensions.Logging;

namespace TypeSafeAI;

/// <summary>
/// Configuration for a <see cref="TypeSafeClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Precedence is: an explicitly set property, then the matching environment variable, then the SDK
/// default. Empty or whitespace-only environment values are ignored, so an empty
/// <c>TYPESAFE_API_KEY</c> behaves as though it were unset. Set
/// <see cref="UseEnvironmentFallback"/> to <see langword="false"/> to skip the environment step.
/// </para>
/// <para>
/// The environment variable names match the TypeSafe Python and JavaScript SDKs, so one set of
/// variables configures every TypeSafe SDK in a deployment.
/// </para>
/// </remarks>
public sealed class TypeSafeClientOptions
{
    /// <summary>
    /// Gets or sets the API key.
    /// </summary>
    /// <remarks>
    /// Falls back to <c>TYPESAFE_API_KEY</c>. Keys can be created in the
    /// <see href="https://console.typesafe.ai/">TypeSafe console</see>.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the API root URL.
    /// </summary>
    /// <remarks>
    /// Falls back to <c>TYPESAFE_BASE_URL</c>, then to
    /// <see cref="TypeSafeDefaults.DefaultBaseUrl"/>. The path <c>/v1/systemone</c> is appended to
    /// this value, so it should not itself include a version segment.
    /// </remarks>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the model used when a request does not name one.
    /// </summary>
    /// <remarks>
    /// Falls back to <c>TYPESAFE_DEFAULT_MODEL</c>, then to <c>jev-latest</c>.
    /// </remarks>
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the timeout applied to each individual HTTP attempt.
    /// </summary>
    /// <remarks>
    /// Defaults to ten seconds, matching the Python and JavaScript SDKs. This bounds one attempt;
    /// <see cref="RetryPolicy.TotalBudget"/> separately controls whether another retry may start.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TypeSafeDefaults.DefaultTimeout;

    /// <summary>
    /// Gets or sets the retry policy.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="RetryPolicy.Default"/>. Set <see cref="RetryPolicy.None"/> to disable
    /// retrying entirely.
    /// </remarks>
    public RetryPolicy Retry { get; set; } = RetryPolicy.Default;

    /// <summary>
    /// Gets or sets request headers sent with every call.
    /// </summary>
    /// <remarks>
    /// Per-call headers from <see cref="TypeSafeRequestOptions.Headers"/> are merged over these.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? DefaultHeaders { get; set; }

    /// <summary>
    /// Gets or sets an additional product token appended to the SDK's <c>User-Agent</c>.
    /// </summary>
    /// <remarks>
    /// Use the conventional <c>product/version</c> form, for example <c>my-service/2.1.0</c>. The
    /// SDK's own token is always sent first and cannot be overridden.
    /// </remarks>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Gets or sets the logger factory used for request logging.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> the client logs nothing. The
    /// <c>TypeSafeAI.Sdk.DependencyInjection</c> package wires this up from the container
    /// automatically.
    /// </remarks>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Gets or sets the time provider used for retry delays.
    /// </summary>
    /// <remarks>
    /// Exposed so tests can advance time instead of sleeping. Leave <see langword="null"/> in
    /// production code.
    /// </remarks>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether unset options fall back to the <c>TYPESAFE_*</c>
    /// environment variables.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>. When <see langword="false"/>, the client reads no
    /// environment variable: <see cref="ApiKey"/> must be set explicitly, or constructing the
    /// client throws <see cref="TypeSafeConfigurationException"/>, and an unset
    /// <see cref="BaseUrl"/> or <see cref="Model"/> uses the SDK default. Use this when a stray
    /// variable on the host must not be able to change the key, the endpoint or the model.
    /// </remarks>
    public bool UseEnvironmentFallback { get; set; } = true;

    /// <summary>
    /// Creates a copy of these options.
    /// </summary>
    /// <returns>A new instance with the same values.</returns>
    /// <remarks>
    /// <see cref="DefaultHeaders"/> is copied into a new dictionary, so changing the original
    /// dictionary does not affect the copy. <see cref="Retry"/> is shared; a
    /// <see cref="RetryPolicy"/> cannot be changed after construction.
    /// </remarks>
    public TypeSafeClientOptions Clone() => new()
    {
        ApiKey = ApiKey,
        BaseUrl = BaseUrl,
        Model = Model,
        Timeout = Timeout,
        Retry = Retry,
        DefaultHeaders = DefaultHeaders switch
        {
            null => null,
            Dictionary<string, string> headers => new Dictionary<string, string>(headers, headers.Comparer),
            var headers => new Dictionary<string, string>(headers, StringComparer.Ordinal),
        },
        UserAgent = UserAgent,
        LoggerFactory = LoggerFactory,
        TimeProvider = TimeProvider,
        UseEnvironmentFallback = UseEnvironmentFallback,
    };

    /// <summary>
    /// Resolves the API key, falling back to the environment.
    /// </summary>
    /// <returns>The resolved API key.</returns>
    /// <exception cref="TypeSafeConfigurationException">
    /// No API key was supplied and, unless <see cref="UseEnvironmentFallback"/> is
    /// <see langword="false"/>, <c>TYPESAFE_API_KEY</c> is unset or blank.
    /// </exception>
    internal string ResolveApiKey()
    {
        var key = FirstNonBlank(ApiKey, ReadEnvironment(TypeSafeDefaults.ApiKeyEnvironmentVariable));

        if (key is null && !UseEnvironmentFallback)
        {
            throw new TypeSafeConfigurationException(
                "No TypeSafe API key was configured. Set the ApiKey option; the " +
                $"{TypeSafeDefaults.ApiKeyEnvironmentVariable} environment variable is not read because " +
                "UseEnvironmentFallback is false. Create a key at https://console.typesafe.ai/.");
        }

        if (key is null)
        {
            throw new TypeSafeConfigurationException(
                "No TypeSafe API key was configured. Set the ApiKey option, or set the " +
                $"{TypeSafeDefaults.ApiKeyEnvironmentVariable} environment variable. Create a key " +
                "at https://console.typesafe.ai/.");
        }

        return key;
    }

    /// <summary>
    /// Resolves the API root URL, falling back to the environment and then to the default.
    /// </summary>
    /// <returns>The resolved base URL.</returns>
    internal Uri ResolveBaseUrl()
    {
        if (BaseUrl is not null)
        {
            return BaseUrl;
        }

        // TYPESAFE_BASE_URL is the documented name; TYPESAFE_ENDPOINT appears in the cookbooks and
        // is accepted so that either style of deployment configuration works.
        var configured = FirstNonBlank(
            ReadEnvironment(TypeSafeDefaults.BaseUrlEnvironmentVariable),
            ReadEnvironment(TypeSafeDefaults.LegacyEndpointEnvironmentVariable));

        return configured is null
            ? new Uri(TypeSafeDefaults.DefaultBaseUrl, UriKind.Absolute)
            : new Uri(configured, UriKind.Absolute);
    }

    /// <summary>
    /// Resolves the default model, falling back to the environment and then to the SDK default.
    /// </summary>
    /// <returns>The resolved model name.</returns>
    internal string ResolveModel() =>
        FirstNonBlank(Model, ReadEnvironment(TypeSafeDefaults.DefaultModelEnvironmentVariable))
        ?? TypeSafeDefaults.DefaultModel;

    private string? ReadEnvironment(string name) =>
        UseEnvironmentFallback ? Environment.GetEnvironmentVariable(name) : null;

    internal static bool IsValidTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero || timeout == System.Threading.Timeout.InfiniteTimeSpan;

    internal static void ValidateTimeout(TimeSpan timeout)
    {
        if (!IsValidTimeout(timeout))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The timeout must be positive, or Timeout.InfiniteTimeSpan to disable it.");
        }
    }

    private static string? FirstNonBlank(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }

        return string.IsNullOrWhiteSpace(second) ? null : second;
    }
}
