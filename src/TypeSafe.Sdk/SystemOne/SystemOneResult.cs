using System.Text.Json;
using System.Text.Json.Serialization;
using TypeSafeAI.Serialization;

namespace TypeSafeAI;

/// <summary>
/// The answers to a System One request, keyed by the question ids that were supplied.
/// </summary>
/// <remarks>
/// <para>
/// The API does not guarantee that every requested id appears in the response, so all lookups have
/// a <c>Try</c> form. A missing id is normal for a speculative question whose answer turned out not
/// to be needed.
/// </para>
/// <para>
/// The result is immutable and safe to cache. It round-trips through
/// <see cref="JsonSerializer"/>, so thresholds and weights can be re-tuned against stored answers
/// without paying for inference again.
/// </para>
/// </remarks>
[JsonConverter(typeof(SystemOneResultJsonConverter))]
public sealed class SystemOneResult
{
    private readonly Dictionary<string, Answer> _answers;
    private readonly Dictionary<string, NoulAnswer> _nouls;
    private readonly Dictionary<string, ChoiceAnswer> _choices;
    private readonly Dictionary<string, ScoreAnswer> _scores;
    private readonly Dictionary<string, BoxAnswer> _boxes;
    private readonly Dictionary<string, UnknownAnswer> _unknown;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemOneResult"/> class.
    /// </summary>
    internal SystemOneResult(
        string model,
        Usage? usage,
        IReadOnlyDictionary<string, Answer> answers,
        string? requestId,
        JsonElement rawJson)
    {
        Model = model;
        Usage = usage;
        _answers = new Dictionary<string, Answer>(answers, StringComparer.Ordinal);

        _nouls = new Dictionary<string, NoulAnswer>(StringComparer.Ordinal);
        _choices = new Dictionary<string, ChoiceAnswer>(StringComparer.Ordinal);
        _scores = new Dictionary<string, ScoreAnswer>(StringComparer.Ordinal);
        _boxes = new Dictionary<string, BoxAnswer>(StringComparer.Ordinal);
        _unknown = new Dictionary<string, UnknownAnswer>(StringComparer.Ordinal);

        foreach (var (id, answer) in _answers)
        {
            switch (answer)
            {
                case NoulAnswer noul:
                    _nouls[id] = noul;
                    break;
                case ChoiceAnswer choice:
                    _choices[id] = choice;
                    break;
                case ScoreAnswer score:
                    _scores[id] = score;
                    break;
                case BoxAnswer box:
                    _boxes[id] = box;
                    break;
                case UnknownAnswer unknown:
                    _unknown[id] = unknown;
                    break;
            }
        }

        RequestId = requestId;
        RawJson = rawJson;
    }

    /// <summary>
    /// Gets the model that performed the evaluation.
    /// </summary>
    /// <remarks>
    /// This is the <em>resolved</em> model, not the alias that was requested: sending
    /// <c>jev-latest</c> and reading <c>jev-1.14.0</c> here is expected, and the alias may resolve
    /// differently later. Record this value alongside any decision that must stay reproducible.
    /// </remarks>
    public string Model { get; }

    /// <summary>
    /// Gets the token usage for the request, or <see langword="null"/> when the API reported none.
    /// </summary>
    public Usage? Usage { get; }

    /// <summary>
    /// Gets every answer, keyed by question id.
    /// </summary>
    /// <remarks>
    /// Question ids are opaque caller-chosen strings. They are never normalised, so ids
    /// containing <c>.</c>, <c>::</c>, <c>@</c>, or <c>?</c> are returned exactly as supplied, and
    /// callers that group questions by prefix can scan these keys directly.
    /// </remarks>
    public IReadOnlyDictionary<string, Answer> Answers => _answers;

    /// <summary>
    /// Gets the yes/no answers, keyed by question id.
    /// </summary>
    public IReadOnlyDictionary<string, NoulAnswer> Nouls => _nouls;

    /// <summary>
    /// Gets the choice answers, keyed by question id.
    /// </summary>
    public IReadOnlyDictionary<string, ChoiceAnswer> Choices => _choices;

