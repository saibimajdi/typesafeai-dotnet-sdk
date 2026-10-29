# Retries and errors

## Retry policy

Retrying is safe for this API: `POST /v1/systemone` is a stateless evaluation call, and a response
that was never read is not billed to the caller. So the SDK retries by default, with the same
defaults as the official TypeSafe Python and JavaScript SDKs, so behaviour is identical across
languages.

`RetryPolicy.Default` is:

| Knob | Default | Meaning |
| --- | --- | --- |
| `MaxRetries` | `2` | Retries after the initial attempt, so at most three attempts in total. `0` disables retrying. |
| `BackoffInitial` | `500 ms` | The delay before the first retry. Doubles for each subsequent retry. |
| `BackoffMax` | `5 s` | The ceiling applied to the computed delay. |
| `BackoffJitter` | `0.25` | The fraction of each delay removed at random, from `0` to `1`. Jitter stops many clients from retrying in lockstep after a shared outage. |
| `HttpStatuses` | `408`, `429`, `500`–`599` | The status codes that are retried. |
| `RespectRetryAfter` | `true` | Whether a `Retry-After` or `retry-after-ms` header overrides the computed delay. |
| `MaxRetryAfter` | `60 s` | The longest server-requested delay the SDK will honour. A longer one is ignored and the computed backoff is used instead. |
| `RetryConnectionErrors` | `true` | Whether a `TypeSafeConnectionException` is retried. |
| `RetryTimeoutErrors` | `true` | Whether a `TypeSafeTimeoutException` is retried. |
| `TotalBudget` | `30 s` | Prevents a retry when its delay would reach the elapsed-time budget. It does not cancel an in-progress attempt. `null` removes the limit. |
| `ShouldRetry` | `null` | An extra predicate, consulted for every failure. |
| `OnRetry` | `null` | A callback invoked just before each retry. |

All of the properties are `init`-only, so a policy is configured with an object initializer and is
immutable once built:

```csharp
using TypeSafeAI;

var options = new TypeSafeClientOptions
{
    Retry = new RetryPolicy
    {
        MaxRetries = 4,
        BackoffInitial = TimeSpan.FromMilliseconds(250),
        BackoffMax = TimeSpan.FromSeconds(8),
        BackoffJitter = 0.5,
        MaxRetryAfter = TimeSpan.FromSeconds(30),
        TotalBudget = TimeSpan.FromSeconds(20),
        OnRetry = attempt => Console.WriteLine(
            $"attempt {attempt.Attempt} failed with {attempt.Exception.GetType().Name}; " +
            $"retrying in {attempt.Delay.TotalMilliseconds:F0} ms"),
    },
};
```

To turn retrying off entirely, use `RetryPolicy.None`, which is the same thing as
`new RetryPolicy { MaxRetries = 0 }`:

```csharp
using TypeSafeAI;

var options = new TypeSafeClientOptions { Retry = RetryPolicy.None };
```

Or for a single call, through `TypeSafeRequestOptions`:

```csharp
using TypeSafeAI;

var result = await client.SystemOneAsync(new SystemOneRequest
{
    State = "My card was charged twice.",
    Questions = [new NoulQuestion("is_urgent", "Does this convey urgency?")],
    Options = new TypeSafeRequestOptions
    {
        Retry = RetryPolicy.None,
        Timeout = TimeSpan.FromSeconds(2),
    },
});
```

### Retrying on your own condition

`ShouldRetry` is consulted for every failure and can only add retries, never remove them. It cannot
suppress a retry the built-in rules already allow:

```csharp
using System.Net;
using TypeSafeAI;

var policy = new RetryPolicy
{
    ShouldRetry = exception => exception is TypeSafeApiException { StatusCode: HttpStatusCode.Conflict },
};
```

### `Retry-After` handling

Both the delta-seconds and the HTTP-date forms of `Retry-After` are understood, and
`retry-after-ms` is checked first when present, matching the sibling SDKs. A server-requested delay
longer than `MaxRetryAfter` is ignored in favour of the computed backoff, and a retry whose delay
would reach or exceed the remaining `TotalBudget` is not attempted at all — the last error is
rethrown instead. This prevents a long server-requested delay from starting another attempt.

## Per-attempt timeout versus total budget

These are two different limits, and both are needed.

| Limit | Default | Bounds |
| --- | --- | --- |
| `TypeSafeClientOptions.Timeout` (or `TypeSafeRequestOptions.Timeout`) | `10 s` | **One HTTP attempt.** Applied with a linked `CancellationTokenSource`, so each retry gets a fresh full timeout. |
| `RetryPolicy.TotalBudget` | `30 s` | **Starting another retry.** Before waiting, the SDK checks elapsed time plus the next delay against this budget. |

