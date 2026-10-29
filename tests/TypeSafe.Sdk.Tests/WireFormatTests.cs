using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypeSafeAI.Tests;

/// <summary>
/// Verifies that the SDK puts exactly the documented bytes on the wire.
/// </summary>
/// <remarks>
/// The expected values are the request bodies printed in the TypeSafe documentation, so these
/// tests compare the SDK against the published contract rather than against its own output.
/// </remarks>
public sealed class WireFormatTests
{
    [Fact]
    public async Task MinimalNoulRequestMatchesTheDocumentedBody()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(
            "Help! My payouts have been failing for 3 days.",
            [new NoulQuestion("is_urgent", "Does this convey urgency?")]);

        AssertJsonEquivalent(Fixtures.MinimalNoulRequest, handler.LastRequest.Body!);
    }

    [Fact]
    public async Task NoulRequestWithCriteriaMatchesTheDocumentedBody()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(
            "Help! My payouts have been failing for 3 days.",
            [
                new NoulQuestion(
                    "is_urgent",
                    "Does this convey urgency?",
                    new NoulCriteria(
                        JsonValue.Create("Explicitly time-sensitive"),
                        JsonValue.Create("No urgency expressed"))),
            ]);

        AssertJsonEquivalent(Fixtures.NoulWithCriteriaRequest, handler.LastRequest.Body!);
    }

    [Fact]
    public async Task ChoiceRequestMatchesTheDocumentedBody()
    {
        var (client, handler) = TestClient.Returning(Fixtures.ChoiceResponse);

        await client.SystemOneAsync(
            "Help! My payouts have been failing for 3 days.",
            [
                new ChoiceQuestion(
                    "department",
                    "Which team should handle this?",
                    new Dictionary<string, string?>
                    {
                        ["billing"] = "Payments, invoicing, refunds",
                        ["technical"] = "Bugs, outages, integrations",
                        ["sales"] = "Pricing, upgrades, new accounts",
                    }),
            ]);

        AssertJsonEquivalent(Fixtures.ChoiceRequest, handler.LastRequest.Body!);
    }

    [Fact]
    public async Task ScoreRequestMatchesTheDocumentedBody()
    {
        var (client, handler) = TestClient.Returning(Fixtures.ScoreResponse);

        await client.SystemOneAsync(
            "Help! My payouts have been failing for 3 days.",
            [new ScoreQuestion("frustration", "How frustrated is the customer?", ["Calm", "Frustrated", "Very angry"])]);

        AssertJsonEquivalent(Fixtures.ScoreRequest, handler.LastRequest.Body!);
    }

    [Fact]
    public async Task StateObjectIsSentAsStructuredJson()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(
            new JsonObject
            {
                ["ticket_message"] = "My flight was cancelled. Can I get a refund?",
                ["refund_policy"] = "Cancelled flights are eligible for a full refund.",
            },
            [new NoulQuestion("refund_requested", "Does `ticket_message` request a refund?")]);

        using var body = handler.LastRequest.ParseBody();
        var state = body.RootElement.GetProperty("state");

        Assert.Equal(JsonValueKind.Object, state.ValueKind);
        Assert.Equal(
            "My flight was cancelled. Can I get a refund?",
            state.GetProperty("ticket_message").GetString());
    }

    [Fact]
    public async Task StructuredInstructionsAreSentVerbatim()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(
            new JsonObject { ["source_text"] = "Invoice #4471 issued March 3, 2026." },
            [
                new NoulQuestion(
                    "invoice_number_is_correct",
                    new JsonObject
                    {
                        ["field"] = new JsonObject
                        {
                            ["name"] = "invoice_number",
                            ["type"] = "string",
                        },
                        ["extracted_value"] = "4471",
                        ["question"] = "Does `extracted_value` match the `field`?",
                    }),
            ]);

        using var body = handler.LastRequest.ParseBody();
        var instructions = body.RootElement
            .GetProperty("questions")
            .GetProperty("invoice_number_is_correct")
            .GetProperty("instructions");

        // The documented guidance is explicit that the keys inside structured instructions are not
        // part of the API and none are reserved, so the SDK must not reshape or rename them.
        Assert.Equal("invoice_number", instructions.GetProperty("field").GetProperty("name").GetString());
        Assert.Equal("4471", instructions.GetProperty("extracted_value").GetString());
    }

    [Fact]
    public async Task NullChoiceDescriptionsArePreserved()
    {
        var (client, handler) = TestClient.Returning(Fixtures.ChoiceResponse);

        await client.SystemOneAsync(
            "text",
            [new ChoiceQuestion("tone", "What is the tone?", ["calm", "angry"])]);

        using var body = handler.LastRequest.ParseBody();
        var criteria = body.RootElement.GetProperty("questions").GetProperty("tone").GetProperty("criteria");

        // The API reference types choice criteria as map<string, string | null>, and null is a
        // meaningful value meaning "this option needs no extra detail".
        Assert.Equal(JsonValueKind.Null, criteria.GetProperty("calm").ValueKind);
        Assert.Equal(JsonValueKind.Null, criteria.GetProperty("angry").ValueKind);
    }

    [Fact]
    public async Task SerializationIsDeterministic()
    {
        var (first, firstHandler) = TestClient.Returning(TypeSafeAI.Tests.TestClient.TriageResponse);
        var (second, secondHandler) = TestClient.Returning(TypeSafeAI.Tests.TestClient.TriageResponse);

        await first.SystemOneAsync("state", TestClient.TriageQuestions());
        await second.SystemOneAsync("state", TestClient.TriageQuestions());

        // Deterministic output is what lets a caller hash, cache, and replay a request body.
        Assert.Equal(firstHandler.LastRequest.Body, secondHandler.LastRequest.Body);
    }

    [Fact]
    public async Task ExtraBodyFieldsAreMergedLastAndCanOverride()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(new SystemOneRequest
        {
            State = "text",
            Model = "jev-latest",
            Questions = [new NoulQuestion("a", "question?")],
            AdditionalProperties = new Dictionary<string, JsonNode?>
            {
                // The documented escape hatch passes fields the SDK does not model, including an
                // undocumented server field. Merging is last-write-wins.
                ["beam_width"] = 4,
                ["model"] = "jev-1.12",
            },
        });

        using var body = handler.LastRequest.ParseBody();

        Assert.Equal(4, body.RootElement.GetProperty("beam_width").GetInt32());
        Assert.Equal("jev-1.12", body.RootElement.GetProperty("model").GetString());

        // A colliding key must not produce a duplicate JSON property.
        var modelCount = 0;
        foreach (var property in body.RootElement.EnumerateObject())
        {
            if (property.NameEquals("model"))
            {
                modelCount++;
            }
        }

        Assert.Equal(1, modelCount);
    }

    [Fact]
    public async Task ExtraBodyFieldsCanOverrideQuestions()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("original", "question?")],
            AdditionalProperties = new Dictionary<string, JsonNode?>
            {
                ["questions"] = new JsonObject
                {
                    ["replacement"] = new JsonObject
                    {
                        ["type"] = "noul",
                        ["instructions"] = "replacement question?",
                    },
                },
            },
        });

        using var body = handler.LastRequest.ParseBody();
        var questions = body.RootElement.GetProperty("questions");

        Assert.False(questions.TryGetProperty("original", out _));
        Assert.Equal(
            "replacement question?",
            questions.GetProperty("replacement").GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task PerCallModelOverridesTheClientDefault()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(new SystemOneRequest
        {
            State = "text",
            Model = "jev-1.12",
            Questions = [new NoulQuestion("a", "question?")],
        });

        using var body = handler.LastRequest.ParseBody();
        Assert.Equal("jev-1.12", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task RawQuestionPassesUnmodelledFieldsThrough()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(
            "text",
            [
                new RawQuestion(
                    "future",
                    "some_future_kind",
                    new JsonObject
                    {
                        ["instructions"] = "A question kind this SDK does not model.",
                        ["weight"] = 2,
                    }),
            ]);

        using var body = handler.LastRequest.ParseBody();
        var question = body.RootElement.GetProperty("questions").GetProperty("future");

        Assert.Equal("some_future_kind", question.GetProperty("type").GetString());
        Assert.Equal(2, question.GetProperty("weight").GetInt32());
    }

    [Fact]
    public async Task ExtraQuestionFieldsAreMerged()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(
            "text",
            [
                new NoulQuestion(
                    "weighted",
                    "question?",
                    criteria: null,
                    additionalProperties: new Dictionary<string, JsonNode?> { ["weight"] = 2 }),
            ]);

        using var body = handler.LastRequest.ParseBody();
        Assert.Equal(
            2,
            body.RootElement.GetProperty("questions").GetProperty("weighted").GetProperty("weight").GetInt32());
    }

    [Fact]
    public async Task QuestionIdsAreSentVerbatimWithoutSanitising()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        // Real ids observed in the TypeSafe cookbooks use these characters.
        string[] ids =
        [
            "plot_price.style?",
            "gate::prose_suffices",
            "__overall__::judge",
            "compare_returns.symbols.NVDA",
            "type_B014",
        ];

        await client.SystemOneAsync("text", [.. ids.Select(id => new NoulQuestion(id, "question?"))]);

        using var body = handler.LastRequest.ParseBody();
        var questions = body.RootElement.GetProperty("questions");

        foreach (var id in ids)
        {
            Assert.True(questions.TryGetProperty(id, out _), $"Question id '{id}' was not sent verbatim.");
        }
    }

    [Fact]
    public async Task SerializedStateDoesNotUseTheSdkNamingPolicy()
    {
        // The SDK applies snake_case to its own envelope, but the state is the caller's data, so a
        // caller's property names must survive untouched.
        var state = new { OrderId = "A-104", AmountUsd = 49 };

        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);
        await client.SystemOneAsync(state, [new NoulQuestion("a", "question?")]);

        using var body = handler.LastRequest.ParseBody();
        var sent = body.RootElement.GetProperty("state");

        Assert.True(sent.TryGetProperty("OrderId", out _), "The caller's property name was renamed.");
        Assert.Equal("A-104", sent.GetProperty("OrderId").GetString());
    }

    [Fact]
    public async Task SerializedRequestIsTheBodyTheClientSends()
    {
        var request = new SystemOneRequest
        {
            State = "Mijn uitbetalingen mislukken al drie dagen — één na één.",
            Questions = TestClient.TriageQuestions(),
            Options = new TypeSafeRequestOptions
            {
                Model = "jev-2026-01",
                AdditionalBodyProperties = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["future_field"] = JsonValue.Create(true),
                },
            },
        };

        // Measure first, then send: the order a caller uses, and it proves measuring leaves the
        // request intact.
        var measured = Serialization.TypeSafeJson.Serialize(request);

        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);
        await client.SystemOneAsync(request);

        // Exact, not JSON-equivalent: a size measurement is only useful if the bytes are the same.
        Assert.Equal(handler.LastRequest.Body, measured);

        // Parity alone would also hold if both paths shared the same bug, so the measured body is
        // checked against the documented shape as well.
        AssertJsonEquivalent(
            """
            {
              "state": "Mijn uitbetalingen mislukken al drie dagen — één na één.",
              "model": "jev-2026-01",
              "questions": {
                "is_urgent": { "type": "noul", "instructions": "Does this convey urgency?" },
                "department": {
                  "type": "choice",
                  "instructions": "Which team should handle this?",
                  "criteria": { "billing": null, "technical": null, "sales": null }
                },
                "frustration": {
                  "type": "score",
                  "instructions": "How frustrated is the customer?",
                  "criteria": ["Calm", "Frustrated", "Very angry"]
                }
              },
              "future_field": true
            }
            """,
            measured);
    }

    [Fact]
    public async Task SerializedRequestKeepsTheClientsCollisionRules()
    {
        // Additional properties named like the SDK's own fields replace them, even an explicit Model.
        var request = new SystemOneRequest
        {
            State = "ignored",
            Questions = [new NoulQuestion("a", "question?")],
            Model = "ignored-too",
            AdditionalProperties = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["state"] = JsonValue.Create("replaced"),
                ["model"] = JsonValue.Create("replaced-model"),
            },
        };

        var measured = Serialization.TypeSafeJson.Serialize(request);

        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);
        await client.SystemOneAsync(request);

        Assert.Equal(handler.LastRequest.Body, measured);

        using var body = JsonDocument.Parse(measured);
        Assert.Equal("replaced", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("replaced-model", body.RootElement.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(null, null, TypeSafeDefaults.DefaultModel)]
    [InlineData(null, "from-options", "from-options")]
    [InlineData("from-request", "from-options", "from-request")]
    [InlineData("from-request", null, "from-request")]
    public void SerializedRequestModelFollowsTheClientPrecedence(string? model, string? optionsModel, string expected)
    {
        var request = new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "question?")],
            Model = model,
            Options = optionsModel is null ? null : new TypeSafeRequestOptions { Model = optionsModel },
        };

        using var body = JsonDocument.Parse(Serialization.TypeSafeJson.Serialize(request));

        Assert.Equal(expected, body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void SerializedRequestAdditionalPropertiesReplaceTheOptionsSet()
    {
        var request = new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "question?")],
            AdditionalProperties = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["from_request"] = JsonValue.Create(1),
            },
            Options = new TypeSafeRequestOptions
            {
                AdditionalBodyProperties = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["from_options"] = JsonValue.Create(2),
                },
            },
        };

        using var body = JsonDocument.Parse(Serialization.TypeSafeJson.Serialize(request));

        // The client replaces the options' set with the request's rather than merging the two.
        Assert.True(body.RootElement.TryGetProperty("from_request", out _));
        Assert.False(body.RootElement.TryGetProperty("from_options", out _));
    }

    [Fact]
    public void SerializedRequestEscapesNonAsciiText()
    {
        // Dutch text: every accented letter is escaped to six ASCII characters on the wire. A plain
        // string state therefore serializes to pure ASCII, where Length and the byte count agree.
        const string Text = "Café, één, über, naïef, façade.";
        var request = new SystemOneRequest
        {
            State = Text,
            Questions = [new NoulQuestion("a", "question?")],
        };

        var json = Serialization.TypeSafeJson.Serialize(request);

        Assert.All(json, c => Assert.True(c < 128, $"Non-ASCII character '{c}' in the serialized request."));
        Assert.Equal(Encoding.UTF8.GetByteCount(json), json.Length);
        Assert.Contains("u00E9", json, StringComparison.Ordinal);

        // The trap this method exists to avoid: counting characters undercounts what the SDK sends.
        // The same request with an ASCII state of the same length is 30 bytes shorter: six accented
        // letters, each one character in the text and six on the wire.
        var ascii = Serialization.TypeSafeJson.Serialize(new SystemOneRequest
        {
            State = new string('x', Text.Length),
            Questions = request.Questions,
        });
        Assert.Equal(ascii.Length + 6 * 5, json.Length);
    }

    [Fact]
    public void SerializeRequestRejectsWhatTheClientRejects()
    {
        Assert.Throws<ArgumentNullException>(() => Serialization.TypeSafeJson.Serialize((SystemOneRequest)null!));

        var nullState = new SystemOneRequest { State = null, Questions = [new NoulQuestion("a", "q?")] };
        Assert.Throws<ArgumentException>(() => Serialization.TypeSafeJson.Serialize(nullState));

        var noQuestions = new SystemOneRequest { State = "text", Questions = [] };
        Assert.Throws<ArgumentException>(() => Serialization.TypeSafeJson.Serialize(noQuestions));

        var nullQuestions = new SystemOneRequest { State = "text", Questions = null! };
        Assert.Throws<ArgumentNullException>(() => Serialization.TypeSafeJson.Serialize(nullQuestions));

        var nullQuestion = new SystemOneRequest { State = "text", Questions = [null!] };
        Assert.Throws<ArgumentNullException>(() => Serialization.TypeSafeJson.Serialize(nullQuestion));

        var duplicateIds = new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "q?"), new NoulQuestion("a", "again?")],
        };
        Assert.Throws<ArgumentException>(() => Serialization.TypeSafeJson.Serialize(duplicateIds));

        var removedField = new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "q?")],
            AdditionalProperties = new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["document"] = null },
        };
        Assert.Throws<ArgumentException>(() => Serialization.TypeSafeJson.Serialize(removedField));

        var removedFieldViaOptions = new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "q?")],
            Options = new TypeSafeRequestOptions
            {
                AdditionalBodyProperties = new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["document"] = null },
            },
        };
        Assert.Throws<ArgumentException>(() => Serialization.TypeSafeJson.Serialize(removedFieldViaOptions));
    }

    private static void AssertJsonEquivalent(string expected, string actual)
    {
        var expectedNode = JsonNode.Parse(expected);
        var actualNode = JsonNode.Parse(actual);

        Assert.True(
            JsonNode.DeepEquals(expectedNode, actualNode),
            $"The request body did not match the documented shape.\nExpected: {expected}\nActual:   {actual}");
    }
}
