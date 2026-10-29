# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Everything listed here is the first planned release, [0.1.0]. The SDK is pre-1.0: while the
version is `0.x`, a breaking change increments the minor version, and the public API surface is
tracked per project in `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` so that no change to
it can happen by accident.

### Changed

- `TypeSafeDefaults.ApproximateRequestTokenBudget` is now `64_000`, matching the documented
  per-request context of Jev 1.13 and 1.14; the per-question limit of `32_000` moved to the new
  `ApproximateQuestionTokenBudget`.
- The NuGet package IDs are now `TypeSafeAI.Sdk` and
  `TypeSafeAI.Sdk.DependencyInjection`. The public C# namespaces are now `TypeSafeAI`,
  `TypeSafeAI.Serialization`, and `TypeSafeAI.DependencyInjection`; assembly names remain
  unchanged. This is a source-breaking migration; see
  [the package migration guide](docs/package-migration.md).
- Repository metadata and documentation now use the canonical
  `saibimajdi/typesafeai-dotnet-sdk` GitHub repository.

### Added

- Support for the Jev 1.14 release (`jev-1.14.0`, which `jev-latest` now resolves to). The default
  model is still the `jev-latest` alias, so no code change is needed to use it.
- `BoxQuestion` and `BoxAnswer` (experimental): locate what the instructions describe in an image.
  The answer carries `Top`, `Bottom`, `Left`, and `Right` as fractions of the image, plus `Width`,
  `Height`, and a nullable `Confidence`. `SystemOneResult` gains `Boxes` and `Box(id)`.
- `ImageAsset`, `ImageFormat`, and `SystemOneRequest.Assets` (experimental): send PNG or JPEG
  images base64-encoded in the request, referenced by placeholder from the state, instructions, or
  criteria. `ImageAsset.FromBytes` and `ImageAsset.FromFileAsync` detect the format from the file
  signature.
- The `TypeSafe-Danger-Zone-Images: true` header is sent automatically on any request that carries
  assets or a `BoxQuestion`; a caller-supplied value wins.
- `TypeSafeRequestOptions.LongContext`, which sends `TypeSafe-Danger-Zone-Long-Context: true` to
  raise both context limits to about 262k tokens.
- `TypeSafeDefaults.ImagesHeader`, `LongContextHeader`, `ApproximateQuestionTokenBudget`, and
  `ApproximateLongContextTokenBudget`.

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
