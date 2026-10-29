using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypeSafeAI.Tests;

/// <summary>
/// Covers the features released alongside Jev 1.14: Box questions, image assets, and the
/// danger-zone opt-in headers.
/// </summary>
public sealed class ImagesAndBoxTests
{
    /// <summary>
    /// The 16x16 PNG used in the API reference's Box example.
    /// </summary>
    private const string RedRectanglePng =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAHElEQVR4nGP4TyJgoKcGBgbsaFQDfTUQB4aDBgDAen2fpitsVAAAAABJRU5ErkJggg==";

    /// <summary>
    /// The Box response printed in the API reference.
    /// </summary>
    private const string BoxResponse = """
        {
          "model": "jev-1.14.0",
          "answers": {
            "red_rectangle": {
              "type": "box",
              "box": { "top": 0.2, "bottom": 0.66, "left": 0.19, "right": 0.68 },
              "confidence": null
            }
          },
          "usage": { "input_tokens": 285, "output_tokens": 20 }
        }
        """;

    [Fact]
    public async Task BoxRequestMatchesTheDocumentedWireFormat()
    {
        var (client, handler) = TestClient.Returning(BoxResponse);

        await client.SystemOneAsync(new SystemOneRequest
        {
            Model = "jev-latest",
            State = "photo",
            Questions = [new BoxQuestion("red_rectangle", "Locate the red rectangle.")],
            Assets = new Dictionary<string, ImageAsset>
            {
                ["photo"] = ImageAsset.FromBase64(RedRectanglePng, ImageFormat.Png),
            },
        });

        using var body = handler.LastRequest.ParseBody();
        var root = body.RootElement;

        var question = root.GetProperty("questions").GetProperty("red_rectangle");
        Assert.Equal("box", question.GetProperty("type").GetString());
        Assert.Equal("Locate the red rectangle.", question.GetProperty("instructions").GetString());

        var asset = root.GetProperty("assets").GetProperty("photo");
        Assert.Equal("image", asset.GetProperty("type").GetString());
        Assert.Equal("png", asset.GetProperty("format").GetString());
        Assert.Equal(RedRectanglePng, asset.GetProperty("data_base64").GetString());
    }

    [Fact]
    public async Task BoxAnswerParsesEdgesAndANullConfidence()
    {
        var (client, _) = TestClient.Returning(BoxResponse);
        var question = new BoxQuestion("red_rectangle", "Locate the red rectangle.");

        var result = await client.SystemOneAsync(new SystemOneRequest
        {
            State = "photo",
            Questions = [question],
            Assets = new Dictionary<string, ImageAsset>
            {
                ["photo"] = ImageAsset.FromBase64(RedRectanglePng, ImageFormat.Png),
            },
        });

        var box = result.Get(question);

        Assert.Equal("jev-1.14.0", result.Model);
        Assert.Equal(0.2, box.Top);
        Assert.Equal(0.66, box.Bottom);
        Assert.Equal(0.19, box.Left);
        Assert.Equal(0.68, box.Right);
        Assert.Equal(0.49, box.Width, precision: 10);
        Assert.Null(box.Confidence);
        Assert.False(box.TryGetConfidence(out _));
        Assert.Same(box, result.Box("red_rectangle"));
        Assert.Same(box, result.Boxes["red_rectangle"]);
        Assert.Empty(result.UnknownAnswers);
    }

    [Fact]
    public void BoxAnswerReportsAConfidenceWhenPresent()
    {
        var answer = JsonSerializer.Deserialize<Answer>("""
            { "id": "b", "type": "box", "box": { "top": 0, "bottom": 1, "left": 0, "right": 1 }, "confidence": 0.9 }
            """);

        var box = Assert.IsType<BoxAnswer>(answer);
        Assert.True(box.TryGetConfidence(out var confidence));
        Assert.Equal(0.9, confidence);
    }

    [Fact]
    public void BoxAnswerRoundTripsKeepingANullConfidence()
    {
        var original = new BoxAnswer("b", 0.1, 0.5, 0.2, 0.7, confidence: null);

        var json = JsonSerializer.Serialize<Answer>(original);
        var node = JsonNode.Parse(json)!;
        var copy = Assert.IsType<BoxAnswer>(JsonSerializer.Deserialize<Answer>(json));

        // The member is required but nullable on the wire, so it must be written, as null.
        Assert.True(node.AsObject().ContainsKey("confidence"));
        Assert.Null(node["confidence"]);
        Assert.Equal(original.Top, copy.Top);
        Assert.Equal(original.Right, copy.Right);
        Assert.Null(copy.Confidence);
    }

    [Fact]
    public void BoxQuestionRoundTrips()
    {
        var json = JsonSerializer.Serialize<Question>(new BoxQuestion("where", "Find the dog."));
        var copy = Assert.IsType<BoxQuestion>(JsonSerializer.Deserialize<Question>(json));

        Assert.Equal("where", copy.Id);
        Assert.Equal("Find the dog.", copy.Instructions!.GetValue<string>());
    }

    [Fact]
    public async Task ImagesHeaderIsSentForBoxQuestions()
    {
        var (client, handler) = TestClient.Returning(BoxResponse);

        await client.SystemOneAsync(new SystemOneRequest
        {
            State = "photo",
            Questions = [new BoxQuestion("red_rectangle", "Locate the red rectangle.")],
            Assets = new Dictionary<string, ImageAsset>
            {
                ["photo"] = ImageAsset.FromBase64(RedRectanglePng, ImageFormat.Png),
            },
        });

        Assert.Equal("true", handler.LastRequest.Headers[TypeSafeDefaults.ImagesHeader]);
        Assert.False(handler.LastRequest.Headers.ContainsKey(TypeSafeDefaults.LongContextHeader));
    }

