# Answers and confidence

## The answer types

| Type | Carries | Key members |
| --- | --- | --- |
| `NoulAnswer` | The probability that the answer is yes. | `Probability` |
| `ChoiceAnswer` | The selected label, the full distribution, and the API's confidence. | `Label`, `Confidence`, `Probabilities`, `TopLabel`, `TopProbability`, `ProbabilityOf`, `ProbabilityOrDefault`, `TryGetProbability`, `Ranked()`, `Top(n)` |
| `ScoreAnswer` | The weighted position, the rubric, the distribution, and the API's confidence. | `Score`, `Confidence`, `Legend`, `Probabilities`, `LevelCount`, `MaxLevel`, `NormalizedScore`, `ExpectedLevel`, `Variance`, `ProbabilityAtLevel`, `LegendTextAtLevel` |
| `UnknownAnswer` | An answer kind this SDK version does not model. | `Type`, `Raw` |

All of them derive from `Answer`, which supplies `Id`, `Type`, `AdditionalProperties`, and
`TryGetConfidence(out double)`.

## A noul has no confidence, and that is deliberate

`NoulAnswer` has exactly one numeric member: `Probability`, the probability that the answer is yes.
There is no confidence member and `TryGetConfidence` always returns `false`.

That mirrors the API: noul answers do not carry a confidence field. The SDK does not invent one,
does not derive one from the probability, and does not expose a nullable property that would let a
missing value be read as a real `0`. The probability is the signal — near `1` is a strong yes, near
`0` is a strong no, and near `0.5` is genuinely uncertain.

If you need a single number that behaves like a confidence for a noul, derive it in your own code
from the probability, where its meaning is visible at the call site. `2 * |p - 0.5|` is the usual
choice: it is `1` when the probability is `0` or `1`, and `0` when the two outcomes are equally
likely. Write it where you use it, not in a helper that hides the definition.

## Confidence is read from the wire, never recomputed

`ChoiceAnswer.Confidence` and `ScoreAnswer.Confidence` are values the API reported. The SDK reads
them from the response and surfaces them verbatim. It never recomputes them, never adjusts them for
distribution shape, and never substitutes a value of its own.

That matters because the current confidence formula is not published, and the values it produces do
not match the entropy formula that appeared in older preview documentation. If the SDK quietly
substituted its own statistic, two SDK versions could disagree about the same cached answer, and a
threshold tuned against one would silently mean something different in the other.

`NormalizedEntropy()`, `ProbabilityMath.NormalizedEntropy`, `ScoreAnswer.Variance`, and
`ScoreAnswer.ExpectedLevel` exist because the documentation explicitly invites callers to compute
their own statistic from the full distribution. They are **not** the API's confidence, and the SDK
never uses them to populate it. If you use them, say so in your own code, and do not mix them with
`Confidence` in the same comparison.

## Thresholds belong in caller code

There is no SDK-level "is this answer good enough" flag, and there is no configuration option that
makes one answer trustworthy. Whether a confidence is high enough depends on what the decision
costs: routing a ticket to the wrong queue is cheap, closing a fraud case on the wrong answer is
not. Only you can price that.

Start with three bands, tune them against your own data, and keep them in one place that a reader
of your code can find:

| Band | A reasonable starting range | What to do with it |
| --- | --- | --- |
| High | confidence ≥ `0.85` | Act on the answer. Log the confidence and the model so a later regression is traceable. |
| Medium | `0.60` ≤ confidence < `0.85` | Act, but keep the fallback path alive: flag it for review, or ask a second, narrower question. |
| Low | confidence < `0.60` | Do not act. Escalate to a human, or improve the question: overlapping rubric levels and vague instructions are the usual causes. |

```csharp
using TypeSafeAI;

internal enum ConfidenceBand
{
    Low,
    Medium,
    High,
}

internal static class Confidence
{
    // Starting points, not SDK semantics. Replace them with values you measured.
    private const double HighThreshold = 0.85;
    private const double MediumThreshold = 0.60;

    public static ConfidenceBand Of(double confidence) => confidence switch
    {
        >= HighThreshold => ConfidenceBand.High,
        >= MediumThreshold => ConfidenceBand.Medium,
        _ => ConfidenceBand.Low,
    };

    // Answers that carry no confidence at all — nouls — are not silently treated as low.
    public static bool TryBand(Answer answer, out ConfidenceBand band)
    {
        if (answer.TryGetConfidence(out var confidence))
        {
            band = Of(confidence);
            return true;
        }

        band = ConfidenceBand.Low;
        return false;
    }
}
```

Three practical rules that matter more than the exact numbers:

- **A low confidence is information, not noise.** It usually means the rubric levels overlap, the
  judgment is multi-dimensional, or the state does not contain enough to decide. Splitting the
  question is often a better response than lowering the threshold.
