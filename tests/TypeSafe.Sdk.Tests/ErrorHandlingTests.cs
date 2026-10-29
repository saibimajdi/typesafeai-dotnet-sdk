using System.Globalization;
using System.Net;

namespace TypeSafeAI.Tests;

/// <summary>
/// Verifies that an unsuccessful response becomes the right exception, carrying the right data.
/// </summary>
public sealed class ErrorHandlingTests
{
    private static TypeSafeClientOptions NoRetry() =>
        new() { Retry = RetryPolicy.None };

    [Fact]
    public async Task AuthenticationFailureBecomesTheAuthenticationException()
    {
        var (client, _) = TestClient.Returning(
            Fixtures.ApplicationErrorBody,
            HttpStatusCode.Unauthorized,
            NoRetry());

        var exception = await Assert.ThrowsAsync<TypeSafeAuthenticationException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("authentication_error", exception.ErrorType);
        Assert.Contains("Cannot authenticate", exception.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains("authentication_error", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, typeof(TypeSafeBadRequestException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(TypeSafeAuthenticationException))]
    [InlineData(HttpStatusCode.Forbidden, typeof(TypeSafePermissionDeniedException))]
    [InlineData(HttpStatusCode.NotFound, typeof(TypeSafeNotFoundException))]
    [InlineData(HttpStatusCode.UnprocessableEntity, typeof(TypeSafeUnprocessableEntityException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(TypeSafeRateLimitException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(TypeSafeServerException))]
    [InlineData((HttpStatusCode)529, typeof(TypeSafeServerException))]
    public async Task EachStatusMapsToItsDocumentedExceptionType(HttpStatusCode statusCode, Type expected)
    {
        var (client, _) = TestClient.Returning(
            Fixtures.ApplicationErrorBody,
            statusCode,
            new TypeSafeClientOptions { Retry = RetryPolicy.None });

        var exception = await Assert.ThrowsAnyAsync<TypeSafeApiException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        // The concrete type is chosen from the status code alone, because the status code is
        // documented and stable while error_type is neither.
        Assert.IsType(expected, exception);
        Assert.Equal(statusCode, exception.StatusCode);
    }

    [Fact]
    public async Task AFrameworkErrorWithABareStringDetailIsHandled()
    {
        var (client, _) = TestClient.Returning(Fixtures.FrameworkErrorBody, HttpStatusCode.NotFound, NoRetry());

        var exception = await Assert.ThrowsAsync<TypeSafeNotFoundException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        // The API's detail member is polymorphic: an object for application errors and a bare
        // string for framework errors.
        Assert.Equal("Not Found", exception.ErrorMessage);
        Assert.Null(exception.ErrorType);
    }

    [Fact]
    public async Task ANonJsonErrorBodyIsPreservedRatherThanThrown()
    {
        var (client, _) = TestClient.Create(
            (_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent(Fixtures.NonJsonErrorBody, System.Text.Encoding.UTF8, "text/html"),
                };

                response.Headers.TryAddWithoutValidation("x-typesafe-request-id", "req_proxy");
                return response;
            },
            NoRetry());

        var exception = await Assert.ThrowsAsync<TypeSafeServerException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        // Failing to parse an error must never mask the error, so the text is kept as the message.
        Assert.Contains("502 Bad Gateway", exception.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal("req_proxy", exception.RequestId);
    }

    [Fact]
    public async Task AnEmptyErrorBodyStillProducesAUsefulMessage()
    {
        var (client, _) = TestClient.Create(
            (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(string.Empty) },
            NoRetry());

        var exception = await Assert.ThrowsAsync<TypeSafeServerException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiExceptionsCarryTheRequestIdEndpointAndBody()
    {
        var (client, _) = TestClient.Returning(
            Fixtures.ApplicationErrorBody,
            HttpStatusCode.Unauthorized,
            NoRetry(),
            requestId: "req_01a0ab8265a27733a1bc672bfe98d906");

        var exception = await Assert.ThrowsAsync<TypeSafeAuthenticationException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal("req_01a0ab8265a27733a1bc672bfe98d906", exception.RequestId);
        Assert.Contains(exception.RequestId!, exception.Message, StringComparison.Ordinal);
        Assert.Equal("POST https://api.typesafe.ai/v1/systemone", exception.Endpoint);
        Assert.NotNull(exception.Body);
        Assert.Equal("https://docs.typesafe.ai/api", exception.DocumentationUrl);
        Assert.True(exception.Headers.ContainsKey("content-type"), "The response headers were not captured.");
    }

    [Fact]
    public async Task ARateLimitExceptionCarriesTheServerRequestedDelay()
    {
        var (client, _) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json(
                    """{"detail":{"error_type":"rate_limit_error","message":"Too many requests."}}""",
                    HttpStatusCode.TooManyRequests);

                response.Headers.TryAddWithoutValidation("Retry-After", "12");
                return response;
            },
            NoRetry());

        var exception = await Assert.ThrowsAsync<TypeSafeRateLimitException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        // The SDK retries 429s itself; seeing the exception means the retries were exhausted, so the
        // caller needs to know how long to wait.
        Assert.Equal(TimeSpan.FromSeconds(12), exception.RetryAfter);
    }

    [Fact]
    public async Task AnHttpDateRetryAfterIsUnderstood()
    {
        var when = DateTimeOffset.UtcNow.AddSeconds(30);

        var (client, _) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation("Retry-After", when.ToString("R"));
                return response;
            },
            NoRetry());

        var exception = await Assert.ThrowsAsync<TypeSafeRateLimitException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.NotNull(exception.RetryAfter);
        Assert.InRange(exception.RetryAfter!.Value.TotalSeconds, 25, 31);
    }

    [Theory]
    [InlineData("retry-after-ms", "1e100")]
    [InlineData("retry-after-ms", "1e309")]
    [InlineData("retry-after-ms", "Infinity")]
    [InlineData("Retry-After", "999999999999999999")]
    [InlineData("Retry-After", "Infinity")]
    public async Task ARetryAfterTooLargeForATimeSpanSaturates(string header, string value)
    {
        var (client, _) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation(header, value);
                return response;
            },
            NoRetry());

