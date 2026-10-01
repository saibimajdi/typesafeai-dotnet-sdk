# API reference

This page is the compact reference for the public C# surface. It is maintained alongside the
implementation so documentation tools do not have to infer behaviour from API-compatibility
baseline files.

For task-oriented examples, start with the [quickstart](quickstart.md). For the complete semantics
of a specific area, follow the links in each section below.

## Packages and namespaces

| Package | Namespace | Purpose |
| --- | --- | --- |
| `TypeSafe.Sdk` | `TypeSafe` | Client, questions, answers, retries, errors, models, and JSON helpers. |
| `TypeSafe.Sdk` | `TypeSafe.Serialization` | `TypeSafeJson` and the public JSON converters. |
| `TypeSafe.Sdk.DependencyInjection` | `TypeSafe.DependencyInjection` | `IServiceCollection` and `IHttpClientFactory` integration. |

Both packages target .NET 8 and .NET 10. The client is asynchronous, thread-safe, and intended to
be reused.

## Create a client

```csharp
using TypeSafe;

// Resolves the API key from TYPESAFE_API_KEY.
using var client = new TypeSafeClient();

// An explicit key takes precedence over options and the environment.
using var explicitClient = new TypeSafeClient("ts_...");

// Configure the client directly.
using var configuredClient = new TypeSafeClient(new TypeSafeClientOptions
{
    ApiKey = "ts_...",
    BaseUrl = new Uri("https://api.typesafe.ai"),
    Model = "jev-latest",
    Timeout = TimeSpan.FromSeconds(10),
    Retry = RetryPolicy.Default,
});
```

The public constructors are:

```csharp
TypeSafeClient(TypeSafeClientOptions? options = null, HttpClient? httpClient = null)
TypeSafeClient(string apiKey, TypeSafeClientOptions? options = null, HttpClient? httpClient = null)
```

When the SDK creates the `HttpClient`, it owns and disposes it. A caller-supplied `HttpClient`
remains owned by the caller, keeps its caller-controlled settings, and is not disposed by
`TypeSafeClient`.

### `TypeSafeClientOptions`

| Property | Type | Default or source |
| --- | --- | --- |
| `ApiKey` | `string?` | `TYPESAFE_API_KEY`; required when the client is constructed. |
| `BaseUrl` | `Uri?` | `TYPESAFE_BASE_URL`, then `TYPESAFE_ENDPOINT`, then `https://api.typesafe.ai`. |
| `Model` | `string?` | `TYPESAFE_DEFAULT_MODEL`, then `jev-latest`. |
| `Timeout` | `TimeSpan` | 10 seconds per HTTP attempt. |
| `Retry` | `RetryPolicy` | `RetryPolicy.Default`. |
| `DefaultHeaders` | `IReadOnlyDictionary<string, string>?` | No extra headers. |
| `UserAgent` | `string?` | Appended to the SDK user-agent token when supplied. |
| `LoggerFactory` | `ILoggerFactory?` | No SDK logging when omitted. |
| `TimeProvider` | `TimeProvider?` | `TimeProvider.System`; injectable for testing. |
| `UseEnvironmentFallback` | `bool` | `true`. When `false`, no `TYPESAFE_*` variable is read: `ApiKey` must be set, and `BaseUrl` and `Model` use the SDK defaults. |

`BaseUrl` is the API root and must not include a version segment. The SDK appends
`/v1/systemone` and `/v1/models` itself.

## Submit a System One request

`ITypeSafeClient` and `TypeSafeClient` expose the same five overloads:

