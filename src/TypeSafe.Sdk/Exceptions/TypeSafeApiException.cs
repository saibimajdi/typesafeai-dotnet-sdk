using System.Net;

namespace TypeSafeAI;

/// <summary>
/// The TypeSafe API returned an unsuccessful HTTP response, after any retries.
/// </summary>
/// <remarks>
/// <para>
/// The concrete type is chosen from the HTTP status code alone. The status code is stable and
/// documented; the body's <c>detail.error_type</c> string is not, so it is surfaced through
/// <see cref="ErrorType"/> but never used to select an exception type. The one exception is a
/// body over the SDK's 16 MiB limit, which raises <see cref="TypeSafeResponseValidationException"/>
/// whatever the status.
/// </para>
/// <para>
/// Every property is a snapshot taken when the response was read, so the exception stays valid
/// after the underlying <see cref="HttpResponseMessage"/> has been disposed.
/// </para>
/// </remarks>
public class TypeSafeApiException : TypeSafeException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TypeSafeApiException"/> class.
    /// </summary>
    public TypeSafeApiException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeSafeApiException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public TypeSafeApiException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeSafeApiException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this error.</param>
    public TypeSafeApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeSafeApiException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="statusCode">The HTTP status code of the response.</param>
    /// <param name="details">The parsed error details, or <see langword="null"/> when unavailable.</param>
    public TypeSafeApiException(string message, HttpStatusCode statusCode, TypeSafeErrorDetails? details)
        : base(message)
    {
        StatusCode = statusCode;
        Details = details;
    }

    /// <summary>
    /// Gets the HTTP status code of the response.
    /// </summary>
    public HttpStatusCode StatusCode { get; internal set; }

    /// <summary>
    /// Gets the raw error details parsed from the response body, or <see langword="null"/> when
    /// the body was empty, unreadable, or not JSON.
    /// </summary>
    /// <remarks>
    /// This is the escape hatch for anything the SDK does not model. The API's <c>detail</c>
    /// member is polymorphic — a string for framework errors and an object for application
    /// errors — and both shapes are preserved here.
    /// </remarks>
    public TypeSafeErrorDetails? Details { get; internal set; }

    /// <summary>
    /// Gets the API's machine-readable error classification, such as <c>authentication_error</c>,
    /// or <see langword="null"/> when the response did not carry one.
    /// </summary>
    /// <remarks>
    /// Do not branch on this value. It is not part of the documented contract and new values can
    /// appear at any time; branch on <see cref="StatusCode"/> instead.
    /// </remarks>
    public string? ErrorType => Details?.ErrorType;

    /// <summary>
    /// Gets the human-readable message supplied by the API, or <see langword="null"/> when the
    /// response did not carry one.
    /// </summary>
    public string? ErrorMessage => Details?.Message;

    /// <summary>
    /// Gets the value of the <c>x-typesafe-request-id</c> response header, or <see langword="null"/>
    /// when it was absent.
    /// </summary>
    /// <remarks>
    /// Quote this when reporting a problem to TypeSafe; it is the only handle on the specific
    /// request that failed.
    /// </remarks>
    public string? RequestId { get; internal set; }

    /// <summary>
    /// Gets the request method and URL, without credentials, query parameters, or fragment.
    /// </summary>
    public string? Endpoint { get; internal set; }

    /// <summary>
    /// Gets the response headers, snapshotted. Multiple values for one header are preserved.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; internal set; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a link to the relevant TypeSafe documentation.
    /// </summary>
    public string? DocumentationUrl { get; internal set; }

    /// <summary>
    /// Gets the response body as raw JSON, or <see langword="null"/> when the body was empty or
    /// not JSON.
    /// </summary>
    public System.Text.Json.JsonElement? Body => Details?.Body;
}