`TotalBudget` is a retry-admission budget, not a hard deadline. An HTTP attempt that has already
started is allowed to run until its per-attempt timeout, so the complete call can finish after the
budget value. Use the caller's `CancellationToken` when the operation needs a strict end-to-end
deadline.

An `HttpClient` created by the SDK uses `Timeout.InfiniteTimeSpan`, and the SDK applies the
per-attempt limit itself. A caller-supplied `HttpClient` is not modified; if it has a shorter timeout,
that timeout can end an attempt before the SDK's configured limit. In either case the SDK surfaces a
`TypeSafeTimeoutException` rather than a bare `TaskCanceledException`.

The caller's `CancellationToken` cancels the entire call, including any pending retry. A cancelled
token surfaces as `OperationCanceledException`, deliberately distinct from `TypeSafeTimeoutException`
so that "I cancelled this" and "the server was too slow" are never confused:

```csharp
using TypeSafeAI;

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

try
{
    var result = await client.SystemOneAsync(state, questions, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("The caller cancelled; this was not a server timeout.");
}
catch (TypeSafeTimeoutException ex)
{
    Console.WriteLine($"An attempt exceeded {ex.Timeout}.");
}
```

## The exception hierarchy

Everything the SDK raises derives from `TypeSafeException`.

| Exception | Raised when | Retried by default |
| --- | --- | --- |
| `TypeSafeConfigurationException` | The client is not configured well enough to make a request — for example no API key was supplied and `TYPESAFE_API_KEY` is unset. Raised before any network call. | Never |
| `TypeSafeConnectionException` | The request never produced an HTTP response: DNS, TLS, socket, or a response that could not be read. | Yes |
| `TypeSafeTimeoutException` | One attempt exceeded its per-attempt timeout. Derives from `TypeSafeConnectionException` and adds `Timeout`. | Yes |
| `TypeSafeApiException` | The API returned an unsuccessful HTTP response, after any retries. | See the concrete types |
| `TypeSafeBadRequestException` | HTTP `400`. | No |
| `TypeSafeAuthenticationException` | HTTP `401` — the key is missing, malformed, or revoked. | No |
| `TypeSafePermissionDeniedException` | HTTP `403` — the key is valid but not allowed to do this. | No |
| `TypeSafeNotFoundException` | HTTP `404`. | No |
| `TypeSafeUnprocessableEntityException` | HTTP `422` — the request was well-formed but the API rejected its contents. | No |
| `TypeSafeRateLimitException` | HTTP `429`, after the retries were exhausted. Adds `RetryAfter`, a `TimeSpan?`. | Yes |
| `TypeSafeServerException` | HTTP `5xx`, after the retries were exhausted. | Yes |
| `TypeSafeResponseValidationException` | A **successful** response whose body was missing or structurally invalid, or a response of any status whose body exceeded 16 MiB. Usually a proxy, a captive portal, a wrong `BaseUrl`, or an incompatible API version rather than a transient fault. Adds `FieldPath`. Derives from `TypeSafeApiException`. | No |
| `TypeSafeApiException` itself | Any other unsuccessful status code. | No |

The hierarchy is `TypeSafeException` → `TypeSafeApiException` → the concrete HTTP-status types, so
`TypeSafeResponseValidationException` is a `TypeSafeApiException` too. A plain (non-derived)
`TypeSafeApiException` is what you get for a status the SDK has no specific type for, such as `409`.
The concrete type is chosen from the **status code alone**. The body's `detail.error_type` string is
surfaced through `ErrorType` but never used to select an exception type, because it is not part of
the documented contract and new values can appear at any time.

The one exception to choosing by status is the size of the body. A response body larger than 16 MiB
is rejected while it is being read, before its status is classified, so even an oversized `503`
raises `TypeSafeResponseValidationException`, with `StatusCode` still set. It is not retried. Real
API responses are a few kilobytes, so the limit only trips on something other than the API
answering, such as an endless body from a broken proxy.

An empty or unrecognised answer, by contrast, is not an error: unmodelled fields are preserved and
an unrecognised answer kind becomes `UnknownAnswer`. See
[forward-compatibility.md](forward-compatibility.md).

### Reading an API failure

