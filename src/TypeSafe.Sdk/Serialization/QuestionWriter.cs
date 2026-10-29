using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypeSafeAI.Serialization;

/// <summary>
/// Writes a <see cref="Question"/> in the TypeSafe wire format.
/// </summary>
/// <remarks>
/// Shared by <see cref="QuestionJsonConverter"/> and by the request builder, so a question is
/// serialized by exactly one code path whether it is sent to the API or written to a cache.
/// </remarks>
internal static class QuestionWriter
{
    /// <summary>
    /// Writes one question object.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="question">The question to write.</param>
    /// <param name="includeId">
    /// Whether to write the question's <c>id</c> as a member. On the wire the id is the key of the
    /// questions map and is never a member of the question object, so the request writer passes
    /// <see langword="false"/>. A question serialized on its own passes <see langword="true"/> so
    /// that it round-trips losslessly through a cache.
    /// </param>
    internal static void Write(Utf8JsonWriter writer, Question question, bool includeId = false)
    {
        writer.WriteStartObject();

        if (includeId)
        {
            writer.WriteString("id", question.Id);
        }

        writer.WriteString("type", question.Type);

        if (question is NoulQuestion noul)
        {
            WriteInstructions(writer, noul.Instructions);
            WriteNoulCriteria(writer, noul.Criteria);
        }
        else if (question is ChoiceQuestion choice)
        {
            WriteInstructions(writer, choice.Instructions);
            WriteChoiceCriteria(writer, choice);
        }
        else if (question is ScoreQuestion score)
        {
            WriteInstructions(writer, score.Instructions);
            WriteScoreCriteria(writer, score);
        }
        else if (question is BoxQuestion box)
        {
            WriteInstructions(writer, box.Instructions);
        }
        else if (question is RawQuestion raw)
        {
            WriteInstructions(writer, raw.Instructions);
            WriteRawBody(writer, raw.Body);
        }

        foreach (var (name, value) in question.AdditionalProperties)
        {
            // Extra fields are merged last so a caller can always override an SDK field, which is
            // the documented last-write-wins behaviour of the sibling SDKs' escape hatch.
            if (value is null)
            {
                writer.WriteNull(name);
            }
            else
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteRawBody(Utf8JsonWriter writer, JsonObject body)
    {
        foreach (var (name, value) in body)
        {
            if (value is null)
            {
                writer.WriteNull(name);
            }
            else
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
        }
    }

    private static void WriteInstructions(Utf8JsonWriter writer, JsonNode? instructions)
    {
        if (instructions is null)
        {
            // Omitted rather than written as null. The HTTP reference marks instructions as
            // required while both sibling SDKs treat it as optional; sending the member only when
            // the caller set it leaves the server as the authority.
            return;
        }

        writer.WritePropertyName("instructions");
        instructions.WriteTo(writer);
    }

    private static void WriteNoulCriteria(Utf8JsonWriter writer, NoulCriteria? criteria)
    {
        if (criteria is null)
        {
            return;
        }

        writer.WriteStartObject("criteria");

        if (criteria.True is { } yes)
        {
            writer.WritePropertyName("true");
            yes.WriteTo(writer);
        }

        if (criteria.False is { } no)
        {
            writer.WritePropertyName("false");
            no.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static void WriteChoiceCriteria(Utf8JsonWriter writer, ChoiceQuestion question)
    {
        writer.WriteStartObject("criteria");

        foreach (var label in question.Labels)
        {
            var description = question.Criteria[label];

            if (description is null)
            {
                writer.WriteNull(label);
            }
            else
            {
                writer.WritePropertyName(label);
                description.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteScoreCriteria(Utf8JsonWriter writer, ScoreQuestion question)
    {
        writer.WriteStartArray("criteria");

        foreach (var level in question.Criteria)
        {
            if (level is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                level.WriteTo(writer);
            }
        }

        writer.WriteEndArray();
    }
}
