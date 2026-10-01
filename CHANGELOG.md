# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Everything listed here is the first planned release, [0.1.0]. The SDK is pre-1.0: while the
version is `0.x`, a breaking change increments the minor version, and the public API surface is
tracked per project in `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` so that no change to
it can happen by accident.

### Fixed

- Installation commands now include `--prerelease` for both NuGet packages so they work while
  only prerelease versions are published.
- A `retry-after-ms` or `Retry-After` value too large for a `TimeSpan`, such as `1e100` or
  `Infinity`, no longer escapes as an `OverflowException`. It saturates to `TimeSpan.MaxValue`,
  so the `TypeSafeRateLimitException` is still thrown, and retries fall back to the computed
  backoff when the value exceeds `MaxRetryAfter`.
- A `usage` token count that does not fit an `int`, such as `2147483648` or `1.5`, no longer fails
  the whole response with a `FormatException`. The count reads as `null` and the original value
  stays available in `RawJson`. `Usage.TotalTokens` likewise reads as `null` when the sum of the
  two counts does not fit an `int`, instead of wrapping around to a negative number.
- A retry delay above about 49.7 days, the longest `Task.Delay` accepts, is now capped there. Before,
  a large `MaxRetryAfter` or `BackoffMax` with no `TotalBudget` made `Task.Delay` throw
  `ArgumentOutOfRangeException`, and `MaxRetryAfter = TimeSpan.MaxValue` let a saturated
  `Retry-After` overflow the budget check with an `OverflowException` instead of stopping with the
  `TypeSafeRateLimitException`.
- Serializing an `UnknownAnswer` no longer writes each of its fields twice. The fields came out
  of both `Raw` and `AdditionalProperties`, producing duplicate keys that strict JSON parsers
  reject and that multiplied on every cache round trip. `Raw` wins a name clash; an additional
  property that `Raw` does not have is still written.
- `new TypeSafeClient(apiKey, options)` no longer writes `apiKey` into the caller's `options`.
  Before, a later client built from the same options object authenticated with that key. The
  client now keeps a copy of the options it is given, so changing the object after construction
  no longer affects a client that already exists. That includes editing the `DefaultHeaders`
  dictionary in place, and `RetryPolicy` now keeps its own copy of `HttpStatuses`.
- Serializing a `SystemOneResult` no longer drops the response fields the SDK does not model, so a
  cached result is complete when it is read back, as the forward-compatibility guide promises.
  Unknown top-level fields, unknown `usage` fields, a `usage` value that is not an object, and a
  token count that read as `null` because it did not fit an `int` are all copied from `RawJson`.
  The model, answers and the other token counts are still written from the typed properties, so
  edits to an answer's `AdditionalProperties` are kept.
- A response body is now limited to 16 MiB. Before, an endless or huge body, from a broken proxy or
  a wrong `BaseUrl`, was buffered until it hit .NET's 2 GB limit, and the resulting connection
  error was retried, so one call could allocate several gigabytes. An oversized body now fails
  with a `TypeSafeResponseValidationException`, which is not retried; a `Content-Length` over the
  limit fails before the body is read.

### Changed

- The NuGet package IDs are now `TypeSafeAI.Sdk` and
  `TypeSafeAI.Sdk.DependencyInjection`. The public C# namespaces are now `TypeSafeAI`,
  `TypeSafeAI.Serialization`, and `TypeSafeAI.DependencyInjection`; assembly names remain
  unchanged. This is a source-breaking migration; see
  [the package migration guide](docs/package-migration.md).
- Repository metadata and documentation now use the canonical
  `saibimajdi/typesafeai-dotnet-sdk` GitHub repository.

### Added

- `TypeSafeClientOptions.UseEnvironmentFallback`, `true` by default. Set it to `false` and the
  client reads no `TYPESAFE_*` environment variable, so a stray variable on the host cannot change
  the API key, the endpoint or the model: a missing `ApiKey` throws
  `TypeSafeConfigurationException`, and an unset `BaseUrl` or `Model` uses the SDK default. It
  binds from `TypeSafe:UseEnvironmentFallback` in configuration.
- A searchable SDK documentation site with light/dark themes and automated GitHub Pages
  deployment, built from the existing Markdown guides and validated on pull requests.

- `TypeSafeClient` and `ITypeSafeClient`: an asynchronous client for the System One API, with five
  `SystemOneAsync` overloads — `SystemOneRequest`, `string` state, `JsonNode?` state, and an
  arbitrary object either with a source-generated `JsonTypeInfo<TState>` for trimming and
  ahead-of-time compilation, or through reflection.
