using System.Text.Json.Serialization;

namespace TypeSafeAI;

/// <summary>
/// Token usage for one request.
/// </summary>
/// <param name="InputTokens">
/// The number of input tokens the request consumed, or <see langword="null"/> when the API did not
/// report it or reported a value that does not fit an <see cref="int"/>.
/// </param>
/// <param name="OutputTokens">
/// The number of output tokens the request produced, or <see langword="null"/> when the API did not
/// report it or reported a value that does not fit an <see cref="int"/>.
/// </param>
/// <remarks>
/// <para>
/// Both counts are nullable because the API does not always report them, and a missing count is
/// not a count of zero. A reported value that cannot be represented, fractional or out of range,
/// is also <see langword="null"/>; the original value stays available in
/// <see cref="SystemOneResult.RawJson"/>. Treat <see langword="null"/> as "unknown" and decide for
/// yourself whether to substitute <c>0</c> when accumulating totals.
/// </para>
/// <para>
/// The request's <c>state</c> and its <c>questions</c> share one token budget of roughly
/// <see cref="TypeSafeDefaults.ApproximateRequestTokenBudget"/> tokens, so these counters are the
/// way to tell how close a batch is to that shared ceiling.
/// </para>
/// </remarks>
public sealed record Usage(
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens)
{
    /// <summary>
    /// Gets the total tokens reported for the request, or <see langword="null"/> when neither count
    /// was reported or their sum does not fit an <see cref="int"/>.
    /// </summary>
    public int? TotalTokens
    {
        get
        {
            if (InputTokens is null && OutputTokens is null)
            {
                return null;
            }

            long total = (long)(InputTokens ?? 0) + (OutputTokens ?? 0);
            return total is >= int.MinValue and <= int.MaxValue ? (int)total : null;
        }
    }
}
