using System.Net;
using System.Text.Json.Nodes;

namespace TypeSafeAI.Tests;

/// <summary>
/// Verifies the retry policy, including the documented defaults and every knob.
/// </summary>
public sealed class RetryTests
{
    /// <summary>
    /// A policy with backoff disabled, so attempt counting tests never sleep.
    /// </summary>
    private static RetryPolicy NoDelay(int maxRetries = 2) => new()
    {
        MaxRetries = maxRetries,
        BackoffInitial = TimeSpan.Zero,
        BackoffMax = TimeSpan.Zero,
    };

    [Fact]
    public void TheDefaultsMatchTheSiblingSdks()
    {
        var policy = RetryPolicy.Default;

        Assert.Equal(2, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.BackoffInitial);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.BackoffMax);
        Assert.Equal(0.25, policy.BackoffJitter);
        Assert.True(policy.RespectRetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.MaxRetryAfter);
        Assert.True(policy.RetryConnectionErrors);
        Assert.True(policy.RetryTimeoutErrors);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.TotalBudget);

        // 408, 429, and every 5xx.
        Assert.Contains(408, policy.HttpStatuses);
        Assert.Contains(429, policy.HttpStatuses);
        Assert.Contains(500, policy.HttpStatuses);
        Assert.Contains(529, policy.HttpStatuses);
        Assert.DoesNotContain(400, policy.HttpStatuses);
        Assert.DoesNotContain(422, policy.HttpStatuses);
    }

    [Fact]
    public void NoRetriesIsEquivalentToAZeroMaxRetriesPolicy()
    {
        Assert.Equal(0, RetryPolicy.None.MaxRetries);
        Assert.Same(RetryPolicy.None, RetryPolicy.None);
    }

    [Fact]
    public async Task ARateLimitedRequestIsRetriedAndCanSucceed()
    {
        var (client, handler) = TestClient.Create((attempt, _) => attempt < 3
            ? StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests)
            : StubHttpMessageHandler.Json(Fixtures.NoulResponse));

        var result = await client.SystemOneAsync(
            "text",
            [new NoulQuestion("is_urgent", "q?")],
            TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Attempts);
        Assert.Equal(0.92, result.Noul("is_urgent").Probability);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ClientErrorsAreNotRetried(HttpStatusCode statusCode)
    {
        var options = new TypeSafeClientOptions { Retry = NoDelay() };
        var (client, handler) = TestClient.Create(
            (_, _) => StubHttpMessageHandler.Json(Fixtures.FrameworkErrorBody, statusCode),
            options);

        await Assert.ThrowsAnyAsync<TypeSafeApiException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        // A 4xx other than 408 or 429 is the caller's problem and will not improve on retry.
        Assert.Equal(1, handler.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData((HttpStatusCode)529)]
    public async Task RetryableStatusesAreRetried(HttpStatusCode statusCode)
    {
        var options = new TypeSafeClientOptions { Retry = NoDelay(maxRetries: 1) };
        var (client, handler) = TestClient.Create(
            (_, _) => StubHttpMessageHandler.Json("""{"detail":"nope"}""", statusCode),
            options);

        await Assert.ThrowsAnyAsync<TypeSafeApiException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task RetryingIsDisabledByAZeroMaxRetriesPolicy()
    {
        var options = new TypeSafeClientOptions { Retry = RetryPolicy.None };
        var (client, handler) = TestClient.Create(
            (_, _) => StubHttpMessageHandler.Json("""{"detail":"nope"}""", HttpStatusCode.ServiceUnavailable),
            options);

        await Assert.ThrowsAsync<TypeSafeServerException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task BackoffDoublesPerAttemptAndIsReported()
    {
        var delays = new List<TimeSpan>();
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 3,
                BackoffInitial = TimeSpan.FromMilliseconds(1),
                BackoffMax = TimeSpan.FromSeconds(5),
                // Jitter is disabled so the schedule is exactly assertable.
                BackoffJitter = 0,
                OnRetry = attempt => delays.Add(attempt.Delay),
            },
        };

        var (client, _) = TestClient.Create(
            (_, _) => StubHttpMessageHandler.Json("""{"detail":"nope"}""", HttpStatusCode.ServiceUnavailable),
            options);

        await Assert.ThrowsAsync<TypeSafeServerException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(3, delays.Count);
        Assert.Equal(1, delays[0].TotalMilliseconds, precision: 6);
        Assert.Equal(2, delays[1].TotalMilliseconds, precision: 6);
        Assert.Equal(4, delays[2].TotalMilliseconds, precision: 6);
    }

    [Fact]
    public async Task BackoffIsCappedByTheMaximum()
    {
        var delays = new List<TimeSpan>();
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 3,
                BackoffInitial = TimeSpan.FromMilliseconds(1),
                BackoffMax = TimeSpan.FromMilliseconds(2),
                BackoffJitter = 0,
                OnRetry = attempt => delays.Add(attempt.Delay),
            },
        };

        var (client, _) = TestClient.Create(
            (_, _) => StubHttpMessageHandler.Json("""{"detail":"nope"}""", HttpStatusCode.ServiceUnavailable),
            options);

        await Assert.ThrowsAsync<TypeSafeServerException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal([1, 2, 2], delays.Select(static d => d.TotalMilliseconds));
    }

    [Fact]
    public async Task JitterOnlyEverShortensTheDelay()
    {
        var delays = new List<TimeSpan>();
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 1,
                BackoffInitial = TimeSpan.FromMilliseconds(100),
                BackoffMax = TimeSpan.FromSeconds(5),
                BackoffJitter = 0.25,
                OnRetry = attempt => delays.Add(attempt.Delay),
            },
        };

        var (client, _) = TestClient.Create(
            (_, _) => StubHttpMessageHandler.Json("""{"detail":"nope"}""", HttpStatusCode.ServiceUnavailable),
            options);

        await Assert.ThrowsAsync<TypeSafeServerException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        // Up to 25% is removed, never added, so clients that failed together do not retry in lockstep.
        Assert.InRange(delays[0].TotalMilliseconds, 75, 100);
    }

    [Fact]
    public async Task RetryAfterMillisecondsOverridesComputedBackoff()
    {
        var delays = new List<TimeSpan>();
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 1,
                BackoffInitial = TimeSpan.FromMilliseconds(1),
                BackoffMax = TimeSpan.FromMilliseconds(2),
                BackoffJitter = 0,
                OnRetry = attempt => delays.Add(attempt.Delay),
            },
        };

        var (client, _) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation("retry-after-ms", "7");
                return response;
            },
            options);

        await Assert.ThrowsAsync<TypeSafeRateLimitException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(7, delays[0].TotalMilliseconds);
    }

    [Fact]
    public async Task RetryAfterSecondsIsHonoured()
    {
        var delays = new List<TimeSpan>();
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 1,
                BackoffInitial = TimeSpan.FromMilliseconds(1),
                BackoffMax = TimeSpan.FromMilliseconds(2),
                BackoffJitter = 0,
                OnRetry = attempt => delays.Add(attempt.Delay),
            },
        };

        var (client, _) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation("Retry-After", "3");
                return response;
            },
            options);

        await Assert.ThrowsAsync<TypeSafeRateLimitException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(3, delays[0].TotalSeconds);
    }

    [Fact]
    public async Task AServerDelayLongerThanTheMaximumIsIgnored()
    {
        var delays = new List<TimeSpan>();
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 1,
                BackoffInitial = TimeSpan.FromMilliseconds(1),
                BackoffMax = TimeSpan.FromMilliseconds(2),
                BackoffJitter = 0,
                MaxRetryAfter = TimeSpan.FromSeconds(5),
                OnRetry = attempt => delays.Add(attempt.Delay),
            },
        };

        var (client, _) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation("Retry-After", "3600");
                return response;
            },
            options);

        await Assert.ThrowsAsync<TypeSafeRateLimitException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        // A one-hour server delay would blow the caller's latency budget, so the computed backoff
        // is used instead.
        Assert.Equal(1, delays[0].TotalMilliseconds);
    }

    [Theory]
    [InlineData("retry-after-ms", "1e100")]
    [InlineData("Retry-After", "1e100")]
    public async Task AServerDelayTooLargeForATimeSpanFallsBackToBackoff(string header, string value)
    {
        var delays = new List<TimeSpan>();
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 1,
                BackoffInitial = TimeSpan.FromMilliseconds(1),
                BackoffMax = TimeSpan.FromMilliseconds(2),
                BackoffJitter = 0,
                OnRetry = attempt => delays.Add(attempt.Delay),
            },
        };

        var (client, handler) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation(header, value);
                return response;
            },
            options);

        await Assert.ThrowsAsync<TypeSafeRateLimitException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        // The saturated server delay exceeds MaxRetryAfter, so the computed backoff is used.
        Assert.Equal(2, handler.Attempts);
        Assert.Equal(1, delays[0].TotalMilliseconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADelayAboveTheTaskDelayLimitIsCapped(bool serverRequested)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var delays = new List<TimeSpan>();
        var hundredDays = TimeSpan.FromDays(100);
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 1,
                MaxRetryAfter = hundredDays,
                BackoffInitial = hundredDays,
                BackoffMax = hundredDays,
                BackoffJitter = 0,
                TotalBudget = null,
                OnRetry = attempt =>
                {
                    delays.Add(attempt.Delay);

                    // The capped delay is still about 49.7 days; stop the test from waiting it out.
                    cancellation.Cancel();
                },
            },
        };

        var (client, _) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests);
                if (serverRequested)
                {
                    response.Headers.TryAddWithoutValidation("Retry-After", "8640000");
                }

                return response;
            },
            options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], cancellation.Token));

        Assert.Equal(TimeSpan.FromMilliseconds(uint.MaxValue - 1), Assert.Single(delays));
    }

    [Fact]
    public async Task AnUnboundedServerDelayRespectsTheTotalBudget()
    {
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 1,
                MaxRetryAfter = TimeSpan.MaxValue,
            },
        };

        var (client, handler) = TestClient.Create(
            (_, _) =>
            {
                var response = StubHttpMessageHandler.Json("""{"detail":"slow down"}""", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation("retry-after-ms", "1e100");
                return response;
            },
            options);

        // The saturated delay is longer than the budget, so the call stops instead of retrying.
        await Assert.ThrowsAsync<TypeSafeRateLimitException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task TheTotalBudgetStopsFurtherRetries()
    {
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 5,
                BackoffInitial = TimeSpan.Zero,
                BackoffMax = TimeSpan.Zero,
                TotalBudget = TimeSpan.Zero,
            },
        };

        var (client, handler) = TestClient.Create(
            (_, _) => StubHttpMessageHandler.Json("""{"detail":"nope"}""", HttpStatusCode.ServiceUnavailable),
            options);

        await Assert.ThrowsAsync<TypeSafeServerException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        // The budget is exhausted before the first retry, so the last error is rethrown rather than
        // waiting.
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task ConnectionFailuresAreRetried()
    {
        var options = new TypeSafeClientOptions { Retry = NoDelay() };
        var (client, handler) = TestClient.Create(
            (attempt, _) => attempt < 2
                ? throw new HttpRequestException("connection refused")
                : StubHttpMessageHandler.Json(Fixtures.NoulResponse),
            options);

        var result = await client.SystemOneAsync(
            "text",
            [new NoulQuestion("is_urgent", "q?")],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Attempts);
        Assert.Equal(0.92, result.Noul("is_urgent").Probability);
    }

    [Fact]
    public async Task ConnectionRetryingCanBeTurnedOff()
    {
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 3,
                BackoffInitial = TimeSpan.Zero,
                BackoffMax = TimeSpan.Zero,
                RetryConnectionErrors = false,
            },
        };

        var (client, handler) = TestClient.Create(
            (_, _) => throw new HttpRequestException("connection refused"),
            options);

        await Assert.ThrowsAsync<TypeSafeConnectionException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task APersistentConnectionFailureSurfacesAsAConnectionException()
    {
        var options = new TypeSafeClientOptions { Retry = NoDelay(maxRetries: 1) };
        var (client, handler) = TestClient.Create((_, _) => throw new HttpRequestException("no route to host"), options);

        var exception = await Assert.ThrowsAsync<TypeSafeConnectionException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task APredicateCanForceARetryForAnOtherwiseFatalStatus()
    {
        var options = new TypeSafeClientOptions
        {
            Retry = new RetryPolicy
            {
                MaxRetries = 1,
                BackoffInitial = TimeSpan.Zero,
                BackoffMax = TimeSpan.Zero,
                ShouldRetry = static failure =>
                    failure is TypeSafeApiException { StatusCode: HttpStatusCode.Conflict },
            },
        };

        var (client, handler) = TestClient.Create(
            (_, _) => StubHttpMessageHandler.Json("""{"detail":"conflict"}""", HttpStatusCode.Conflict),
            options);

        await Assert.ThrowsAnyAsync<TypeSafeApiException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task AnAlreadyCancelledCallDoesNotRetry()
    {
        var options = new TypeSafeClientOptions { Retry = NoDelay() };
        var (client, handler) = TestClient.Create(
            (_, _) => StubHttpMessageHandler.Json("""{"detail":"nope"}""", HttpStatusCode.ServiceUnavailable),
            options);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // A caller's cancellation is not a failure to retry, and it must not be converted into a
        // timeout: the caller has to be able to tell the two apart.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], cancellation.Token));

        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task PerCallRetryOverridesTheClientPolicy()
    {
        var options = new TypeSafeClientOptions { Retry = NoDelay(maxRetries: 5) };
        var (client, handler) = TestClient.Create(
            (_, _) => StubHttpMessageHandler.Json("""{"detail":"nope"}""", HttpStatusCode.ServiceUnavailable),
            options);

        await Assert.ThrowsAsync<TypeSafeServerException>(() => client.SystemOneAsync(new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "q?")],
            Options = new TypeSafeRequestOptions { Retry = RetryPolicy.None },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public void InvalidRetrySettingsAreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { MaxRetries = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { BackoffInitial = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { BackoffMax = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { BackoffJitter = 1.5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { BackoffJitter = -0.1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { MaxRetryAfter = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { TotalBudget = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentNullException>(() => new RetryPolicy { HttpStatuses = null! });
    }

    [Fact]
    public async Task RetriesReuseTheSameSerializedBody()
    {
        var bodies = new List<string?>();
        var options = new TypeSafeClientOptions { Retry = NoDelay(maxRetries: 1) };

        var (client, _) = TestClient.Create(
            (attempt, request) =>
            {
                bodies.Add(request.Body);
                return attempt < 2
                    ? StubHttpMessageHandler.Json("""{"detail":"nope"}""", HttpStatusCode.ServiceUnavailable)
                    : StubHttpMessageHandler.Json(Fixtures.NoulResponse);
            },
            options);

        await client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")], TestContext.Current.CancellationToken);

        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(bodies[0]!), JsonNode.Parse(bodies[1]!)));
    }
}
