# Quickstart

## 1. Install

```bash
dotnet add package TypeSafeAI.Sdk --prerelease
```

The dependency injection integration is a separate, optional package:

```bash
dotnet add package TypeSafeAI.Sdk.DependencyInjection --prerelease
```

Only prerelease versions are currently published, so `--prerelease` is required. Once a stable
version is available, omit the flag to install the latest stable release.

The package IDs and C# namespaces use the `TypeSafeAI` brand. The primary namespaces are
`TypeSafeAI` and `TypeSafeAI.DependencyInjection`.

## 2. Get an API key

Create a key in the [TypeSafe console](https://console.typesafe.ai/) and put it in the
`TYPESAFE_API_KEY` environment variable:

```bash
export TYPESAFE_API_KEY="ts_..."
```

```powershell
$env:TYPESAFE_API_KEY = "ts_..."
```

The SDK reads the same `TYPESAFE_*` variable names as the official TypeSafe Python and JavaScript
SDKs, so one set of variables configures every TypeSafe SDK in a deployment.

## 3. Ask your first questions

```csharp
using TypeSafeAI;

var client = new TypeSafeClient();               // reads TYPESAFE_API_KEY
var result = await client.SystemOneAsync(
    "My card was charged twice, please fix this ASAP.",
    [
        new NoulQuestion("is_urgent", "Does this convey urgency?"),
        new ChoiceQuestion("department", "Which team should handle this?", ["billing", "technical", "sales"]),
        new ScoreQuestion("frustration", "How frustrated is the customer?", ["Calm", "Frustrated", "Very angry"]),
    ]);
Console.WriteLine(result.Noul("is_urgent").Probability);
Console.WriteLine(result.Choice("department").Label);
Console.WriteLine(result.Score("frustration").Score);
```

Three questions, one HTTP request. Every question saw the same state, and each was answered
independently of the others.

`Probability` is a number from `0` to `1`. `Label` is one of the labels you supplied, echoed back
byte-for-byte. `Score` is the probability-weighted position on your rubric, so it can land between
levels — `1.6` on a three-level rubric is normal and means the probability mass sat between
"Frustrated" and "Very angry".

## 4. Read the answer you actually need

The result exposes a typed accessor per question kind. Use the one that matches the question, or
the question-bound generic form, which turns a renamed question into a compile error instead of a
runtime miss:

```csharp
using TypeSafeAI;

var urgency = new NoulQuestion("is_urgent", "Does this convey urgency?");
var department = new ChoiceQuestion("department", "Which team should handle this?", ["billing", "technical", "sales"]);

var result = await client.SystemOneAsync(state: "My card was charged twice.", questions: [urgency, department]);

// By id, bound to the answer type at compile time.
NoulAnswer isUrgent = result.Get(urgency);
ChoiceAnswer team = result.Get(department);

// By id, when the question object is not in scope.
Console.WriteLine(result.Noul("is_urgent").Probability);
Console.WriteLine(result.Choice("department").Label);
```

The API does not promise an answer for every question that was asked — a speculative question
whose answer was not needed can simply be absent. `TryGet` is the non-throwing form, and it is the
right default for anything you are not certain you will need:

```csharp
using TypeSafeAI;

if (result.TryGet(department, out var answer))
{
    Console.WriteLine(answer.Label);
}

// By id, when the question object is not in scope. Match on the answer kind before using it.
if (result.TryGet("is_urgent", out var byId) && byId is NoulAnswer noul)
{
    Console.WriteLine(noul.Probability);
}
```

`Ids` enumerates the ids that did come back, which is how you find out which speculative questions
went unanswered.

## 5. Configure the client

Values are resolved in this order: an explicitly set option, then the environment variable, then the
SDK default. Empty or whitespace-only environment values are treated as if they were unset.

| Environment variable | Option | Default |
| --- | --- | --- |
| `TYPESAFE_API_KEY` | `TypeSafeClientOptions.ApiKey` | none — required |
| `TYPESAFE_BASE_URL` | `TypeSafeClientOptions.BaseUrl` | `https://api.typesafe.ai` |
| `TYPESAFE_ENDPOINT` | `TypeSafeClientOptions.BaseUrl` | accepted as an alias; `TYPESAFE_BASE_URL` wins when both are set |
| `TYPESAFE_DEFAULT_MODEL` | `TypeSafeClientOptions.Model` | `jev-latest` |

```csharp
using TypeSafeAI;

var options = new TypeSafeClientOptions
{
    ApiKey = Environment.GetEnvironmentVariable("MY_SECRET_STORE_KEY"),
    BaseUrl = new Uri("https://gateway.internal.example/typesafe/"),
    Model = "jev-latest",
    Timeout = TimeSpan.FromSeconds(20),
    DefaultHeaders = new Dictionary<string, string>
    {
        ["x-tenant"] = "acme",
    },
    UserAgent = "acme-support-triage/2.1.0",
};

using var client = new TypeSafeClient(options, httpClient: null);
```

Create one client and reuse it. `TypeSafeClient` is thread-safe and holds a single pooled
`HttpClient`. Dispose it when your application shuts down; if you passed your own `HttpClient`, the
client leaves it alone and it stays yours to dispose.

Override any of it for a single call with `TypeSafeRequestOptions`:

```csharp
using TypeSafeAI;

var result = await client.SystemOneAsync(
    new SystemOneRequest
    {
        State = "My card was charged twice.",
        Questions = [new NoulQuestion("is_urgent", "Does this convey urgency?")],
        Model = "jev-latest",
        Options = new TypeSafeRequestOptions
        {
            Timeout = TimeSpan.FromSeconds(3),
            Retry = RetryPolicy.None,
            Headers = new Dictionary<string, string> { ["x-trace"] = "abc123" },
        },
    });
```

## 6. Send structured state

`state` does not have to be a string. `JsonNode` gives you full control over the shape that goes on
the wire:

```csharp
using System.Text.Json.Nodes;
using TypeSafeAI;

var state = new JsonObject
{
    ["subject"] = "Charged twice",
    ["body"] = "My card was charged twice, please fix this ASAP.",
    ["customer"] = new JsonObject
    {
        ["plan"] = "pro",
        ["tenure_months"] = 14,
    },
};

var result = await client.SystemOneAsync(
    state,
    [new NoulQuestion("is_urgent", "Does this convey urgency?")]);
```

For your own types, use the `JsonTypeInfo<TState>` overload. It is the trimming- and
ahead-of-time-compilation-safe path, because it does not need reflection at run time:

```csharp
using System.Text.Json.Serialization;
using TypeSafeAI;

internal sealed record Ticket(string Subject, string Body, string Plan);

[JsonSerializable(typeof(Ticket))]
internal partial class TicketJsonContext : JsonSerializerContext
{
}

internal static class Triage
{
    public static async Task<double> UrgencyAsync(Ticket ticket, TypeSafeClient client)
    {
        var result = await client.SystemOneAsync(
            ticket,
            [new NoulQuestion("is_urgent", "Does this convey urgency?")],
            TicketJsonContext.Default.Ticket);

        return result.Noul("is_urgent").Probability;
    }
}
```

The reflection-based overload, `SystemOneAsync(ticket, questions)`, also works. It is annotated as
requiring unreferenced and dynamic code access, so it warns under trimming and Native AOT. Note that
the state is serialized with its own property names preserved exactly as declared, deliberately
**not** with the SDK's snake_case policy: the state is your data, not the SDK's.

## 7. Register with dependency injection

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TypeSafeAI.DependencyInjection;

// In an ASP.NET Core application these are builder.Configuration and builder.Services.
IConfiguration configuration = new ConfigurationBuilder().Build();
var services = new ServiceCollection();

services.AddTypeSafeClient(configuration);
```

Add `TypeSafe` to configuration, from any provider, and the section is bound to
`TypeSafeClientOptions` after the environment variables have already been applied:

```json
{
  "TypeSafe": {
    "Model": "jev-latest",
    "Timeout": "00:00:20"
  }
}
```

To configure the client from this section only, set `"UseEnvironmentFallback": false` in it. The
client then reads no `TYPESAFE_*` environment variable, so `ApiKey` must be set and an unset
`BaseUrl` or `Model` uses the SDK default.

`AddTypeSafeClient` returns the `IHttpClientBuilder`, so a handler can be added to the pipeline. The
client is registered as a singleton on top of `IHttpClientFactory`, and the factory's own HTTP
logging has credential headers redacted, so turning on `Trace` logging does not write your API key
into your logs.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TypeSafeAI.DependencyInjection;

sealed class CorrelationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("x-correlation-id", Guid.NewGuid().ToString("n"));
        return base.SendAsync(request, cancellationToken);
    }
}

internal static class Registration
{
    public static void Add(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddTypeSafeClient(configuration, options => options.UserAgent = "acme-triage/2.1.0")
            .AddHttpMessageHandler<CorrelationHandler>();
    }
}
```

Do not attach a resilience handler that retries. The SDK's own retry policy is in charge, and
retrying at two layers multiplies attempts and makes the documented backoff and budget semantics
impossible to reason about.

## 8. List the available models

```csharp
using TypeSafeAI;

var models = await client.Models.ListAsync();
foreach (var model in models.Models)
{
    Console.WriteLine($"{model.Name} ({model.ReleaseDate}): {model.Description}");
}
```

Pin a model name from that list when a decision has to stay reproducible. `jev-latest` is an alias,
and `SystemOneResult.Model` reports the concrete version an alias resolved to, which can change
over time.

## 9. Handle failures

```csharp
using TypeSafeAI;

try
{
    var result = await client.SystemOneAsync(state, questions);
}
catch (TypeSafeRateLimitException ex)
{
    // The SDK already retried while its budget allowed. Back off for at least ex.RetryAfter.
    Console.Error.WriteLine($"Rate limited; retry after {ex.RetryAfter}.");
}
catch (TypeSafeApiException ex)
{
    // Any other unsuccessful response: ex.StatusCode, ex.ErrorType, ex.RequestId, ex.Endpoint.
    Console.Error.WriteLine($"{ex.StatusCode} with request id {ex.RequestId}.");
}
catch (TypeSafeConnectionException ex)
{
    // The request never produced a response: DNS, TLS, socket, or the per-attempt timeout.
    Console.Error.WriteLine($"Could not reach TypeSafe: {ex.Message}");
}
catch (TypeSafeException ex)
{
    // Everything else the SDK raises, including TypeSafeConfigurationException for a missing key.
    Console.Error.WriteLine(ex.Message);
}
```

`ArgumentNullException` and `ArgumentException` are thrown synchronously, before any network call,
for programmer errors: a null state, an empty choice, a rubric outside 2 to 10 levels. Those are
bugs in the caller, not API failures, and they are worth fixing rather than catching.

[retries-and-errors.md](retries-and-errors.md) has the full exception table and the retry knobs.

## Where to go next

- [questions.md](questions.md) — choosing between noul, choice, and score, and their documented
  limits.
- [answers-and-confidence.md](answers-and-confidence.md) — what confidence means here, and where
  thresholds belong.
- [patterns.md](patterns.md) — fan-out, confidence-gated routing, composite scoring, intent routing.
- [forward-compatibility.md](forward-compatibility.md) — using API features this SDK version does
  not model.