```csharp
Task<SystemOneResult> SystemOneAsync(
    SystemOneRequest request,
    CancellationToken cancellationToken = default)

Task<SystemOneResult> SystemOneAsync(
    string state,
    IEnumerable<Question> questions,
    CancellationToken cancellationToken = default)

Task<SystemOneResult> SystemOneAsync(
    JsonNode? state,
    IEnumerable<Question> questions,
    CancellationToken cancellationToken = default)

Task<SystemOneResult> SystemOneAsync<TState>(
    TState state,
    IEnumerable<Question> questions,
    JsonTypeInfo<TState> stateTypeInfo,
    CancellationToken cancellationToken = default)

Task<SystemOneResult> SystemOneAsync<TState>(
    TState state,
    IEnumerable<Question> questions,
    CancellationToken cancellationToken = default)
```

Use the `JsonTypeInfo<TState>` overload for trimming and Native AOT. The final generic overload uses
reflection and is annotated accordingly. A state must not be `null`, and every request must contain
at least one question with a unique id.

### `SystemOneRequest`

| Property | Type | Meaning |
| --- | --- | --- |
| `State` | `JsonNode?` | Required text, object, or array to evaluate. The value itself must not be `null`. |
| `Questions` | `IEnumerable<Question>` | Required non-empty collection with unique ids. |
| `Model` | `string?` | Model for this request, or the client default. |
| `Options` | `TypeSafeRequestOptions?` | Per-call retry, timeout, header, model, and body overrides. |
| `AdditionalProperties` | `IReadOnlyDictionary<string, JsonNode?>?` | Extra top-level request fields, merged last. |

### `TypeSafeRequestOptions`

| Property | Type | Meaning |
| --- | --- | --- |
| `Model` | `string?` | Overrides the client model for one call. `SystemOneRequest.Model` wins when both are set. |
| `Retry` | `RetryPolicy?` | Overrides the client retry policy. Use `RetryPolicy.None` to disable retries. |
| `Timeout` | `TimeSpan?` | Overrides the per-attempt timeout. |
| `Headers` | `IReadOnlyDictionary<string, string>?` | Merged over the client default headers. |
| `AdditionalBodyProperties` | `IReadOnlyDictionary<string, JsonNode?>?` | Extra top-level fields, merged last. |

See [Forward compatibility](forward-compatibility.md) before overriding a modelled field through an
additional-properties dictionary.

## Questions

All question ids are local map keys. They are not sent inside the individual question body.

| Type | Constructor shape | Answer |
| --- | --- | --- |
| `NoulQuestion` | `(id, instructions?, criteria?, additionalProperties?)` | `NoulAnswer` |
| `ChoiceQuestion` | `(id, instructions, labels, additionalProperties?)` or `(id, instructions, criteria, additionalProperties?)` | `ChoiceAnswer` |
| `ScoreQuestion` | `(id, instructions, levels, additionalProperties?)` | `ScoreAnswer` |
| `RawQuestion` | `(id, type, body)` | A known answer type or `UnknownAnswer` |

`QuestionSet` is an ordered collection that rejects duplicate ids when they are added. The client
also accepts any `IEnumerable<Question>` and validates it before making a network call.

See [Questions](questions.md) for criteria shapes, limits, and complete examples.

## Results and answers

`SystemOneResult` exposes all answers through `Answers` and typed views through `Nouls`, `Choices`,
`Scores`, and `UnknownAnswers`.

| Member | Meaning |
| --- | --- |
| `Get(id)` | Return any answer, or throw when the id is absent. |
| `Get(question)` | Return the answer type bound to an `IQuestion<TAnswer>`. |
| `TryGet(id, out answer)` | Non-throwing lookup by id. |
| `TryGet(question, out answer)` | Non-throwing, strongly typed lookup. |
| `Noul(id)`, `Choice(id)`, `Score(id)` | Return a specific answer kind. |
| `Contains(id)`, `Ids` | Inspect which answers were returned. |
| `Model` | Concrete model reported by the API. |
| `Usage` | Input, output, and total token counts when reported. |
| `RequestId` | Value of the `x-typesafe-request-id` response header. |
| `RawJson` | Complete response body, including fields not modelled by this SDK version. |

### Answer types

