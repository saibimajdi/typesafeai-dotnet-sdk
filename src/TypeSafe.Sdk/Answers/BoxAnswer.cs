using System.Globalization;
using System.Text.Json.Nodes;

namespace TypeSafeAI;

/// <summary>
/// The answer to a <see cref="BoxQuestion"/>: the edges of a box around what was asked for, as
/// fractions of the image's height and width.
/// </summary>
/// <remarks>
/// Vertical edges are measured from the top of the image and horizontal edges from the left, so
/// a box covering the whole image is <c>Top = 0, Bottom = 1, Left = 0, Right = 1</c>. Each edge
/// is the expected value of its predicted position. Multiply by the image's pixel size to get
/// pixel coordinates.
/// </remarks>
public sealed class BoxAnswer : Answer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BoxAnswer"/> class.
    /// </summary>
    /// <param name="id">The id of the question this answer responds to.</param>
    /// <param name="top">The top edge, as a fraction of image height from the top.</param>
    /// <param name="bottom">The bottom edge, as a fraction of image height from the top.</param>
    /// <param name="left">The left edge, as a fraction of image width from the left.</param>
    /// <param name="right">The right edge, as a fraction of image width from the left.</param>
    /// <param name="confidence">The API's confidence in the box, or <see langword="null"/> when none was reported.</param>
    /// <param name="additionalProperties">Response fields this SDK version does not model.</param>
    public BoxAnswer(
        string id,
        double top,
        double bottom,
        double left,
        double right,
        double? confidence,
        IReadOnlyDictionary<string, JsonNode?>? additionalProperties = null)
        : base(id, additionalProperties)
    {
        Top = top;
        Bottom = bottom;
        Left = left;
        Right = right;
        Confidence = confidence;
    }

    /// <summary>
    /// Gets the top edge of the box, as a fraction of image height from the top (<c>0</c> to <c>1</c>).
    /// </summary>
    public double Top { get; }

    /// <summary>
    /// Gets the bottom edge of the box, as a fraction of image height from the top (<c>0</c> to <c>1</c>).
    /// </summary>
    public double Bottom { get; }

    /// <summary>
    /// Gets the left edge of the box, as a fraction of image width from the left (<c>0</c> to <c>1</c>).
    /// </summary>
    public double Left { get; }

    /// <summary>
    /// Gets the right edge of the box, as a fraction of image width from the left (<c>0</c> to <c>1</c>).
    /// </summary>
    public double Right { get; }

    /// <summary>
    /// Gets the box's width as a fraction of image width.
    /// </summary>
    public double Width => Right - Left;

    /// <summary>
    /// Gets the box's height as a fraction of image height.
    /// </summary>
    public double Height => Bottom - Top;

    /// <summary>
    /// Gets the API's confidence in the box, from <c>0</c> to <c>1</c>, or <see langword="null"/>
    /// when the model did not provide one.
    /// </summary>
    /// <remarks>
    /// The API sends <c>null</c> rather than omitting the field when it has no confidence, and the
    /// SDK never substitutes a value of its own.
    /// </remarks>
    public double? Confidence { get; }

    /// <inheritdoc />
    public override string Type => "box";

    /// <inheritdoc />
    /// <returns><see langword="true"/> only when the API reported a confidence for this box.</returns>
    public override bool TryGetConfidence(out double confidence)
    {
        confidence = Confidence.GetValueOrDefault();
        return Confidence.HasValue;
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Type}:{Id}=[top={Top}, bottom={Bottom}, left={Left}, right={Right}]");
}
