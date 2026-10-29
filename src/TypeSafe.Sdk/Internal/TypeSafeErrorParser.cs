using System.Globalization;
using System.Net;
using System.Text.Json;

namespace TypeSafeAI.Internal;

/// <summary>
/// Turns an unsuccessful response into the right <see cref="TypeSafeApiException"/>.
/// </summary>
/// <remarks>
/// The exception type is chosen from the HTTP status code alone. The body's <c>detail</c> member is
/// polymorphic — an object for application errors, a bare string for framework errors — and the
/// <c>error_type</c> values inside it are undocumented and open-ended, so they are surfaced but
/// never switched on.
/// </remarks>
internal static class TypeSafeErrorParser
{
    /// <summary>
    /// Builds the exception for an unsuccessful response.
    /// </summary>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="body">The raw response body, or <see langword="null"/> when it was empty.</param>
    /// <param name="headers">The response headers.</param>
    /// <param name="requestId">The request id, when present.</param>
    /// <param name="endpoint">The request method and URL.</param>
    /// <returns>The exception to throw.</returns>
    public static TypeSafeApiException Create(
        HttpStatusCode statusCode,
        string? body,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        string? requestId,
        string? endpoint)
    {
        var details = ParseDetails(body);
        var message = BuildMessage(statusCode, details, requestId);

        var exception = (int)statusCode switch
        {
            400 => new TypeSafeBadRequestException(message),
            401 => new TypeSafeAuthenticationException(message),
            403 => new TypeSafePermissionDeniedException(message),
            404 => new TypeSafeNotFoundException(message),
            422 => new TypeSafeUnprocessableEntityException(message),
            429 => new TypeSafeRateLimitException(message) { RetryAfter = ParseRetryAfter(headers) },
            >= 500 => new TypeSafeServerException(message),
            _ => new TypeSafeApiException(message),
        };

        // Every property below is a snapshot, so the exception stays valid after the response is
        // disposed and remains usable from a logging handler or an exception filter.
        exception.StatusCode = statusCode;
        exception.Details = details;
        exception.RequestId = requestId;
        exception.Endpoint = endpoint;
        exception.Headers = headers;
        exception.DocumentationUrl = "https://docs.typesafe.ai/api";

        return exception;
    }

    /// <summary>
    /// Parses the error body, tolerating a body that is empty, non-JSON, or shaped unexpectedly.
    /// </summary>
    /// <param name="body">The raw response body.</param>
    /// <returns>The parsed details, or <see langword="null"/> when nothing could be read.</returns>
    /// <remarks>
    /// Failing to parse an error must never mask the error, so every failure path returns a value
    /// rather than throwing.
    /// </remarks>
    public static TypeSafeErrorDetails? ParseDetails(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // A proxy or load balancer answered instead of the API. Keep the text so it is not lost.
            return new TypeSafeErrorDetails(null, Truncate(body), null);
        }

        using (document)
        {
            var root = document.RootElement.Clone();

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("detail", out var detail))
            {
                return new TypeSafeErrorDetails(null, null, root);
            }

            switch (detail.ValueKind)
            {
                case JsonValueKind.Object:
                    return new TypeSafeErrorDetails(
                        ReadString(detail, "error_type"),
                        ReadString(detail, "message"),
                        root);

                case JsonValueKind.String:
                    // Framework-level errors, such as {"detail":"Not Found"}.
                    return new TypeSafeErrorDetails(null, detail.GetString(), root);

                default:
                    return new TypeSafeErrorDetails(null, null, root);
            }
        }
    }

    /// <summary>
    /// Reads the server's requested wait from <c>retry-after-ms</c> or <c>Retry-After</c>.
    /// </summary>
    /// <param name="headers">The response headers.</param>
    /// <returns>The requested delay, or <see langword="null"/> when absent or unparseable.</returns>
    /// <remarks>
    /// <c>retry-after-ms</c> wins when both are present, matching the sibling SDKs.
    /// <c>Retry-After</c> is accepted in both its delta-seconds and HTTP-date forms.
    /// </remarks>
    public static TimeSpan? ParseRetryAfter(IReadOnlyDictionary<string, IReadOnlyList<string>> headers)
    {
        if (TryGetHeader(headers, "retry-after-ms", out var milliseconds) &&
            double.TryParse(milliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) &&
            ms >= 0)
        {
            // A value beyond TimeSpan's range, infinity included, saturates rather than throwing,
            // so the caller still gets the rate-limit exception and MaxRetryAfter still applies.
            return ms >= TimeSpan.MaxValue.TotalMilliseconds ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(ms);
        }

        if (!TryGetHeader(headers, "Retry-After", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            return seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
        {
            var delay = when - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private static bool TryGetHeader(
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        string name,
        out string value)
    {
        value = string.Empty;

        if (!headers.TryGetValue(name, out var values) || values.Count == 0)
        {
            return false;
        }

        value = values[0];
        return true;
    }

    private static string BuildMessage(
        HttpStatusCode statusCode,
        TypeSafeErrorDetails? details,
        string? requestId)
    {
        var message = details?.Message;

        if (string.IsNullOrWhiteSpace(message))
        {
            message = $"The TypeSafe API returned {(int)statusCode} {statusCode}.";
        }

        if (details?.ErrorType is { Length: > 0 } errorType)
        {
            message = $"{message} (error type: {errorType})";
        }

        if (requestId is { Length: > 0 })
        {
            message = $"{message} [request id: {requestId}]";
        }

        return message;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string Truncate(string value) =>
        value.Length <= 512 ? value : string.Concat(value.AsSpan(0, 512), "...");
}