| Type | Important members |
| --- | --- |
| `NoulAnswer` | `Probability`; a noul deliberately has no confidence property. |
| `ChoiceAnswer` | `Label`, `Confidence`, `Probabilities`, `TopLabel`, `TopProbability`, `Ranked()`, `Top(n)`, `ProbabilityOf(label)`, `NormalizedEntropy()`. |
| `ScoreAnswer` | `Score`, `NormalizedScore`, `ExpectedLevel`, `Confidence`, `Variance`, `Legend`, `Probabilities`, `ProbabilityAtLevel(level)`, `NormalizedEntropy()`. |
| `UnknownAnswer` | `Type` and the complete answer object in `Raw`. |

Every answer also carries `Id`, `Type`, `AdditionalProperties`, and
`TryGetConfidence(out double)`. `TryGetConfidence` returns `false` for answer kinds that do not
report confidence.

See [Answers and confidence](answers-and-confidence.md) for distribution semantics and threshold
guidance.

## List models

```csharp
var result = await client.Models.ListAsync();

foreach (var model in result.Models)
{
    Console.WriteLine($"{model.Name} ({model.ReleaseDate}): {model.Description}");
}
```

`ModelsResult` also exposes `RequestId` and `RawJson`.

## Retries and errors

`RetryPolicy.Default` makes two retries after the initial attempt. It retries HTTP `408`, `429`,
and `500` through `599`, plus connection and per-attempt timeout failures. The first backoff is
500 milliseconds, the maximum backoff is 5 seconds, and the default retry-admission budget is
30 seconds.

`RetryAttempt.Attempt` is **one-based**: attempt `1` is the initial request that failed. `Delay` is
the wait before the next attempt, and `Exception` is the failure that triggered the retry.

Every SDK exception derives from `TypeSafeException`. HTTP failures derive from
`TypeSafeApiException`; connection failures use `TypeSafeConnectionException`, and per-attempt
timeouts use `TypeSafeTimeoutException`. A caller cancellation remains an
`OperationCanceledException`.

See [Retries and errors](retries-and-errors.md) for the complete policy defaults, exception table,
and catch ordering.

## Dependency injection

Install `TypeSafe.Sdk.DependencyInjection`, then register both the concrete client and interface:

```csharp
using TypeSafe.DependencyInjection;

builder.Services.AddTypeSafeClient(builder.Configuration);
```

The overloads are:

```csharp
IHttpClientBuilder AddTypeSafeClient(
    this IServiceCollection services,
    Action<TypeSafeClientOptions>? configure = null)

IHttpClientBuilder AddTypeSafeClient(
    this IServiceCollection services,
    IConfiguration configuration,
    Action<TypeSafeClientOptions>? configure = null)
```

The configuration overload binds the `TypeSafe` section. The returned `IHttpClientBuilder` can add
handlers or change the named `TypeSafe` client. The SDK remains responsible for retries; adding a
second retry handler can multiply the total number of attempts.

## HTTP wire contract

Normal applications should use `TypeSafeClient` rather than constructing HTTP requests directly.
The wire examples here document what the SDK sends and prevent tooling from inventing endpoint or
payload shapes.

System One requests use `POST /v1/systemone`. `questions` is an object keyed by question id, not an
array:

```json
{
  "state": "Help! My payouts have been failing for 3 days.",
  "model": "jev-latest",
  "questions": {
    "is_urgent": {
      "type": "noul",
      "instructions": "Does this convey urgency?"
    }
  }
}
```

The response is the result object itself. It is not wrapped in a `result` property:

```json
{
  "model": "jev-latest",
  "answers": {
    "is_urgent": {
      "type": "noul",
      "noul": 0.92
    }
  },
  "usage": {
    "input_tokens": 312,
    "output_tokens": 48
  }
}
```

The SDK sends the API key as a bearer token and records the optional
`x-typesafe-request-id` response header on results and API exceptions.
