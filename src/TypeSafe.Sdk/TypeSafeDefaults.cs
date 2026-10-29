namespace TypeSafeAI;

/// <summary>
/// Environment variable names shared with the TypeSafe Python and JavaScript SDKs, and the
/// defaults applied when neither an explicit option nor an environment variable supplies a value.
/// </summary>
/// <remarks>
/// <para>
/// Precedence is always: explicit client option, then environment variable, then the default
/// below. Empty or whitespace-only environment values are treated as unset.
/// </para>
/// <para>
/// The names match the TypeSafe Python and JavaScript SDKs, so one set of environment variables
/// configures every TypeSafe SDK in a polyglot deployment.
/// </para>
/// </remarks>
public static class TypeSafeDefaults
{
    /// <summary>
    /// Environment variable holding the API key. Required unless the key is passed to the client
    /// constructor directly.
    /// </summary>
    public const string ApiKeyEnvironmentVariable = "TYPESAFE_API_KEY";

    /// <summary>
    /// Environment variable overriding the API root URL.
    /// </summary>
    public const string BaseUrlEnvironmentVariable = "TYPESAFE_BASE_URL";

    /// <summary>
    /// Environment variable overriding the default model.
    /// </summary>
    public const string DefaultModelEnvironmentVariable = "TYPESAFE_DEFAULT_MODEL";

    /// <summary>
    /// Environment variable the sibling TypeSafe SDKs use to set their log level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The .NET SDK does not read this variable, and does not set a log level of its own. Logging
    /// levels belong to the host's logging configuration in .NET, and a library that silently
    /// reconfigured them would fight the application that owns them. Configure the
    /// <c>TypeSafe.Sdk</c> category through your own logging setup instead:
    /// </para>
    /// <code>
    /// builder.Logging.AddFilter("TypeSafe.Sdk", LogLevel.Debug);
    /// </code>
    /// <para>
    /// The name is published here for polyglot deployments, where the same environment variable
    /// may still need to drive a Python or JavaScript service alongside a .NET one.
    /// </para>
    /// </remarks>
    public const string LogLevelEnvironmentVariable = "TYPESAFE_LOG_LEVEL";

    /// <summary>
    /// Environment variable overriding the API root URL, accepted as an undocumented alias of
    /// <see cref="BaseUrlEnvironmentVariable"/>.
    /// </summary>
    /// <remarks>
    /// The TypeSafe cookbooks use <c>TYPESAFE_ENDPOINT</c> for the same purpose while the SDK
    /// reference pages document <c>TYPESAFE_BASE_URL</c>. Both are honoured so that either style
    /// of deployment configuration works; <see cref="BaseUrlEnvironmentVariable"/> wins when
    /// both are set.
    /// </remarks>
    public const string LegacyEndpointEnvironmentVariable = "TYPESAFE_ENDPOINT";

    /// <summary>
    /// The API root used when neither an explicit option nor an environment variable supplies one.
    /// </summary>
    public const string DefaultBaseUrl = "https://api.typesafe.ai";

    /// <summary>
    /// The model used when neither an explicit option nor an environment variable supplies one.
    /// </summary>
    /// <remarks>
    /// <c>jev-latest</c> is an alias. The response's <see cref="SystemOneResult.Model"/> reports
    /// the concrete version the alias resolved to, such as <c>jev-1.14.0</c>, and the alias can
    /// resolve differently over time.
    /// </remarks>
    public const string DefaultModel = "jev-latest";

    /// <summary>
    /// The timeout applied to a single HTTP attempt.
    /// </summary>
    /// <remarks>
    /// This is the per-attempt timeout. It is distinct from
    /// <see cref="RetryPolicy.TotalBudget"/>, which prevents a retry from starting when its delay
    /// would reach the retry budget.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The number of levels a Score rubric may contain, inclusive.
    /// </summary>
    public const int MinimumScoreLevels = 2;

    /// <summary>
    /// The largest number of levels a Score rubric may contain, inclusive.
    /// </summary>
    /// <remarks>
    /// A rubric of eleven levels is rejected by the API.
    /// </remarks>
    public const int MaximumScoreLevels = 10;

    /// <summary>
    /// The largest number of options a Choice may offer, inclusive.
    /// </summary>
    /// <remarks>
    /// The API accepts at most 255 options. The documentation also notes that a Choice "works
    /// reliably up to roughly 240 options", so treat 255 as the hard cap and 240 as a practical
    /// ceiling worth staying under.
    /// </remarks>
    public const int MaximumChoiceOptions = 255;

    /// <summary>
    /// The approximate number of tokens shared between <c>state</c> and all <c>questions</c> in a
    /// single request: the documented per-request context of Jev 1.13 and Jev 1.14.
    /// </summary>
    /// <remarks>
    /// The API publishes no token-counting endpoint, so the SDK cannot enforce this. It is
    /// documented here so callers can size their batches. Batching every question into one
    /// request is dramatically cheaper and faster than one request per question. The state plus
    /// any one question must also fit <see cref="ApproximateQuestionTokenBudget"/>.
    /// </remarks>
    public const int ApproximateRequestTokenBudget = 64_000;

    /// <summary>
    /// The approximate number of tokens the <c>state</c> plus any one question may use: the
    /// documented per-question context of Jev 1.13 and Jev 1.14.
    /// </summary>
    /// <remarks>
    /// Like <see cref="ApproximateRequestTokenBudget"/>, this is informational and not enforced.
    /// </remarks>
    public const int ApproximateQuestionTokenBudget = 32_000;

    /// <summary>
    /// The approximate per-request and per-question context when
    /// <see cref="TypeSafeRequestOptions.LongContext"/> is turned on.
    /// </summary>
    /// <remarks>
    /// Long context costs the same per token, but is slower and less accurate than staying
    /// within the default limits, so turn it on only for requests that need it.
    /// </remarks>
    public const int ApproximateLongContextTokenBudget = 262_000;

    /// <summary>
    /// The request header that turns on images and Box questions.
    /// </summary>
    /// <remarks>
    /// The SDK sends this header with the value <c>true</c> automatically on any request that
    /// carries <see cref="SystemOneRequest.Assets"/> or a <see cref="BoxQuestion"/>. A value set
    /// through <see cref="TypeSafeRequestOptions.Headers"/> wins.
    /// </remarks>
    public const string ImagesHeader = "TypeSafe-Danger-Zone-Images";

    /// <summary>
    /// The request header that raises both context limits to
    /// <see cref="ApproximateLongContextTokenBudget"/>.
    /// </summary>
    /// <remarks>
    /// Sent with the value <c>true</c> when <see cref="TypeSafeRequestOptions.LongContext"/> is
    /// <see langword="true"/>.
    /// </remarks>
    public const string LongContextHeader = "TypeSafe-Danger-Zone-Long-Context";
}
