using System.Text.Json.Nodes;

namespace TypeSafeAI;

/// <summary>
/// Per-call overrides that take precedence over the client's own settings for one request only.
/// </summary>
/// <remarks>
/// Leave a property <see langword="null"/> to inherit the client-level value.
/// </remarks>
public sealed class TypeSafeRequestOptions
{
    /// <summary>
    /// Gets the model to use for this call, overriding the client default.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets the retry policy for this call, overriding the client policy.
    /// </summary>
    /// <remarks>
    /// Use <see cref="RetryPolicy.None"/> to disable retrying for a single call.
    /// </remarks>
    public RetryPolicy? Retry { get; init; }

    /// <summary>
    /// Gets the timeout applied to each individual attempt, overriding the client timeout.
    /// </summary>
    /// <remarks>
    /// This bounds one attempt. <see cref="RetryPolicy.TotalBudget"/> separately controls whether
    /// another retry may start after a failed attempt.
    /// </remarks>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets whether to turn on long context for this call, raising both the per-request and the
    /// per-question context to about <see cref="TypeSafeDefaults.ApproximateLongContextTokenBudget"/>
    /// tokens.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, the SDK sends the <see cref="TypeSafeDefaults.LongContextHeader"/>
    /// header. Long context costs the same per token but is slower and less accurate than staying
    /// within the default limits, so turn it on only for requests that need it.
    /// </remarks>
    public bool? LongContext { get; init; }

    /// <summary>
    /// Gets additional request headers for this call, merged over the client's default headers.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Gets extra top-level request-body fields for this call, merged last so they win on a
    /// collision.
    /// </summary>
    /// <remarks>
    /// The escape hatch for API features this SDK version does not model. Merging is shallow, so an
    /// object value replaces rather than merges into an existing value.
    /// </remarks>
    public IReadOnlyDictionary<string, JsonNode?>? AdditionalBodyProperties { get; init; }
}
