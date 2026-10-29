# Questions

A question is one discrete judgment about the state. The API models three kinds, and each maps to
one answer type in this SDK.

| Question | Answer | Wire `type` | Use it for |
| --- | --- | --- | --- |
| `NoulQuestion` | `NoulAnswer` | `noul` | A yes/no judgment where the probability itself is the signal. |
| `ChoiceQuestion` | `ChoiceAnswer` | `choice` | Choosing one of a known, unordered set of options. |
| `ScoreQuestion` | `ScoreAnswer` | `score` | A position on an ordered, described spectrum. |
| `RawQuestion` | `UnknownAnswer` | anything | A question kind, or a field, that this SDK version does not model. |

## Asking a good question

Ask for one snap judgment per question. Something a knowledgeable person could answer in a second
given the right context is a good question. "Analyse this ticket and decide what to do" is not a
question — it is a task, and the fix is to split it into several questions and combine the answers
in code. The [patterns](patterns.md) page is about exactly that.

The `id` is for your code. It is never sent to the model and is never part of inference, so write
the complete question in `instructions` even when the id looks self-explanatory. Ids are opaque:
any characters are allowed, they are never normalised or case-folded, and they round-trip verbatim.
Ids containing `.`, `::`, `@`, or `?` are fine, which is what makes an `area::question` naming
scheme practical.

`instructions` accepts any JSON shape — a string, an object, an array, or `null`. A string converts
implicitly, so the common case needs no ceremony:

```csharp
using System.Text.Json.Nodes;
using TypeSafeAI;

// A plain string.
var simple = new NoulQuestion("is_urgent", "Does this convey urgency?");

// Structured instructions, when the question benefits from explicit context.
var structured = new NoulQuestion(
    "is_urgent",
    new JsonObject
    {
        ["question"] = "Does this convey urgency?",
        ["definition"] = "Urgent means the sender needs a response before the end of the next business day.",
    });
```

## Noul: a yes/no probability

A noul asks a yes/no question and answers with the probability that the answer is yes, from `0` to
`1`. There is no confidence on a noul answer; the probability is the signal. Read it as: near `1`
is a strong yes, near `0` is a strong no, near `0.5` is genuinely uncertain. See
[answers-and-confidence.md](answers-and-confidence.md).

Use a noul when the probability itself is what you will act on — is this a bug report, is the
customer asking for a refund, does this document mention distributed systems. Several noul
probabilities can be averaged to gate an action on the mean of several independent conditions.

Do **not** use a noul to measure a position on a spectrum. A noul value of `0.5` means yes and no
are equally likely; it does not mean "medium", and averaging a noul with anything else does not
turn it into one. Use a `ScoreQuestion` with described levels.

`criteria` optionally pins down a subtle boundary between yes and no:

```csharp
using TypeSafeAI;

var question = new NoulQuestion(
    "is_bug_report",
    "Does this report a defect in the product?",
    new NoulCriteria(
        "The sender describes behaviour that differs from the documented or intended behaviour.",
        "The sender is asking how something works, requesting a feature, or reporting a problem with their own integration."));
```

`NoulCriteria` requires at least one of the two sides. Supplying neither throws `ArgumentException`
— omit the criteria entirely instead of supplying an empty one.

## Choice: one of a set

A choice selects one option from a set you define. It fits when the answer is one of a known set of
options with no order between them: routing a ticket to a department, classifying a document, naming
a detected programming language.

```csharp
using TypeSafeAI;

// Bare labels. Each label is sent to the model as-is and echoed back in the answer.
var byLabel = new ChoiceQuestion(
    "department",
    "Which team should handle this?",
    ["billing", "technical", "sales"]);

// Labels with a rubric description, which is sent to the model and improves the answer.
var byDescription = new ChoiceQuestion(
    "department",
    "Which team should handle this?",
    new Dictionary<string, string?>
    {
        ["billing"] = "Payment, invoice, refund, and subscription problems.",
        ["technical"] = "Product defects, outages, API errors, and integration problems.",
        ["sales"] = "Pricing, contracts, renewals, and new purchases.",
    });
```

