# Patterns

Four shapes cover most real uses of System One. Each one is a way of arranging questions and
combining answers in your own code — the SDK deliberately does not choose for you, because the
decision procedure is the part that has to match your business.

## 1. Fan-out: ask everything, use what you need

Questions in one request are evaluated in parallel against the same state and cost only their own
tokens. So ask every question that might matter, and read only the answers the current path needs.

```csharp
using TypeSafeAI;

var questions = new QuestionSet
{
    new NoulQuestion("is_urgent", "Does this convey urgency?"),
    new NoulQuestion("is_bug", "Does this report a defect in the product?"),
    new ChoiceQuestion("department", "Which team should handle this?", ["billing", "technical", "sales"]),
    new ScoreQuestion("frustration", "How frustrated is the customer?", ["Calm", "Frustrated", "Very angry"]),
};

var result = await client.SystemOneAsync(ticketText, questions);

// The urgent path reads one answer and ignores the rest.
if (result.TryGet("is_urgent", out var answer) && answer is NoulAnswer urgency && urgency.Probability >= 0.8)
{
    Console.WriteLine("Page the on-call engineer.");
}
else if (result.Contains("department"))
{
    Console.WriteLine($"Route to {result.Choice("department").Label}.");
}
```

The alternative — one request per question — is dramatically more expensive and slower for the same
answers. The only reason to split is the token budget: state and questions share roughly 32,000
tokens per request, so split by state, not by question. Tokens are counted by the server, but the
request's size on the wire is a usable proxy: `TypeSafeJson.Serialize(request)` returns the body
the client would send (with `TypeSafeDefaults.DefaultModel` when the request names no model, since
there is no client to supply one), and `Encoding.UTF8.GetByteCount` of it is the size in bytes. Measuring
the state text itself undercounts, because the SDK escapes each non-ASCII character to a
six-character sequence.

Because the API does not promise an answer for every id, use `TryGet` for anything speculative.
`Ids` tells you which questions were answered.

## 2. Confidence-gated routing

Act when the answer is clear, and escalate when it is not. The gate lives in your code, because only
you can price the cost of being wrong.

```csharp
using TypeSafeAI;

internal static class Routing
{
    private const double ActThreshold = 0.85;
    private const double TieMargin = 0.15;

    public static string Decide(ChoiceAnswer answer)
    {
        if (answer.Confidence >= ActThreshold)
        {
            return answer.Label;
        }

        // A low confidence often just means two options are close. Say so explicitly rather than
        // letting the winner look decisive.
        var top = answer.Top(2).ToList();
        if (top.Count == 2 && top[0].Value - top[1].Value < TieMargin)
        {
            return "human-review";
        }

        // Not a tie, just soft: keep the answer, but route it somewhere it will be checked.
        return $"{answer.Label} (review)";
    }
}

internal static class Example
{
    public static string Route(SystemOneResult result) => Routing.Decide(result.Choice("department"));
}
```

The same gate works for a score, where `Variance` is often the more informative warning:

```csharp
using TypeSafeAI;

var severity = result.Score("severity");

if (severity.Confidence >= 0.85 && severity.Variance < 0.5)
{
    Console.WriteLine($"Severity {severity.Score:F2} of {severity.MaxLevel}.");
}
else
{
    Console.WriteLine("Severity is unclear; ask a human.");
}
```

For a noul there is no confidence to gate on, so gate on the probability itself and on distance from
the middle:

```csharp
using TypeSafeAI;

var noul = result.Noul("is_bug");

// 2 * |p - 0.5| is 1 at the extremes and 0 when the two outcomes are equally likely.
var decisiveness = 2 * Math.Abs(noul.Probability - 0.5);

if (decisiveness >= 0.7)
{
    Console.WriteLine(noul.Probability >= 0.5 ? "Treat as a bug report." : "Treat as not a bug report.");
}
else
{
    Console.WriteLine("Too close to call; send it to triage.");
}
```

See [answers-and-confidence.md](answers-and-confidence.md) for why a noul has no confidence and why
thresholds should not be averaged across different quantities.

## 3. Composite scoring

One judgment that depends on several things is better expressed as one Score per thing, combined in
code with weights. When the result does not match what your team would decide, change the weights
and recompute — no prompt changes, no new inference, and no re-evaluation of questions whose answers
you already have.

`CompositeScore.Weighted` normalises each score by its own rubric length first, so rubrics of
different lengths combine meaningfully.

```csharp
using TypeSafeAI;

var result = await client.SystemOneAsync(
    ticket,
    [
        new ScoreQuestion("severity", "How severe is this defect?", ["Cosmetic", "Degraded", "Blocking"]),
        new ScoreQuestion("frustration", "How frustrated is the customer?", ["Calm", "Frustrated", "Very angry"]),
        new ScoreQuestion("revenue_impact", "How much revenue is at risk?", ["None", "One account", "Many accounts"]),
    ]);

var priority = CompositeScore.Weighted(
    new WeightedScore(result.Score("severity"), 3.0),
    new WeightedScore(result.Score("frustration"), 2.0),
    new WeightedScore(result.Score("revenue_impact"), 1.0));

Console.WriteLine($"priority {priority:P0}");
```

Weights are the second argument and are relative, so only their ratios matter: `3, 2, 1` means the
same thing as `0.5, 0.33, 0.17`. Negative weights are rejected with `ArgumentException` — a factor
that should reduce the result is better expressed by reversing its rubric than by a negative weight
— as is a set of parts whose weights are all zero.