    /// <summary>
    /// Gets the score answers, keyed by question id.
    /// </summary>
    public IReadOnlyDictionary<string, ScoreAnswer> Scores => _scores;

    /// <summary>
    /// Gets the box answers, keyed by question id.
    /// </summary>
    public IReadOnlyDictionary<string, BoxAnswer> Boxes => _boxes;

    /// <summary>
    /// Gets the answers whose kind this SDK version does not model, keyed by question id.
    /// </summary>
    /// <remarks>
    /// Usually empty. A non-empty result means the API introduced an answer kind after this SDK
    /// release; the raw body is available on each <see cref="UnknownAnswer"/>, and the entire
    /// response is in <see cref="RawJson"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, UnknownAnswer> UnknownAnswers => _unknown;

    /// <summary>
    /// Gets the value of the <c>x-typesafe-request-id</c> response header, or <see langword="null"/>
    /// when it was absent.
    /// </summary>
    /// <remarks>
    /// TypeSafe support asks for this value when investigating a specific request. It is populated
    /// on failures as well, through <see cref="TypeSafeApiException.RequestId"/>.
    /// </remarks>
    public string? RequestId { get; }

    /// <summary>
    /// Gets the complete response body exactly as it arrived.
    /// </summary>
    /// <remarks>
    /// The escape hatch for fields this SDK version does not model, including answers of an
    /// unrecognised kind.
    /// </remarks>
    public JsonElement RawJson { get; }

    /// <summary>
    /// Gets the answer to a question, bound to the answer type at compile time.
    /// </summary>
    /// <typeparam name="TAnswer">The answer type the question produces.</typeparam>
    /// <param name="question">The question that was asked.</param>
    /// <returns>The typed answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="question"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The response contains no answer with that id.</exception>
    /// <exception cref="InvalidOperationException">The answer exists but is a different kind.</exception>
    /// <remarks>
    /// This is the strongly typed path: it needs neither a string key nor a cast, so a renamed
    /// question becomes a compile error rather than a runtime miss.
    /// </remarks>
    public TAnswer Get<TAnswer>(IQuestion<TAnswer> question)
        where TAnswer : Answer
    {
        ArgumentNullException.ThrowIfNull(question);

        if (!_answers.TryGetValue(question.Id, out var answer))
        {
            throw new KeyNotFoundException(
                $"The response contains no answer for question id '{question.Id}'. The API does not " +
                "guarantee an answer for every question that was asked; use TryGet when the answer " +
                "may be absent.");
        }

        if (answer is TAnswer typed)
        {
            return typed;
        }

        throw new InvalidOperationException(
            $"Question '{question.Id}' was answered with a '{answer.Type}' answer, but a " +
            $"'{typeof(TAnswer).Name}' was expected. This means the response does not match the " +
            "question that was asked.");
    }

    /// <summary>
    /// Gets the answer to a question, without throwing when it is absent.
    /// </summary>
    /// <typeparam name="TAnswer">The answer type the question produces.</typeparam>
    /// <param name="question">The question that was asked.</param>
    /// <param name="answer">The typed answer, when present and of the expected kind.</param>
    /// <returns><see langword="true"/> when a matching answer was found.</returns>
    /// <remarks>
    /// Use this for speculative questions, whose answers only matter for some inputs.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="question"/> is <see langword="null"/>.</exception>
    public bool TryGet<TAnswer>(IQuestion<TAnswer> question, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TAnswer? answer)
        where TAnswer : Answer
    {
        ArgumentNullException.ThrowIfNull(question);

        if (_answers.TryGetValue(question.Id, out var found) && found is TAnswer typed)
        {
            answer = typed;
            return true;
        }

        answer = null;
        return false;
    }

    /// <summary>
    /// Gets an answer by question id.
    /// </summary>
    /// <param name="id">The question id.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="KeyNotFoundException">The response contains no answer with that id.</exception>
    public Answer Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (_answers.TryGetValue(id, out var answer))
        {
            return answer;
        }