- **Never average a confidence with a probability.** They are different quantities on the same
  `0`–`1` range. Combine them only through an explicit decision you can explain.
- **Do not tune thresholds against cached answers and then forget it.** `TypeSafeJson` makes it
  cheap to replay a stored result through new thresholds, which is the intended workflow. Do that
  in a test or a notebook, and commit the thresholds you chose.

## The distribution is a richer signal than the winner

Two choice answers can share a winner and mean very different things:

| Distribution | `Label` | `Confidence` | What it suggests |
| --- | --- | --- | --- |
| `billing` 0.95, `technical` 0.03, `sales` 0.02 | `billing` | high | A clear answer. |
| `billing` 0.50, `technical` 0.45, `sales` 0.05 | `billing` | low | A genuine coin toss between two options. Treat the winner as a coin toss. |

```csharp
using TypeSafeAI;

var department = result.Choice("department");

// Read the whole distribution when the runner-up matters.
foreach (var (label, probability) in department.Ranked())
{
    Console.WriteLine($"{label}: {probability:P1}");
}

// Or just the top few.
foreach (var (label, probability) in department.Top(2))
{
    Console.WriteLine($"{label}: {probability:P1}");
}
```

`ProbabilityOf` throws when a label is absent, `TryGetProbability` and `ProbabilityOrDefault` do
not. The distinction matters: the API does not guarantee that every requested label appears in the
distribution, and "not reported" is not the same as "ruled out". Use the non-throwing forms for any
label you did not just observe in `Probabilities`.

For a score, the same idea applies along the rubric. `Variance` is often the more useful warning
than `Confidence` on its own: a large variance means the probability mass is split across distant
levels, which is a stronger sign that the rubric does not fit the state than a low confidence by
itself.

```csharp
using TypeSafeAI;

var severity = result.Score("severity");

Console.WriteLine($"position {severity.Score:F2} of {severity.MaxLevel} (normalised {severity.NormalizedScore:F2})");
Console.WriteLine($"confidence {severity.Confidence:F2}, variance {severity.Variance:F2}");

for (var level = 0; level <= severity.MaxLevel; level++)
{
    var text = severity.LegendTextAtLevel(level) ?? "(undescribed)";
    Console.WriteLine($"  {level} {text}: {severity.ProbabilityAtLevel(level):P1}");
}
```

`Legend` values are echoed verbatim from the question, so a level described with an object comes
back as an object. `LegendTextAtLevel` is the convenience for the common case where the levels are
plain strings, and it returns `null` for anything else.

## Answers that are not there

The API does not guarantee an answer for every question that was asked. A missing id is normal for a
speculative question whose answer turned out not to be needed.

```csharp
using TypeSafeAI;

var isUrgentQuestion = new NoulQuestion("is_urgent", "Does this convey urgency?");

// By id.
if (result.TryGet("is_urgent", out var answer) && answer is NoulAnswer urgency)
{
    Console.WriteLine(urgency.Probability);
}

// Bound to the question, so a renamed question is a compile error rather than a runtime miss.
if (result.TryGet(isUrgentQuestion, out var typed))
{
    Console.WriteLine(typed.Probability);
}

// Which ids did come back?
foreach (var id in result.Ids)
{
    Console.WriteLine(id);
}
```

`Get`, `Noul`, `Choice`, and `Score` throw `KeyNotFoundException` when there is no answer for the id
— and, for the typed accessors, also when the answer exists but is a different kind. Use them when a
missing answer is a bug; use `TryGet` when it is expected.

## Reproducibility

An answer is only reproducible if you record what produced it.

```csharp
using TypeSafeAI;
using TypeSafeAI.Serialization;

var result = await client.SystemOneAsync(state, questions);

// The resolved model, not the alias you asked for: "jev-latest" may come back as "jev-1.14.0",
// and the alias may resolve differently later.
Console.WriteLine($"model {result.Model}, request {result.RequestId}");
Console.WriteLine($"tokens in {result.Usage?.InputTokens}, out {result.Usage?.OutputTokens}");

// Round-trips, so thresholds and weights can be re-tuned against stored answers without paying
// for inference again. RequestId is not part of the body and is not included.
var json = TypeSafeJson.Serialize(result);
var replayed = TypeSafeJson.DeserializeResult(json);
```

Record `Model` alongside any decision that has to stay reproducible, and re-run your thresholds
against the cached result rather than calling the API again. `RequestId` is the handle TypeSafe
support asks for when investigating a specific request; it is safe to log and safe to paste into an
issue. See [SECURITY.md](https://github.com/saibimajdi/typesafeai-dotnet-sdk/blob/main/SECURITY.md) for what is and is not safe to share.