Because answers are immutable and round-trip through JSON, re-tuning weights against a stored result
costs nothing:

```csharp
using TypeSafeAI;
using TypeSafeAI.Serialization;

var cached = TypeSafeJson.DeserializeResult(storedJson);
var parts = new WeightedScore[]
{
    new(cached.Score("severity"), 3.0),
    new(cached.Score("frustration"), 2.0),
    new(cached.Score("revenue_impact"), 1.0),
};

double Original() => CompositeScore.Weighted(parts);

double CheaperToBeWrong() => CompositeScore.Weighted(
    new WeightedScore(cached.Score("severity"), 1.0),
    new WeightedScore(cached.Score("frustration"), 1.0),
    new WeightedScore(cached.Score("revenue_impact"), 5.0));

Console.WriteLine($"{Original():P0} vs {CheaperToBeWrong():P0}");
```

### Several profiles over the same answers

Different roles judge the same factors differently. `CompositeScore.Profiles` computes several
named weightings from one set of answers, matching factors to score answers by the answer's `Id`:

```csharp
using TypeSafeAI;

var profiles = new Dictionary<string, IReadOnlyDictionary<string, double>>
{
    ["support-lead"] = new Dictionary<string, double>
    {
        ["severity"] = 3.0,
        ["frustration"] = 2.0,
        ["revenue_impact"] = 1.0,
    },
    ["account-manager"] = new Dictionary<string, double>
    {
        ["severity"] = 2.0,
        ["frustration"] = 1.0,
        ["revenue_impact"] = 3.0,
    },
};

var parts = new WeightedScore[]
{
    new(result.Score("severity"), 1.0),
    new(result.Score("frustration"), 1.0),
    new(result.Score("revenue_impact"), 1.0),
};

IReadOnlyDictionary<string, double> byProfile = CompositeScore.Profiles(parts, profiles);

foreach (var (profile, score) in byProfile)
{
    Console.WriteLine($"{profile}: {score:P0}");
}
```

Two behaviours to know: the weights carried by `parts` are ignored — a profile names its own factors
and weights — and a factor a profile names but the answers do not contain is skipped. If a profile
matches nothing, `CompositeScore.Weighted` throws `ArgumentException`, so make sure every profile
names at least one factor that exists.

## 4. Intent routing

Route on one answer, then branch. Ask the follow-ups in the same request as the intent question:
they are evaluated in parallel and cost only their tokens, so the branch you do not take costs
almost nothing.

```csharp
using TypeSafeAI;

var intent = new ChoiceQuestion(
    "intent",
    "What does the customer want?",
    ["cancel", "refund", "how-to", "bug", "other"]);

var result = await client.SystemOneAsync(
    ticket,
    [
        intent,
        new ScoreQuestion("churn_risk", "How likely is this customer to cancel?", ["No signal", "Some signal", "Explicit threat"]),
        new NoulQuestion("wants_human", "Does the customer ask to speak to a person?"),
        new ScoreQuestion("urgency", "How urgent is this?", ["Whenever", "This week", "Today"]),
    ]);

if (!result.TryGet(intent, out var answer))
{
    Console.WriteLine("No intent answer came back; route to triage.");
    return;
}

switch (answer.Label)
{
    case "cancel":
        Console.WriteLine($"Churn risk {result.Score("churn_risk").NormalizedScore:P0}.");
        break;

    case "refund":
    case "bug":
        Console.WriteLine($"Wants a human: {result.Noul("wants_human").Probability:P0}.");
        Console.WriteLine($"Urgency: {result.Score("urgency").NormalizedScore:P0}.");
        break;

    default:
        Console.WriteLine($"Intent {answer.Label} at confidence {answer.Confidence:P0}.");
        break;
}
```

Use a second round only when the follow-up needs something you could not send in the first request —
a record you have to fetch, or an answer that determines which data is relevant at all:

```csharp
using System.Text.Json.Nodes;
using TypeSafeAI;

var first = await client.SystemOneAsync(ticket, [new ChoiceQuestion("intent", "What does the customer want?", ["refund", "other"])]);

if (first.TryGet("intent", out var intentAnswer) && intentAnswer is ChoiceAnswer { Label: "refund", Confidence: >= 0.7 })
{
    var order = await LoadOrderAsync();
    var second = await client.SystemOneAsync(
        order,
        [new ChoiceQuestion("reason", "Why is the customer asking for a refund?", ["duplicate-charge", "never-arrived", "changed-mind"])]);

    Console.WriteLine(second.Choice("reason").Label);
}

static Task<JsonNode?> LoadOrderAsync() => Task.FromResult<JsonNode?>(null);
```

Two rounds mean two billing events and two latencies. Prefer one round with speculative questions,
and reserve the second round for the case where the questions themselves depend on data you had to
fetch.

## Choosing between them

| Situation | Pattern |
| --- | --- |
| Several independent judgments about the same text | Fan-out — one request, read what you need. |
| The answer drives an action, and being wrong is expensive | Confidence gating — act high, escalate low, and review the middle. |
| Many factors contribute to one number | Composite scoring — one Score per factor, weights in code. |
| The state means different things depending on what it is | Intent routing — one choice, then speculative follow-ups in the same request. |

These compose. A real triage pipeline usually fans out, routes on intent, gates each branch on
confidence, and ranks within a branch with a composite score.