Give the full list rather than a shortlist: labels and descriptions both go to the model, so a
richer list costs a few tokens each and buys a better answer. Add an `other` or
`none of the above` option whenever the list might not cover every input, so the model has
somewhere to put a value that fits none of the others. **The SDK never injects such an option for
you** — an injected option would make the answer set differ from the one you declared.

### Limits

| Limit | Value | Notes |
| --- | --- | --- |
| Maximum options | 255 | Hard cap. Exceeding it throws `ArgumentException` locally, before any network call. |
| Practical ceiling | ~240 | The API documentation notes a choice "works reliably up to roughly 240 options". Prefer a two-stage hierarchical classification above that. |
| Minimum options | 1 | An empty option list throws `ArgumentException`. |
| Duplicate labels | rejected | Labels are the keys of the criteria map and of the answer's probability map, so they must be unique. A duplicate throws `ArgumentException`. |

The order of the labels is preserved in `ChoiceQuestion.Labels` so that a serialized request is
deterministic and can be cached or hashed. The API itself treats the options as an unordered set.

Labels are matched and returned byte-for-byte. Do not normalise case or trim whitespace when
comparing a returned `Label` against your own list.

## Score: a position on a described spectrum

A score rates the state against an ordered rubric you define. It fits when the answer falls on a
spectrum and each point on the spectrum can be described: bug severity, customer frustration, skill
level. The order of the levels is their numbering, starting at zero.

```csharp
using TypeSafeAI;

var rubric = new ScoreQuestion(
    "frustration",
    "How frustrated is the customer?",
    ["Calm", "Frustrated", "Very angry"]);

// Structured levels work too, and are echoed back verbatim.
var structured = new ScoreQuestion(
    "severity",
    "How severe is this defect?",
    [
        new System.Text.Json.Nodes.JsonObject { ["label"] = "Cosmetic", ["impact"] = "No functional effect." },
        new System.Text.Json.Nodes.JsonObject { ["label"] = "Degraded", ["condition"] = "A workaround exists." },
        new System.Text.Json.Nodes.JsonObject { ["label"] = "Blocking", ["condition"] = "No workaround; the customer cannot proceed." },
    ]);
```

`ScoreAnswer.Score` is the probability-weighted position along the rubric, equal to the sum of each
level index multiplied by its probability. It can land between levels: a score of `2.02` on a
four-level rubric means the model put most of its probability mass on level 2. Treat it as a real
number, not an index, and compare it against thresholds rather than testing it for equality.

Use as many levels as can be described distinctly. Three is usually enough. Levels that overlap make
the answer hard to interpret, and the overlap shows up as low confidence and high
`ScoreAnswer.Variance`.

### Limits

| Limit | Value | Notes |
| --- | --- | --- |
| Minimum levels | 2 | Fewer throws `ArgumentException`. If the answer is a plain yes or no, use a `NoulQuestion`. |
| Maximum levels | 10 | More throws `ArgumentException`. A rubric of eleven levels is rejected by the API. |
| Level descriptions | any JSON shape | Each level is echoed back verbatim on the answer's `Legend`, so whatever shape you send is the shape you read. |

Both are exposed as constants on `TypeSafeDefaults` — `MinimumScoreLevels`, `MaximumScoreLevels`,
and `MaximumChoiceOptions` — so validation code does not have to hard-code them.

## Box: where something is in an image

> **Experimental.** TypeSafe labels images and Box questions as not yet production-ready. The request
> format, the answers, and the limits can change between releases. Box questions need Jev 1.14
> (`jev-1.14.0`, which `jev-latest` points to).

A `BoxQuestion` locates what its instructions describe in the request's image. The answer is the
four edges of a box, as fractions of the image's height and width, measured from the top and from
the left:

```csharp
using System.Text.Json.Nodes;
using TypeSafeAI;

var photo = Guid.NewGuid().ToString();
var findDog = new BoxQuestion("dog", "Locate the dog.");

var result = await client.SystemOneAsync(new SystemOneRequest
{
    State = new JsonObject { ["photo"] = photo },
    Assets = new Dictionary<string, ImageAsset>
    {
        [photo] = await ImageAsset.FromFileAsync("feeder.jpg"),
    },
    Questions = [findDog, new NoulQuestion("has_dog", "Is there a dog in `photo`?")],
});

BoxAnswer box = result.Get(findDog);
Console.WriteLine($"top={box.Top} left={box.Left} width={box.Width} height={box.Height}");
```

A box answer always reports a box, so pair it with a noul when the thing may be absent.
`BoxAnswer.Confidence` is `null` when the model provides none; `TryGetConfidence` then returns
`false` rather than inventing a value.

### Images

An image can go anywhere text can go: in the state, in a question's instructions, and in its
criteria. Each image is listed once in `SystemOneRequest.Assets`, keyed by a reference string.
Write the reference wherever the image belongs, as a whole string value or inside a longer string,
and the model receives the image in its place.

- Every asset must be referenced at least once, or the API returns `400`.
- Object keys are never replaced, so a choice label stays text; put the reference in its
  description instead.
- References match as plain text anywhere in the request, so use one that cannot occur by
  accident. A new `Guid` per image is the safest choice.
- Each use adds another copy of the image to what the model reads.

`ImageAsset.FromBytes` and `ImageAsset.FromFileAsync` detect PNG or JPEG from the file's
signature. `ImageAsset.FromBase64` takes an already encoded image and its `ImageFormat`. Those are
the only two formats the API accepts.

The SDK sends the required `TypeSafe-Danger-Zone-Images: true` header automatically on any request
that carries assets or a `BoxQuestion`. A value you set through `TypeSafeRequestOptions.Headers`
wins.

## Asking many questions at once

Every question in a request sees the same state, is evaluated independently, and answers under the
id you chose. One question's answer is never context for another, so questions can be added or
removed without changing the others' results.

That has a practical consequence: **send every question that shares a state in one request.** The
questions are evaluated in parallel, so adding one barely changes response time, and it costs only
the tokens for the extra question. Asking a question that may not be needed is close to free, which
is what makes speculative fan-out practical. See [patterns.md](patterns.md).

`QuestionSet` is a convenience for assembling questions dynamically. The client accepts any
`IEnumerable<Question>`, so a collection expression or an array works just as well:

```csharp
using TypeSafeAI;

var questions = new QuestionSet
{
    new NoulQuestion("is_urgent", "Does this convey urgency?"),
    new ChoiceQuestion("department", "Which team should handle this?", ["billing", "technical"]),
};

var other = new Question[]
{
    new NoulQuestion("is_churn_risk", "Does this suggest the customer may cancel?"),
};
```

`QuestionSet` rejects a duplicate id at the point it is added, which turns a mistake that would
otherwise produce a confusing answer into an immediate `ArgumentException`.

## Budget

Jev 1.13 and Jev 1.14 have two context limits. The state plus **all** questions must fit about
**64,000 tokens** (`TypeSafeDefaults.ApproximateRequestTokenBudget`), and the state plus **any one**
question must fit about **32,000 tokens** (`TypeSafeDefaults.ApproximateQuestionTokenBudget`). The
API publishes no token-counting endpoint, so the SDK cannot enforce either; they are documented so
you can size batches yourself.

When a single state is too large, set `TypeSafeRequestOptions.LongContext = true`. The SDK then
sends `TypeSafe-Danger-Zone-Long-Context: true`, which raises both limits to about 262,000 tokens.
It costs the same per token but is slower and less accurate, so turn it on only for the requests
that need it. If a batch is too large, split it by state rather than by question — one request
per batch of questions over the same state is dramatically cheaper and faster than one request per
question.

## When the SDK does not model what you need

If the API grows a question kind or a field this SDK version does not know about, `RawQuestion`
sends it verbatim and `additionalProperties` merges extra fields into any modelled question. Both
are covered in [forward-compatibility.md](forward-compatibility.md).
