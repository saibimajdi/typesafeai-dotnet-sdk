using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TypeSafeAI.Serialization;

namespace TypeSafeAI;

/// <summary>
/// The typed value returned for one question.
/// </summary>
/// <remarks>
/// <para>
/// Every answer is constrained to the options supplied in the question, so it never has to be
/// recovered from generated prose. Every answer is also independent of the others, which is why
/// questions can be added or removed without disturbing the rest.
/// </para>
/// <para>
/// Answers are immutable and safe to cache. They round-trip through
/// <see cref="System.Text.Json.JsonSerializer"/>, so a decision can be re-scored against cached
/// answers without paying for inference again.
/// </para>
/// </remarks>
[JsonConverter(typeof(AnswerJsonConverter))]
public abstract class Answer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Answer"/> class.
    /// </summary>
    /// <param name="id">The id of the question this answer responds to.</param>
    /// <param name="additionalProperties">
    /// Response fields this SDK version does not model, preserved so nothing is silently dropped.
    /// </param>
    protected Answer(string id, IReadOnlyDictionary<string, JsonNode?>? additionalProperties = null)
    {
        Id = id;
        AdditionalProperties = additionalProperties ?? EmptyProperties;
    }

    private static IReadOnlyDictionary<string, JsonNode?> EmptyProperties { get; } =
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the id of the question this answer responds to.
    /// </summary>
    /// <remarks>
    /// This is taken from the key the answer arrived under, not from the body, because question
    /// ids are never sent to the model.
    /// </remarks>
    public string Id { get; }

    /// <summary>
    /// Gets the answer fields this SDK version does not model.
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode?> AdditionalProperties { get; }

    /// <summary>
    /// Gets the value of this answer's <c>type</c> discriminator.
    /// </summary>
    public abstract string Type { get; }

    /// <summary>
    /// Gets the answer's confidence, when the answer kind carries one.
    /// </summary>
    /// <param name="confidence">
    /// When this method returns <see langword="true"/>, the answer's confidence between <c>0</c>
    /// and <c>1</c>; otherwise <c>0</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> for <see cref="ChoiceAnswer"/> and <see cref="ScoreAnswer"/>, and for
    /// a <see cref="BoxAnswer"/> whose confidence the API reported;
    /// <see langword="false"/> for every other answer, including <see cref="NoulAnswer"/>, which
    /// deliberately has no confidence of its own.
    /// </returns>
    /// <remarks>
    /// Use this rather than a nullable property so that a noul's absent confidence cannot be read
    /// as a real value of zero.
    /// </remarks>
    public abstract bool TryGetConfidence(out double confidence);

    /// <summary>
    /// Returns a compact description of the answer, for diagnostics.
    /// </summary>
    /// <returns>A string such as <c>choice:department=technical</c>.</returns>
    public override string ToString() => $"{Type}:{Id}";
}
