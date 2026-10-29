# TypeSafeAI .NET SDK documentation

Ask typed questions about text or JSON state. Get probabilities, labels, and scores you can
use directly in C# — with batching, retries, dependency injection, and forward compatibility.

This community SDK targets **.NET 8 and .NET 10** and supports trimming and Native AOT.
It is independent of, and not affiliated with or endorsed by, TypeSafe AI.

## Install

```bash
dotnet add package TypeSafeAI.Sdk --prerelease
```

For `IHttpClientFactory` and `IServiceCollection` integration, also install
`TypeSafeAI.Sdk.DependencyInjection`:

```bash
dotnet add package TypeSafeAI.Sdk.DependencyInjection --prerelease
```

Only prerelease versions are currently published, so `--prerelease` is required. Once a stable
version is available, omit the flag to install the latest stable release.

**[Start with the quickstart →](quickstart.md)** — create your first request, configure the
client, and work with typed answers.

## Start here

| Document | Read it when |
| --- | --- |
| [Quickstart](quickstart.md) | You want a working request in five minutes, then configuration, dependency injection, structured state, and error handling. |
| [API reference](api-reference.md) | You need the authoritative public signatures, option precedence, result members, retry defaults, or HTTP wire shape. |
| [Questions](questions.md) | You are choosing between a noul, a choice, and a score, or you have hit one of the documented limits. |
| [Answers and confidence](answers-and-confidence.md) | You are about to put a threshold on an answer, or you are wondering why a noul has no confidence. |
| [Composition patterns](patterns.md) | You want to batch questions, gate an action on confidence, combine several scores, or route by intent. |
| [Forward compatibility](forward-compatibility.md) | The API has a field, a question kind, or an answer kind the SDK does not model. |
| [Retries and errors](retries-and-errors.md) | A call failed, or you need to reason about timeouts, retries, and budgets before one does. |
| [Package migration](package-migration.md) | You are moving from the former NuGet package IDs, namespaces, or repository URL. |

## The three ideas worth internalising

**One request, many questions.** Every question in a request sees the same state and is evaluated
independently and in parallel. Adding a question barely changes latency and costs only its tokens.
This is what makes the fan-out pattern in [Composition patterns](patterns.md) cheaper than the obvious
alternative of one request per question.

**Answers are typed, and nothing is lost.** Every answer is constrained to the options the question
supplied, so nothing has to be recovered from prose. Anything the SDK does not model is preserved —
`AdditionalProperties` on a question or answer, `RawJson` on a result, `UnknownAnswer` for an answer
kind newer than the SDK, `RawQuestion` for a question kind newer than the SDK. The SDK is never the
reason a new API feature cannot be used.

**Confidence is reported, not invented.** Where the API reports a confidence, the SDK surfaces it
verbatim. Where it does not — a noul — there is no confidence member at all, deliberately, rather
than a value of zero that could be mistaken for a real one. Composing on top, including where to put
a threshold, is caller code. See [Answers and confidence](answers-and-confidence.md).

## Other places to look

- [Contributing guide](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/CONTRIBUTING.md) — build, test, coding standards, and how to add a public
  API.
- [Changelog](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/CHANGELOG.md) — what shipped when.
- [Releasing](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/RELEASING.md) — how a version reaches nuget.org, and the one-time trusted
  publishing setup behind it.
- [Support](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/SUPPORT.md) — where to ask what, and the boundary between this SDK and the TypeSafe
  service.
- [TypeSafe's own documentation](https://docs.typesafe.ai/) — the authority on the API itself:
  states, primitives, confidence, and the HTTP contract this SDK implements.