```csharp
using TypeSafeAI;

try
{
    var result = await client.SystemOneAsync(state, questions);
}
catch (TypeSafeApiException ex)
{
    Console.Error.WriteLine($"status      {ex.StatusCode}");
    Console.Error.WriteLine($"error type  {ex.ErrorType}");
    Console.Error.WriteLine($"message     {ex.ErrorMessage}");
    Console.Error.WriteLine($"endpoint    {ex.Endpoint}");
    Console.Error.WriteLine($"request id  {ex.RequestId}");
    Console.Error.WriteLine($"docs        {ex.DocumentationUrl}");

    // The whole parsed error body, for anything the properties above do not cover.
    Console.Error.WriteLine($"body        {ex.Body}");

    // Response headers, with multiple values per header preserved.
    if (ex.Headers.TryGetValue("retry-after", out var values))
    {
        Console.Error.WriteLine($"retry-after {string.Join(", ", values)}");
    }

    // The raw detail, which the API sends as an object for application errors and as a plain
    // string for framework errors.
    Console.Error.WriteLine($"detail      {ex.Details?.Body}");
}
```

Every property is a snapshot taken when the response was read, so the exception stays valid after
the underlying `HttpResponseMessage` has been disposed, and `Endpoint` never contains credentials,
query parameters, or a fragment — it is safe to log and safe to paste into an issue.

`RequestId` is the value of the `x-typesafe-request-id` header. It is not a credential, it is what
TypeSafe support asks for when investigating a specific request, and it is safe to share publicly.
See [SECURITY.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/SECURITY.md) for the rest of that list.

### Catching in the right order

Derived types must be caught before their bases, or the base clause makes them unreachable:

```csharp
using TypeSafeAI;

try
{
    var result = await client.SystemOneAsync(state, questions);
}
catch (TypeSafeRateLimitException ex)
{
    Console.Error.WriteLine($"Rate limited; back off for at least {ex.RetryAfter}.");
}
catch (TypeSafeAuthenticationException)
{
    Console.Error.WriteLine("The API key is missing, malformed, or revoked.");
}
catch (TypeSafeApiException ex)
{
    Console.Error.WriteLine($"The API rejected the request: {ex.StatusCode}.");
}
catch (TypeSafeTimeoutException ex)
{
    Console.Error.WriteLine($"No response within {ex.Timeout}.");
}
catch (TypeSafeConnectionException ex)
{
    Console.Error.WriteLine($"Could not reach TypeSafe: {ex.Message}");
}
catch (TypeSafeException ex)
{
    Console.Error.WriteLine(ex.Message);
}
```

If you also want to distinguish a `TypeSafeResponseValidationException`, catch it **before**
`TypeSafeApiException`, because it derives from it — a base clause makes every derived clause after
it unreachable, and the compiler reports `CS0160`.

### Errors the SDK raises locally

Argument and state validation happens before any network call, mirroring the server's rules and
saving a round trip. These are bugs in the caller, not API failures, and they are worth fixing
rather than catching:

| Exception | Cause |
| --- | --- |
| `ArgumentException` | A null or whitespace question id; an empty choice; more than 255 choice options; a duplicate option label; a score rubric with fewer than 2 or more than 10 levels; empty `NoulCriteria`. |
| `ArgumentNullException` | A null state, questions collection, request, question, or id where one is required. |
| `ArgumentOutOfRangeException` | A negative `RetryPolicy` value; a `BackoffJitter` outside `0`–`1`; a negative count passed to `ChoiceAnswer.Top`. |
| `TypeSafeConfigurationException` | No API key could be resolved. |
| `KeyNotFoundException` | `Get`, `Noul`, `Choice`, or `Score` was called for an id that has no answer, or for an answer of a different kind. |
| `InvalidOperationException` | `Get<TAnswer>(question)` found an answer of a different kind than the question produces. |
| `ObjectDisposedException` | The client was used after `Dispose`. |

## Logging

The SDK logs through `Microsoft.Extensions.Logging`. Give it a factory and it records request
headers (with credential-shaped ones masked), retries, and a warning for every unrecognised answer
kind:

```csharp
using Microsoft.Extensions.Logging;
using TypeSafeAI;

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

var options = new TypeSafeClientOptions { LoggerFactory = loggerFactory };
using var client = new TypeSafeClient(options);
```

Any request header whose name contains `authorization`, `cookie`, `token`, `secret`, or `key` is
logged as `***`. Request and response bodies are not redacted — they are your data, and rewriting
them would make the logs untrustworthy for debugging. Do not enable debug logging of bodies in an
environment where those logs go somewhere you would not send the data itself.

`TypeSafeAI.Sdk.DependencyInjection` wires the container's `ILoggerFactory` in automatically, and
additionally stops `IHttpClientFactory`'s own handler from logging the `Authorization` header at
`Trace` level.