    [Fact]
    public async Task ImagesHeaderIsSentForAssetsWithoutABoxQuestion()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(new SystemOneRequest
        {
            State = new JsonObject { ["door"] = "a3f1c2e8-door" },
            Questions = [new NoulQuestion("person_present", "Is there a person in `door`?")],
            Assets = new Dictionary<string, ImageAsset>
            {
                ["a3f1c2e8-door"] = ImageAsset.FromBase64(RedRectanglePng, ImageFormat.Png),
            },
        });

        Assert.Equal("true", handler.LastRequest.Headers[TypeSafeDefaults.ImagesHeader]);
    }

    [Fact]
    public async Task ImagesHeaderIsSentForAssetsPassedAsAnAdditionalBodyField()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(new SystemOneRequest
        {
            State = "photo",
            Questions = [new NoulQuestion("a", "Is there a dog in this picture: photo?")],
            AdditionalProperties = new Dictionary<string, JsonNode?>
            {
                ["assets"] = new JsonObject
                {
                    ["photo"] = new JsonObject { ["type"] = "image", ["format"] = "png", ["data_base64"] = RedRectanglePng },
                },
            },
        });

        Assert.Equal("true", handler.LastRequest.Headers[TypeSafeDefaults.ImagesHeader]);
    }

    [Fact]
    public async Task TextOnlyRequestsSendNoDangerZoneHeadersAndNoAssets()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync("text", [new NoulQuestion("a", "question?")]);

        Assert.False(handler.LastRequest.Headers.ContainsKey(TypeSafeDefaults.ImagesHeader));
        Assert.False(handler.LastRequest.Headers.ContainsKey(TypeSafeDefaults.LongContextHeader));

        using var body = handler.LastRequest.ParseBody();
        Assert.False(body.RootElement.TryGetProperty("assets", out _));
    }

    [Fact]
    public async Task LongContextSendsItsHeader()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(new SystemOneRequest
        {
            State = "a long contract",
            Questions = [new NoulQuestion("a", "question?")],
            Options = new TypeSafeRequestOptions { LongContext = true },
        });

        Assert.Equal("true", handler.LastRequest.Headers[TypeSafeDefaults.LongContextHeader]);
        Assert.False(handler.LastRequest.Headers.ContainsKey(TypeSafeDefaults.ImagesHeader));
    }

    [Fact]
    public async Task ACallerHeaderWinsOverAnAutomaticOne()
    {
        var (client, handler) = TestClient.Returning(BoxResponse);

        await client.SystemOneAsync(new SystemOneRequest
        {
            State = "photo",
            Questions = [new BoxQuestion("red_rectangle", "Locate the red rectangle.")],
            Assets = new Dictionary<string, ImageAsset>
            {
                ["photo"] = ImageAsset.FromBase64(RedRectanglePng, ImageFormat.Png),
            },
            Options = new TypeSafeRequestOptions
            {
                Headers = new Dictionary<string, string> { ["typesafe-danger-zone-images"] = "custom" },
            },
        });

        Assert.Equal("custom", handler.LastRequest.Headers[TypeSafeDefaults.ImagesHeader]);
    }

    [Fact]
    public void ImageAssetDetectsPngAndJpegFromBytes()
    {
        var png = ImageAsset.FromBytes(Convert.FromBase64String(RedRectanglePng));
        var jpeg = ImageAsset.FromBytes(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 });

        Assert.Equal(ImageFormat.Png, png.Format);
        Assert.Equal(RedRectanglePng, png.DataBase64);
        Assert.Equal(ImageFormat.Jpeg, jpeg.Format);
    }

    [Fact]
    public void ImageAssetRejectsUnknownFormatsAndBadBase64()
    {
        Assert.Throws<ArgumentException>(() => ImageAsset.FromBytes("GIF89a"u8));
        Assert.Throws<ArgumentException>(() => ImageAsset.FromBytes(ReadOnlySpan<byte>.Empty, ImageFormat.Png));
        Assert.Throws<ArgumentException>(() => ImageAsset.FromBase64("data:image/png;base64,AAAA", ImageFormat.Png));
        Assert.Throws<ArgumentException>(() => ImageAsset.FromBase64("not base64!", ImageFormat.Png));
        Assert.Throws<ArgumentException>(() => ImageAsset.FromBase64("AAAA\nAAAA", ImageFormat.Png));
        Assert.Throws<ArgumentException>(() => ImageAsset.FromBase64("AAAA AAAA", ImageFormat.Png));
    }

    [Fact]
    public async Task ImageAssetReadsAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(RedRectanglePng));

        try
        {
            var asset = await ImageAsset.FromFileAsync(path);
            Assert.Equal(ImageFormat.Png, asset.Format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task JpegAssetsUseTheJpgWireFormat()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await client.SystemOneAsync(new SystemOneRequest
        {
            State = "ref",
            Questions = [new NoulQuestion("a", "question?")],
            Assets = new Dictionary<string, ImageAsset>
            {
                ["ref"] = ImageAsset.FromBytes(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }),
            },
        });

        using var body = handler.LastRequest.ParseBody();
        Assert.Equal("jpg", body.RootElement.GetProperty("assets").GetProperty("ref").GetProperty("format").GetString());
    }

    [Fact]
    public async Task AnEmptyAssetReferenceIsRejectedLocally()
    {
        var (client, handler) = TestClient.Returning(Fixtures.NoulResponse);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SystemOneAsync(new SystemOneRequest
        {
            State = "text",
            Questions = [new NoulQuestion("a", "question?")],
            Assets = new Dictionary<string, ImageAsset>
            {
                [" "] = ImageAsset.FromBase64(RedRectanglePng, ImageFormat.Png),
            },
        }));

        Assert.Empty(handler.Requests);
    }
}
