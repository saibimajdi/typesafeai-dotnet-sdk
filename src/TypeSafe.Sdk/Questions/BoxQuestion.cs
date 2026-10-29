using System.Text.Json.Nodes;

namespace TypeSafeAI;

/// <summary>
/// A question that locates what its instructions describe in the request's image. Its answer is
/// the four edges of a box, as fractions of the image's width and height.
/// </summary>
/// <remarks>
/// <para>
/// Box questions are an experimental API feature that needs Jev 1.14 or later and an image in the
/// request, supplied through <see cref="SystemOneRequest.Assets"/>. The SDK sends the
/// <see cref="TypeSafeDefaults.ImagesHeader"/> header automatically on any request that contains
/// a box question.
/// </para>
/// <para>
/// Ask a <see cref="NoulQuestion"/> alongside the box when the thing may be absent: a box answer
/// always reports a box, even when there is nothing to find.
/// </para>
/// </remarks>
public sealed class BoxQuestion : Question, IQuestion<BoxAnswer>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BoxQuestion"/> class.
    /// </summary>
    /// <param name="id">The id this question is keyed by.</param>
    /// <param name="instructions">What to locate in the image, or <see langword="null"/>.</param>
    /// <param name="additionalProperties">Extra JSON fields for API features this SDK does not model.</param>
    public BoxQuestion(
        string id,
        JsonNode? instructions = null,
        IReadOnlyDictionary<string, JsonNode?>? additionalProperties = null)
        : base(id, instructions, additionalProperties)
    {
    }

    /// <inheritdoc />
    public override string Type => "box";
}