        throw new KeyNotFoundException(
            $"The response contains no answer for question id '{id}'. Known ids: " +
            $"{string.Join(", ", _answers.Keys)}.");
    }

    /// <summary>
    /// Gets an answer by question id, without throwing when it is absent.
    /// </summary>
    /// <param name="id">The question id.</param>
    /// <param name="answer">The answer, when present.</param>
    /// <returns><see langword="true"/> when an answer with that id was found.</returns>
    public bool TryGet(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Answer? answer)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _answers.TryGetValue(id, out answer);
    }

    /// <summary>
    /// Gets a yes/no answer by question id.
    /// </summary>
    /// <param name="id">The question id.</param>
    /// <returns>The noul answer.</returns>
    /// <exception cref="KeyNotFoundException">
    /// The response contains no answer with that id, or the answer is not a noul answer.
    /// </exception>
    public NoulAnswer Noul(string id) => Require<NoulAnswer>(id, "noul");

    /// <summary>
    /// Gets a choice answer by question id.
    /// </summary>
    /// <param name="id">The question id.</param>
    /// <returns>The choice answer.</returns>
    /// <exception cref="KeyNotFoundException">
    /// The response contains no answer with that id, or the answer is not a choice answer.
    /// </exception>
    public ChoiceAnswer Choice(string id) => Require<ChoiceAnswer>(id, "choice");

    /// <summary>
    /// Gets a score answer by question id.
    /// </summary>
    /// <param name="id">The question id.</param>
    /// <returns>The score answer.</returns>
    /// <exception cref="KeyNotFoundException">
    /// The response contains no answer with that id, or the answer is not a score answer.
    /// </exception>
    public ScoreAnswer Score(string id) => Require<ScoreAnswer>(id, "score");

    /// <summary>
    /// Gets a box answer by question id.
    /// </summary>
    /// <param name="id">The question id.</param>
    /// <returns>The box answer.</returns>
    /// <exception cref="KeyNotFoundException">
    /// The response contains no answer with that id, or the answer is not a box answer.
    /// </exception>
    public BoxAnswer Box(string id) => Require<BoxAnswer>(id, "box");

    /// <summary>
    /// Determines whether the response contains an answer with the given id.
    /// </summary>
    /// <param name="id">The question id.</param>
    /// <returns><see langword="true"/> when an answer with that id is present.</returns>
    public bool Contains(string id) => id is not null && _answers.ContainsKey(id);

    /// <summary>
    /// Gets the ids present in the response, in the order the API returned them.
    /// </summary>
    /// <remarks>
    /// Useful for grouping answers by an id convention, such as a <c>prefix::name</c> scheme, and
    /// for detecting the speculative questions that were not answered.
    /// </remarks>
    public IEnumerable<string> Ids => _answers.Keys;

    /// <summary>
    /// Writes this result in the wire format.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <remarks>
    /// Kept internal so serialization goes through the SDK's own converter, which is annotation-free
    /// under trimming and ahead-of-time compilation.
    /// </remarks>
    internal void WriteTo(System.Text.Json.Utf8JsonWriter writer) =>
        new Serialization.SystemOneResultJsonConverter().Write(writer, this, Serialization.TypeSafeJson.Options);

    /// <summary>
    /// Returns a copy of this result carrying a request id.
    /// </summary>
    /// <param name="requestId">The value of the <c>x-typesafe-request-id</c> response header.</param>
    /// <returns>A result identical to this one, but with the request id attached.</returns>
    /// <remarks>
    /// The request id lives in a response header rather than the body, so it is attached after the
    /// body has been deserialized.
    /// </remarks>
    internal SystemOneResult WithRequestId(string? requestId) =>
        new(Model, Usage, _answers, requestId, RawJson);

    private TAnswer Require<TAnswer>(string id, string kind)
        where TAnswer : Answer
    {
        ArgumentNullException.ThrowIfNull(id);

        if (!_answers.TryGetValue(id, out var answer))
        {
            throw new KeyNotFoundException(
                $"The response contains no answer for question id '{id}'. Known ids: " +
                $"{string.Join(", ", _answers.Keys)}.");
        }

        if (answer is TAnswer typed)
        {
            return typed;
        }

        throw new KeyNotFoundException(
            $"The answer for question id '{id}' is a '{answer.Type}' answer, not a '{kind}' answer. " +
            "Use Answers, or the matching accessor, to read it.");
    }
}
