using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TypeSafeAI.Internal;

namespace TypeSafeAI;

/// <summary>
/// Sends HTTP requests and applies the retry policy.
/// </summary>
/// <remarks>
/// <para>
/// A fresh <see cref="HttpRequestMessage"/> is built per attempt. The serialized body bytes are
/// reused, so retrying costs no extra serialization and cannot observe a mutated request.
/// </para>
/// <para>
/// Delays run through <see cref="TimeProvider"/>, which is what makes the retry behaviour testable
/// without real sleeping.
/// </para>
/// </remarks>
internal sealed class TypeSafeTransport : IDisposable
{
    // The longest delay Task.Delay accepts: uint.MaxValue - 1 milliseconds, about 49.7 days.
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    // ponytail: fixed cap, far above any real System One or models response; make it an option if
    // a legitimate response ever needs more.
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TypeSafeClientOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _apiKey;
    private readonly string _userAgent;

    public TypeSafeTransport(
        HttpClient httpClient,
        bool ownsHttpClient,
        TypeSafeClientOptions options,
        string apiKey)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _options = options;
        _apiKey = apiKey;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _logger = options.LoggerFactory?.CreateLogger("TypeSafe.Sdk") ?? NullLogger.Instance;
        _userAgent = BuildUserAgent(options.UserAgent);
    }

    /// <summary>
    /// Sends one logical request, retrying according to the effective policy.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="uri">The absolute request URI.</param>
    /// <param name="body">The serialized request body, or <see langword="null"/> for a bodyless request.</param>
    /// <param name="operation">A short operation name used in logs and telemetry.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="cancellationToken">Cancels the whole call, including pending retries.</param>
    /// <returns>The successful response.</returns>
    /// <exception cref="TypeSafeApiException">The API returned an unsuccessful response.</exception>
    /// <exception cref="TypeSafeConnectionException">The request never produced a response.</exception>
    public async Task<TypeSafeHttpResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        byte[]? body,
        string operation,
        TypeSafeRequestOptions? options,
        CancellationToken cancellationToken)
    {
        var policy = options?.Retry ?? _options.Retry;
        var attemptTimeout = options?.Timeout ?? _options.Timeout;
        TypeSafeClientOptions.ValidateTimeout(attemptTimeout);

        using var activity = TypeSafeTelemetry.ActivitySource.StartActivity(operation, ActivityKind.Client);
        activity?.SetTag("typesafe.operation", operation);
        activity?.SetTag("server.address", uri.Host);
        activity?.SetTag("url.full", uri.AbsoluteUri);

        var startedAt = _timeProvider.GetTimestamp();
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            var outcome = await AttemptAsync(method, uri, body, options, attemptTimeout, operation, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Response is { } success)
            {
                RecordSuccess(operation, success, attempt, startedAt, activity);
                return success;
            }

            var failure = outcome.Failure!;
            var elapsed = _timeProvider.GetElapsedTime(startedAt);

            if (!ShouldRetry(failure, attempt, policy, outcome.Retryable))
            {
                RecordFailure(operation, failure, attempt, elapsed, activity);
                throw failure;
            }

            // Task.Delay rejects anything above about 49.7 days, and an unbounded MaxRetryAfter lets a
            // server ask for up to TimeSpan.MaxValue. The budget check is written as a subtraction so
            // that it cannot overflow either.
            var delay = ComputeDelay(failure, attempt, policy, out var serverRequested);
            if (delay > MaxDelay)
            {
                delay = MaxDelay;
            }

            if (policy.TotalBudget is { } budget && delay >= budget - elapsed)
            {
                // The Python SDK stops before a retry whose delay would reach the budget and
                // re-raises the last error, which keeps worst-case latency bounded.
                RecordFailure(operation, failure, attempt, elapsed, activity);
                throw failure;
            }

            AnnounceRetry(operation, failure, delay, attempt, policy, serverRequested);

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Performs one attempt and classifies its outcome.
    /// </summary>
    /// <remarks>
    /// Transport-level failures are converted into the SDK's own exception types here rather than
    /// escaping as <see cref="HttpRequestException"/>, so callers only ever see the SDK's
    /// exception hierarchy.
    /// </remarks>
    private async Task<AttemptOutcome> AttemptAsync(
        HttpMethod method,
        Uri uri,
        byte[]? body,
        TypeSafeRequestOptions? options,
        TimeSpan attemptTimeout,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendOnceAsync(method, uri, body, options, attemptTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccess)
            {
                return new AttemptOutcome(response, null, Retryable: false);
            }

            var failure = TypeSafeErrorParser.Create(
                response.StatusCode,
                response.Body,
                response.Headers,
                response.RequestId,
                $"{method.Method} {uri.AbsoluteUri}");

            // A 4xx other than 408 or 429 is the caller's problem and will not improve on retry.
            var retryable = (options?.Retry ?? _options.Retry).HttpStatuses.Contains((int)response.StatusCode);

            return new AttemptOutcome(null, failure, retryable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled. That is not a failure to retry.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            var timeout = new TypeSafeTimeoutException(
                $"The TypeSafe {operation} request did not complete within " +
                $"{attemptTimeout.TotalSeconds:0.###} seconds.",
                ex)
            {
                Timeout = attemptTimeout,
            };

            return new AttemptOutcome(null, timeout, Retryable: true);
        }
        catch (TypeSafeResponseValidationException tooLarge)
        {
            // An oversized body will be oversized again, so the built-in rules do not retry it.
            return new AttemptOutcome(null, tooLarge, Retryable: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Reading the body as a stream surfaces a connection dropped mid-body as an IOException,
            // where buffering it used to wrap that in an HttpRequestException.
            var connection = new TypeSafeConnectionException(
                $"The TypeSafe API could not be reached: {ex.Message}",
                ex);

            return new AttemptOutcome(null, connection, Retryable: true);
        }
    }

    // Read in chunks against a fixed cap, because HttpClient.MaxResponseContentBufferSize does not
    // apply under ResponseHeadersRead and an endless body would otherwise grow until memory runs out.
    private static async Task<string> ReadBodyAsync(
        HttpResponseMessage response,
        Func<TypeSafeResponseValidationException> tooLarge,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw tooLarge();
        }

        using var buffer = new MemoryStream();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw tooLarge();
                }

                buffer.Write(chunk, 0, read);
            }
        }

        return buffer.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static TypeSafeResponseValidationException ResponseTooLarge(
        HttpResponseMessage response,
        string? requestId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        HttpMethod method,
        Uri uri) =>
        new($"The TypeSafe API response body exceeded the {MaxResponseBytes / (1024 * 1024)} MiB limit, " +
            "which usually means a proxy or a misconfigured BaseUrl is answering instead of the API.")
        {
            StatusCode = response.StatusCode,
            RequestId = requestId,
            Headers = headers,
            Endpoint = $"{method.Method} {uri.AbsoluteUri}",
        };

    private void AnnounceRetry(
        string operation,
        Exception failure,
        TimeSpan delay,
        int attempt,
        RetryPolicy policy,
        bool serverRequested)
    {
        TypeSafeTelemetry.Retries.Add(1, new KeyValuePair<string, object?>("typesafe.operation", operation));
        TypeSafeLog.Retrying(
            _logger,
            operation,
            delay.TotalMilliseconds,
            attempt,
            policy.MaxRetries,
            serverRequested ? "the server's Retry-After" : failure.GetType().Name);

        policy.OnRetry?.Invoke(new RetryAttempt(attempt, delay, failure));
    }

    private static string BuildUserAgent(string? extra)
    {
        var version = typeof(TypeSafeTransport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var token = $"TypeSafe.Sdk/{version}";

        return string.IsNullOrWhiteSpace(extra) ? token : $"{token} {extra}";
    }

    private async Task<TypeSafeHttpResponse> SendOnceAsync(
        HttpMethod method,
        Uri uri,
        byte[]? body,
        TypeSafeRequestOptions? options,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        ApplyHeaders(request, options);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (attemptTimeout > TimeSpan.Zero && attemptTimeout != Timeout.InfiniteTimeSpan)
        {
            timeoutSource.CancelAfter(attemptTimeout);
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
            .ConfigureAwait(false);

        var requestId = response.Headers.TryGetValues("x-typesafe-request-id", out var ids)
            ? ids.FirstOrDefault()
            : null;

        var headersSnapshot = Snapshot(response);

        // The body is read into memory so the response can be disposed immediately and the parsed
        // payload stays valid for the caller.
        var text = await ReadBodyAsync(
                response,
                () => ResponseTooLarge(response, requestId, headersSnapshot, method, uri),
                timeoutSource.Token)
            .ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            TypeSafeLog.ResponseBody(_logger, method.Method, text);
        }

        return new TypeSafeHttpResponse(response.StatusCode, text, requestId, headersSnapshot);
    }

    private void ApplyHeaders(HttpRequestMessage request, TypeSafeRequestOptions? options)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (_options.DefaultHeaders is { } defaults)
        {
            foreach (var (name, value) in defaults)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (options?.Headers is { } headers)
        {
            // Added last so a per-call header wins: TryAddWithoutValidation is a no-op when the
            // header is already present.
            foreach (var (name, value) in headers)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            // The guard above means Redact only runs when debug logging is on, which is exactly
            // what CA1873 asks for; the analyzer cannot see through the source-generated logger.
#pragma warning disable CA1873
            TypeSafeLog.RequestHeaders(_logger, request.Method.Method, Redact(request.Headers));
#pragma warning restore CA1873
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// Decides whether another attempt is worthwhile.
    /// </summary>
    /// <param name="failure">The failure from the attempt that just finished.</param>
    /// <param name="attempt">The one-based number of that attempt.</param>
    /// <param name="policy">The effective retry policy.</param>
    /// <param name="statusRetryable">Whether the built-in rules consider the status retryable.</param>
    /// <returns><see langword="true"/> when another attempt should be made.</returns>
    /// <remarks>
    /// <see cref="RetryPolicy.ShouldRetry"/> is additive, matching the sibling SDKs: returning
    /// <see langword="true"/> forces a retry even for a status the built-in rules reject, and
    /// returning <see langword="false"/> does not suppress one they allow.
    /// </remarks>
    private static bool ShouldRetry(Exception failure, int attempt, RetryPolicy policy, bool statusRetryable)
    {
        if (attempt > policy.MaxRetries)
        {
            return false;
        }

        if (policy.ShouldRetry?.Invoke(failure) == true)
        {
            return true;
        }

        if (!statusRetryable)
        {
            return false;
        }

        // A timeout is a kind of connection failure, so it has to be tested first.
        if (failure is TypeSafeTimeoutException)
        {
            return policy.RetryTimeoutErrors;
        }

        if (failure is TypeSafeConnectionException)
        {
            return policy.RetryConnectionErrors;
        }

        return true;
    }

    private static TimeSpan ComputeDelay(Exception failure, int attempt, RetryPolicy policy, out bool serverRequested)
    {
        serverRequested = false;

        if (policy.RespectRetryAfter &&
            failure is TypeSafeApiException { Headers: { } headers } &&
            TypeSafeErrorParser.ParseRetryAfter(headers) is { } requested &&
            requested <= policy.MaxRetryAfter)
        {
            serverRequested = true;
            return requested;
        }

        if (policy.BackoffInitial <= TimeSpan.Zero || policy.BackoffMax <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // attempt is one-based, so the first retry uses the initial delay and each subsequent one
        // doubles, capped at the maximum.
        var exponent = Math.Min(attempt - 1, 30);
        var scaled = policy.BackoffInitial.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(scaled, policy.BackoffMax.TotalMilliseconds);

        if (policy.BackoffJitter > 0)
        {
            // Subtracting a random fraction decorrelates clients that failed together, so they do
            // not all retry at the same instant.
            capped -= Random.Shared.NextDouble() * policy.BackoffJitter * capped;
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, capped));
    }

    private void RecordSuccess(
        string operation,
        TypeSafeHttpResponse response,
        int attempts,
        long startedAt,
        Activity? activity)
    {
        var elapsed = _timeProvider.GetElapsedTime(startedAt);

        activity?.SetTag("typesafe.retry.count", attempts - 1);
        activity?.SetTag("http.response.status_code", (int)response.StatusCode);

        TypeSafeTelemetry.Requests.Add(
            1,
            new KeyValuePair<string, object?>("typesafe.operation", operation),
            new KeyValuePair<string, object?>("typesafe.outcome", "success"));

        RecordDuration(operation, elapsed);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            TypeSafeLog.RequestCompleted(
                _logger,
                operation,
                (int)response.StatusCode,
                elapsed.TotalMilliseconds,
                response.RequestId);
        }
    }

    private void RecordFailure(
        string operation,
        Exception failure,
        int attempts,
        TimeSpan elapsed,
        Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Error, failure.Message);
        activity?.SetTag("typesafe.retry.count", attempts - 1);

        TypeSafeTelemetry.Requests.Add(
            1,
            new KeyValuePair<string, object?>("typesafe.operation", operation),
            new KeyValuePair<string, object?>("typesafe.outcome", "failure"));

        RecordDuration(operation, elapsed);

        if (failure is TypeSafeApiException api)
        {
            TypeSafeLog.RequestFailed(
                _logger,
                operation,
                (int)api.StatusCode,
                attempts,
                api.RequestId,
                api.Message);
        }
        else
        {
            TypeSafeLog.RequestErrored(_logger, operation, attempts, failure.Message);
        }
    }

    private static void RecordDuration(string operation, TimeSpan elapsed) =>
        TypeSafeTelemetry.Duration.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("typesafe.operation", operation));

    private static Dictionary<string, IReadOnlyList<string>> Snapshot(HttpResponseMessage response)
    {
        var snapshot = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in response.Headers)
        {
            snapshot[header.Key] = [.. header.Value];
        }

        foreach (var header in response.Content.Headers)
        {
            snapshot[header.Key] = [.. header.Value];
        }

        return snapshot;
    }

    /// <summary>
    /// Renders headers for the debug log, masking anything that could carry a credential.
    /// </summary>
    /// <remarks>
    /// Bodies are deliberately not redacted, matching the documented behaviour of the sibling SDKs.
    /// </remarks>
    private static string Redact(HttpRequestHeaders headers)
    {
        var builder = new StringBuilder();

        foreach (var header in headers)
        {
            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(header.Key).Append(": ").Append(IsSecret(header.Key) ? "***" : string.Join(", ", header.Value));
        }

        return builder.ToString();
    }

    private static bool IsSecret(string name) =>
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("key", StringComparison.OrdinalIgnoreCase);
}
