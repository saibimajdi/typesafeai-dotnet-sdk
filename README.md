# TypeSafeAI .NET SDK

[![CI](https://github.com/saibimajdi/typesafeai-dotnet-sdk/actions/workflows/ci.yml/badge.svg)](https://github.com/saibimajdi/typesafeai-dotnet-sdk/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/TypeSafeAI.Sdk.svg)](https://www.nuget.org/packages/TypeSafeAI.Sdk)
[![NuGet downloads](https://img.shields.io/nuget/dt/TypeSafeAI.Sdk.svg)](https://www.nuget.org/packages/TypeSafeAI.Sdk)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-net8.0%20%7C%20net10.0-512BD4.svg)](#requirements)

Ask typed, answerable questions about any text or JSON state, and get structured,
probability-backed answers back: yes/no probabilities, a chosen option out of a set you
define, or a position on a rubric you define.

This is a community SDK. It is an independent client for the TypeSafe AI System One HTTP API,
written and maintained by the community.

Links below point at the repository, because NuGet renders this file as a standalone page where
relative paths do not resolve.

> [!IMPORTANT]
> **This project is not affiliated with, sponsored by, or endorsed by TypeSafe AI.**
> It is an independent, community-maintained client library. "TypeSafe" and "System One" are
> used only to describe the API this library talks to. For the API, the service, the models,
> and anything about accounts, billing, or uptime, contact TypeSafe AI directly through
> [typesafe.ai](https://typesafe.ai/) and [console.typesafe.ai](https://console.typesafe.ai/).
> See [SUPPORT.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/SUPPORT.md) for which venue to use for what.

## Why this SDK

- **Typed answers, not prose.** Every question is declared with a kind and answered inside the
  constraints you supplied, so nothing has to be parsed out of generated text.
- **One round trip for many questions.** Questions in a single request are evaluated in
  parallel against the same state. Adding a speculative question is nearly free, which makes
  fan-out and confidence-gated routing practical instead of expensive.
- **Confidence is reported, never invented.** When the API reports a confidence, the SDK
  surfaces it verbatim and never recomputes or substitutes it.
- **Nothing is lost relative to the raw HTTP API.** Unmodelled response and request fields,
  unknown answer kinds, and new question kinds all survive with
  [`RawJson`](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/forward-compatibility.md), `RawQuestion`, and `UnknownAnswer`.
- **Forward compatible by default.** A new answer kind does not fail the response; the other
  answers in the same payload are unaffected.
- **Trimming- and AOT-clean.** The library is annotated, analyzer-clean, and ships a
  source-generator-friendly overload for serializing your own state types.
- **Batteries included, dependencies minimal.** The core package depends on exactly one
  package, `Microsoft.Extensions.Logging.Abstractions`. Retry, backoff, `Retry-After`
  handling, and per-attempt timeouts are implemented in the library.
- **Built for .NET 8 and .NET 10**, with nullable reference types, XML documentation on every
  public member, and a public API surface that is checked in and enforced at build time.

## Install

```bash
dotnet add package TypeSafeAI.Sdk --prerelease
```

```bash
dotnet add package TypeSafeAI.Sdk.DependencyInjection --prerelease
```

The DI package is optional and adds `IServiceCollection` registration on top of
`IHttpClientFactory`.

Only prerelease versions are currently published, so `--prerelease` is required. Once a stable
version is available, omit the flag to install the latest stable release.

## 60-second quickstart

Get an API key from the [TypeSafe console](https://console.typesafe.ai/) and put it in the
`TYPESAFE_API_KEY` environment variable:

```bash
export TYPESAFE_API_KEY="apikey_..."
```

```powershell
$env:TYPESAFE_API_KEY = "apikey_..."
```

Then:

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

That is the whole loop. `Probability` is a number from `0` to `1`, `Label` is one of the
options you supplied, and `Score` is the probability-weighted position along your rubric, so
it can land between levels.

The NuGet packages and C# namespaces now use the `TypeSafeAI` brand. Existing users must update
both their package references and `using` directives; see the
[package migration guide](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/package-migration.md).

Continue with [docs/quickstart.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/quickstart.md) for configuration, dependency
injection, structured state, and error handling.

## At a glance

### Client

| Type | Purpose |
| --- | --- |
| `TypeSafeClient` | The default client. Thread-safe, reuses one pooled `HttpClient`, implements `IDisposable`. |
| `ITypeSafeClient` | The interface to depend on. `TypeSafeClient` is the shipped implementation. |
| `TypeSafeClientOptions` | API key, base URL, default model, per-attempt timeout, retry policy, headers, `ILoggerFactory`. |
| `TypeSafeRequestOptions` | Per-call overrides of model, retry, timeout, headers, and extra body fields. |
| `SystemOneRequest` | A whole request — state, questions, model, options, extra body fields — as one object. |

`SystemOneAsync` has five overloads: a `SystemOneRequest`, a `string` state, a `JsonNode?`
state, an arbitrary `TState` with a `JsonTypeInfo<TState>` (trim/AOT-safe), and an arbitrary
`TState` using reflection.

### Questions

| Type | Answer type | Wire `type` |
| --- | --- | --- |
| `NoulQuestion` | `NoulAnswer` | `noul` |
| `ChoiceQuestion` | `ChoiceAnswer` | `choice` |
| `ScoreQuestion` | `ScoreAnswer` | `score` |
| `BoxQuestion` | `BoxAnswer` | `box` (experimental, needs an image) |
| `RawQuestion` | `UnknownAnswer` (or a modelled answer if the kind matches) | caller-supplied |
| `QuestionSet` | — | an ordered, duplicate-rejecting collection of questions |
| `NoulCriteria` | — | optional descriptions of what yes and no mean |

A `NoulQuestion` asks a yes/no question. A `ChoiceQuestion` selects one label from a set of up
to 255 you supply. A `ScoreQuestion` places the state on an ordered rubric of 2 to 10 levels. A `BoxQuestion` finds where something is in an image sent through
`SystemOneRequest.Assets` as an `ImageAsset`; images and Box questions are experimental API features.
See [docs/questions.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/questions.md).

### Answers

| Type | Members |
| --- | --- |
| `SystemOneResult` | `Noul(id)`, `Choice(id)`, `Score(id)`, `Box(id)`, `Get(id)`, `TryGet(id, …)`, `Get<TAnswer>(question)`, `TryGet<TAnswer>(question, …)`, `Contains(id)`, `Ids`, `Answers`, `Nouls`, `Choices`, `Scores`, `Boxes`, `UnknownAnswers`, `Model`, `Usage`, `RequestId`, `RawJson` |
| `NoulAnswer` | `Probability` — a calibrated probability from `0` to `1`. No confidence; see below. |
| `ChoiceAnswer` | `Label`, `Confidence`, `Probabilities`, `TopLabel`, `TopProbability`, `ProbabilityOf`, `ProbabilityOrDefault`, `TryGetProbability`, `Ranked()`, `Top(n)`, `NormalizedEntropy()` |
| `ScoreAnswer` | `Score`, `Confidence`, `Legend`, `Probabilities`, `LevelCount`, `MaxLevel`, `NormalizedScore`, `ExpectedLevel`, `Variance`, `ProbabilityAtLevel`, `TryGetProbability`, `LegendAtLevel`, `LegendTextAtLevel`, `NormalizedEntropy()` |
| `BoxAnswer` | `Top`, `Bottom`, `Left`, `Right`, `Width`, `Height`, `Confidence` (nullable) |
| `UnknownAnswer` | `Type`, `Raw` — for answer kinds newer than this SDK release |
| `Answer` | `Id`, `Type`, `AdditionalProperties`, `TryGetConfidence(out double)` |

Every answer type carries `Id` and `AdditionalProperties`, the response fields this SDK
version does not model.

### Combining answers in code

| Type | Purpose |
| --- | --- |
| `CompositeScore.Weighted(parts)` | Weighted mean of several `ScoreAnswer`s, each normalised by its own rubric length. |
| `CompositeScore.Profiles(parts, profiles)` | Several named weightings over the same answers. |
| `WeightedScore` | One `ScoreAnswer` plus its relative weight. |
| `ProbabilityMath` | `NormalizedEntropy`, `ExpectedLevel`, `Variance` for callers who want their own statistic. |

See [docs/patterns.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/patterns.md) for fan-out, confidence-gated routing, composite
scoring, and intent routing.

### Configuration and diagnostics

| Type | Purpose |
| --- | --- |
| `RetryPolicy` | Retry count, backoff, jitter, retryable status codes, `Retry-After` handling, total budget. |
| `RetryAttempt` | The attempt number, the delay, and the failure, passed to `RetryPolicy.OnRetry`. |
| `TypeSafeDefaults` | Environment variable names and the defaults applied when nothing overrides them. |
| `TypeSafeJson` | `Options`, plus `Serialize`/`Deserialize` helpers for results, answers, and questions. |
| `Usage` | `InputTokens`, `OutputTokens`, `TotalTokens`, when the API reports them. |
| `IModelsResource`, `ModelsResult`, `ModelMetadata` | `client.Models.ListAsync()` — the models available to the account. |

### Errors

Every error the SDK raises derives from `TypeSafeException`. Authentication, permissions,
bad requests, not found, unprocessable entities, rate limits, and server errors derive from
`TypeSafeApiException` and carry `StatusCode`, `Details`, `RequestId`, `Endpoint`, `Headers`,
`DocumentationUrl`, and the raw `Body`. See
[docs/retries-and-errors.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/retries-and-errors.md) for the full table.

### Dependency injection

```csharp
using TypeSafeAI.DependencyInjection;

builder.Services.AddTypeSafeClient(builder.Configuration);
```

`AddTypeSafeClient` registers `ITypeSafeClient` and `TypeSafeClient` as singletons on top of
`IHttpClientFactory`, binds the `TypeSafe` configuration section, and redacts credentials from
the framework's own HTTP logging.

## Confidence, and what it is not

`ChoiceAnswer.Confidence` and `ScoreAnswer.Confidence` are values the **API reported**. The
SDK reads them from the wire and never recomputes, adjusts, or substitutes them.
`NoulAnswer` has no confidence at all — a noul's `Probability` is itself the signal, and the
SDK deliberately offers no confidence member rather than inventing one.

`NormalizedEntropy()`, `ProbabilityMath`, `Variance`, and `ExpectedLevel` exist for callers who
want to compute their own statistic from the full distribution. They are **not** the API's
confidence, and the SDK never uses them to populate it. Thresholds belong in your code, next
to the decision they gate. Read
[docs/answers-and-confidence.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/answers-and-confidence.md) before wiring a threshold
into production.

## Requirements

| Requirement | Value |
| --- | --- |
| Target frameworks | `net8.0`, `net10.0` |
| .NET SDK to build from source | .NET 10 SDK (`10.0.100` or later) |
| Core package dependencies | `Microsoft.Extensions.Logging.Abstractions` only |
| DI package dependencies | the core package, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Configuration.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions` |
| Licence | MIT |

The SDK uses only .NET 8-era APIs (`JsonNode`, `TimeProvider`,
`JsonNamingPolicy.SnakeCaseLower`, `[JsonExtensionData]`), so both target frameworks are
first-class and neither is a compatibility shim. The `net8.0` asset is dropped in the first
release after .NET 8 reaches end of support.

## Configuration

Values are resolved in this order: an explicitly set option, then the environment variable,
then the SDK default. Empty or whitespace-only environment values are treated as unset.

| Environment variable | Option | Default |
| --- | --- | --- |
| `TYPESAFE_API_KEY` | `TypeSafeClientOptions.ApiKey` | none — required |
| `TYPESAFE_BASE_URL` | `TypeSafeClientOptions.BaseUrl` | `https://api.typesafe.ai` |
| `TYPESAFE_ENDPOINT` | `TypeSafeClientOptions.BaseUrl` | accepted as an alias; `TYPESAFE_BASE_URL` wins |
| `TYPESAFE_DEFAULT_MODEL` | `TypeSafeClientOptions.Model` | `jev-latest` |

The same names configure the official TypeSafe Python and JavaScript SDKs, so one set of
variables configures every TypeSafe SDK in a polyglot deployment.

## Documentation

Browse the **[SDK documentation site](https://saibimajdi.github.io/typesafeai-dotnet-sdk/)**
for searchable guides, code examples, and a suggested reading order. The same guides are
available below as Markdown in this repository.

| Document | What it covers |
| --- | --- |
| [docs/README.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/README.md) | Index of every document, and the suggested reading order. |
| [docs/quickstart.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/quickstart.md) | Install, configure, first request, DI, structured state, errors. |
| [docs/questions.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/questions.md) | Noul, choice, and score questions, with their documented limits. |
| [docs/answers-and-confidence.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/answers-and-confidence.md) | The answer types, confidence semantics, and threshold guidance. |
| [docs/patterns.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/patterns.md) | Fan-out, confidence-gated routing, composite scoring, intent routing. |
| [docs/forward-compatibility.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/forward-compatibility.md) | Extra fields, `RawQuestion`, `UnknownAnswer`, `RawJson`. |
| [docs/retries-and-errors.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/retries-and-errors.md) | Retry knobs and defaults, the exception hierarchy, timeouts and budgets. |
| [docs/package-migration.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/docs/package-migration.md) | Move from the former NuGet package IDs and C# namespaces. |

## Contributing

Contributions are welcome. Start with [CONTRIBUTING.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/CONTRIBUTING.md) for the development
setup, the coding standards, and how to add a public API. By taking part you agree to the
[Code of Conduct](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/CODE_OF_CONDUCT.md).

- Bugs and feature requests: [open an issue](https://github.com/saibimajdi/typesafeai-dotnet-sdk/issues/new/choose).
- Questions and ideas: [start a discussion](https://github.com/saibimajdi/typesafeai-dotnet-sdk/discussions).
- Security reports: **not** in a public issue — follow [SECURITY.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/SECURITY.md).

Change history lives in [CHANGELOG.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/CHANGELOG.md).
Maintainers cutting a release should follow
[RELEASING.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/RELEASING.md).

## Licence

MIT. See [LICENSE](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/LICENSE).