- `NoulQuestion` and `NoulAnswer`: yes/no questions answered with a calibrated probability from
  `0` to `1`. Noul answers carry no confidence; the probability is the signal.
- `ChoiceQuestion` and `ChoiceAnswer`: a selection from up to 255 caller-supplied options, with the
  full probability distribution, the API's confidence, ranked and top-`n` views, and per-option
  lookups that distinguish "absent" from "zero".
- `ScoreQuestion` and `ScoreAnswer`: position on an ordered rubric of 2 to 10 levels, with the
  legend echoed back, the distribution, the score, confidence, `NormalizedScore`, `ExpectedLevel`,
  and `Variance`.
- `NoulCriteria` for describing what yes and no mean, and `QuestionSet` for assembling questions
  dynamically with duplicate-id rejection.
- `SystemOneResult` with typed accessors (`Noul`, `Choice`, `Score`), question-bound generic
  lookups (`Get<TAnswer>`, `TryGet<TAnswer>`), `TryGet` by id, `Contains`, `Ids`, the per-kind
  dictionaries, `Model`, `Usage`, `RequestId`, and the complete `RawJson`.
- `CompositeScore.Weighted` and `CompositeScore.Profiles`, plus `WeightedScore`, for combining
  several Score answers into one judgment using weights held in caller code.
- `ProbabilityMath` with `NormalizedEntropy`, `ExpectedLevel`, and `Variance` for callers who want
  to compute their own statistic from a full distribution. These are documented as **not** being
  the API's confidence, and the SDK never uses them to populate it.
- `RetryPolicy` with the documented defaults — two retries, 500 ms initial backoff doubling to a
  5 s ceiling, up to 25% jitter, retries on 408/429/5xx, `Retry-After` and `retry-after-ms`
  honoured up to 60 s, and a 30 s total budget — plus per-call overrides through
  `TypeSafeRequestOptions` and `RetryPolicy.None`.
- A complete exception hierarchy rooted at `TypeSafeException`: `TypeSafeApiException` with
  `StatusCode`, `Details`, `RequestId`, `Endpoint`, `Headers`, `DocumentationUrl`, and the raw
  `Body`, plus `TypeSafeAuthenticationException`, `TypeSafePermissionDeniedException`,
  `TypeSafeBadRequestException`, `TypeSafeNotFoundException`, `TypeSafeUnprocessableEntityException`,
  `TypeSafeRateLimitException` with `RetryAfter`, `TypeSafeServerException`,
  `TypeSafeConnectionException`, `TypeSafeTimeoutException`, `TypeSafeConfigurationException`, and
  `TypeSafeResponseValidationException`.
- Forward compatibility throughout: unmodelled request and response fields are preserved through
  `AdditionalProperties` and `AdditionalBodyProperties`, `RawJson` exposes the whole response body,
  `RawQuestion` sends a question kind the SDK does not model, and a new answer kind deserializes to
  `UnknownAnswer` instead of failing the response.
- `TypeSafeJson` with cached `Options` and annotation-free `Serialize`/`Deserialize` helpers for
  results, answers, and questions, so a decision can be re-scored against stored answers without
  paying for inference again.
- `Models` resource (`IModelsResource`, `ModelsResult`, `ModelMetadata`) for listing the models
  available to the account.
- Configuration from `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL` (with `TYPESAFE_ENDPOINT` accepted as
  an alias), and `TYPESAFE_DEFAULT_MODEL`, with explicit options taking precedence and the SDK
  defaults (`https://api.typesafe.ai`, `jev-latest`, a 10 s per-attempt timeout) as the fallback.
- `TypeSafeAI.Sdk.DependencyInjection` with `AddTypeSafeClient` for `IServiceCollection`, built on
  `IHttpClientFactory`, binding the `TypeSafe` configuration section and redacting credential
  headers from the framework's own HTTP logging.
- Packaging and compatibility guarantees: `net8.0` and `net10.0` assets, trimming and
  ahead-of-time compilation analyzers enabled, a tracked public API surface, SourceLink, and
  symbols published as a `.snupkg` next to each `.nupkg`.
- Documentation: a quickstart, a guide to the question types and their documented limits, the
  confidence rules, composition patterns, forward compatibility, and retries and errors.

[Unreleased]: https://github.com/saibimajdi/typesafeai-dotnet-sdk/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/saibimajdi/typesafeai-dotnet-sdk/releases/tag/v0.1.0