        // An out-of-range value used to escape as OverflowException instead of the rate-limit error.
        var exception = await Assert.ThrowsAsync<TypeSafeRateLimitException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.MaxValue, exception.RetryAfter);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("-Infinity")]
    public async Task ANonFiniteRetryAfterIsIgnored(string value)
    {
        var (client, _) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation("retry-after-ms", value);
                return response;
            },
            NoRetry());

        var exception = await Assert.ThrowsAsync<TypeSafeRateLimitException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Null(exception.RetryAfter);
    }

    [Theory]
    [InlineData("retry-after-ms", 0)]
    [InlineData("retry-after-ms", 1)]
    [InlineData("Retry-After", 0)]
    [InlineData("Retry-After", 1)]
    public async Task ARetryAfterSaturatesExactlyAtTheTimeSpanLimit(string header, double unitsBelowLimit)
    {
        var inMilliseconds = header == "retry-after-ms";
        var limit = inMilliseconds ? TimeSpan.MaxValue.TotalMilliseconds : TimeSpan.MaxValue.TotalSeconds;
        var value = limit - unitsBelowLimit;
        var (client, _) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation(header, value.ToString("R", CultureInfo.InvariantCulture));
                return response;
            },
            NoRetry());

        var exception = await Assert.ThrowsAsync<TypeSafeRateLimitException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        // At the limit the value saturates; one unit below it still converts exactly.
        var expected = unitsBelowLimit == 0
            ? TimeSpan.MaxValue
            : inMilliseconds ? TimeSpan.FromMilliseconds(value) : TimeSpan.FromSeconds(value);
        Assert.Equal(expected, exception.RetryAfter);
        Assert.Equal(unitsBelowLimit == 0, exception.RetryAfter == TimeSpan.MaxValue);
    }

    [Fact]
    public async Task EverySdkExceptionDerivesFromTheCommonBase()
    {
        var (client, _) = TestClient.Returning(Fixtures.ApplicationErrorBody, HttpStatusCode.Unauthorized, NoRetry());

        // One catch clause is enough to handle any SDK failure.
        var caught = await Assert.ThrowsAnyAsync<TypeSafeException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<TypeSafeApiException>(caught);
    }

    [Fact]
    public async Task AMissingApiKeyDoesNotReachTheNetwork()
    {
        using var environment = new EnvironmentScope()
            .Set(TypeSafeDefaults.ApiKeyEnvironmentVariable, null)
            .Set(TypeSafeDefaults.BaseUrlEnvironmentVariable, null);

        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(Fixtures.NoulResponse));

        // Configuration errors are raised before any request is built.
        Assert.Throws<TypeSafeConfigurationException>(() => new TypeSafeClient(options: null, new HttpClient(handler, disposeHandler: false)));
        Assert.Equal(0, handler.Attempts);

        await Task.CompletedTask;
    }

    // Mirrors the SDK's internal cap on a response body.
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    private static HttpResponseMessage Streaming(
        FakeBodyStream body,
        long? contentLength = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(statusCode) { Content = new StreamContent(body) };
        response.Content.Headers.ContentLength = contentLength;
        return response;
    }

    private static FakeBodyStream NoulBody(long length, Func<CancellationToken, Task>? atEnd = null) =>
        new(System.Text.Encoding.UTF8.GetBytes(Fixtures.NoulResponse), length, atEnd);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABodyAtTheSizeLimitIsAccepted(bool withContentLength)
    {
        var (client, _) = TestClient.Create(
            (_, _) => Streaming(NoulBody(MaxResponseBytes), withContentLength ? MaxResponseBytes : null),
            NoRetry());

        var result = await client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal(0.92, result.Noul("is_urgent").Probability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABodyOneByteOverTheSizeLimitIsRejectedAndNotRetried(bool withContentLength)
    {
        var (client, handler) = TestClient.Create(
            (_, _) => Streaming(NoulBody(MaxResponseBytes + 1L), withContentLength ? MaxResponseBytes + 1L : null));

        var exception = await Assert.ThrowsAsync<TypeSafeResponseValidationException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task AnOversizedContentLengthIsRejectedBeforeTheBodyIsRead()
    {
        var body = NoulBody(long.MaxValue);
        var (client, _) = TestClient.Create((_, _) => Streaming(body, MaxResponseBytes + 1L), NoRetry());

        await Assert.ThrowsAsync<TypeSafeResponseValidationException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(0, body.BytesRead);
    }

    [Fact]
    public async Task AnEndlessBodyStopsAtTheSizeLimit()
    {
        var body = NoulBody(long.MaxValue);
        var (client, handler) = TestClient.Create((_, _) => Streaming(body));

        await Assert.ThrowsAsync<TypeSafeResponseValidationException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")], TestContext.Current.CancellationToken));

        // The body is read in chunks, so it is consumed at most a chunk past the limit.
        Assert.InRange(body.BytesRead, MaxResponseBytes, MaxResponseBytes + (1024 * 1024));
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task AnOversizedErrorBodyIsRejectedBeforeItsStatusIsClassified()
    {
        var (client, handler) = TestClient.Create(
            (_, _) => Streaming(NoulBody(long.MaxValue), statusCode: HttpStatusCode.ServiceUnavailable));

        var exception = await Assert.ThrowsAsync<TypeSafeResponseValidationException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task AConnectionDroppedMidBodyIsARetryableConnectionError()
    {
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy { MaxRetries = 2, BackoffInitial = TimeSpan.Zero, BackoffMax = TimeSpan.Zero },
        };
        var (client, handler) = TestClient.Create(
            (_, _) => Streaming(NoulBody(1000, _ => throw new IOException("connection reset")), contentLength: 5000),
            options);

        await Assert.ThrowsAsync<TypeSafeConnectionException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(3, handler.Attempts);
    }

    [Fact]
    public async Task ABodyThatStallsHitsTheAttemptTimeout()
    {
        var options = new TypeSafeClientOptions { Retry = RetryPolicy.None, Timeout = TimeSpan.FromMilliseconds(200) };
        var (client, _) = TestClient.Create(
            (_, _) => Streaming(NoulBody(1000, ct => Task.Delay(Timeout.InfiniteTimeSpan, ct))),
            options);

        await Assert.ThrowsAsync<TypeSafeTimeoutException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellingDuringAStalledBodyCancelsTheCall()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var options = new TypeSafeClientOptions { Retry = RetryPolicy.None, Timeout = Timeout.InfiniteTimeSpan };
        var (client, _) = TestClient.Create(
            (_, _) => Streaming(NoulBody(1000, ct =>
            {
                cancellation.Cancel();
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            })),
            options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")], cancellation.Token));
    }
}
