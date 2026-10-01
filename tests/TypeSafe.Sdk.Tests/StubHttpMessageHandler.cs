using System.Net;
using System.Text;

namespace TypeSafeAI.Tests;

/// <summary>
/// A captured request, snapshotted because the client disposes the request it sent.
/// </summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The absolute request URI.</param>
/// <param name="Body">The request body as text, or <see langword="null"/> when there was none.</param>
/// <param name="Headers">The request headers, keyed by name.</param>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>
    /// Parses the request body as JSON.
    /// </summary>
    /// <returns>The parsed body.</returns>
    public System.Text.Json.JsonDocument ParseBody() => System.Text.Json.JsonDocument.Parse(Body!);
}

/// <summary>
/// Replaces the network with a scripted responder, and records every attempt.
/// </summary>
/// <remarks>
/// Hand-written rather than pulled from a mocking library: the SDK needs to assert on the exact
/// bytes it put on the wire, and a scripted responder expresses that more directly than a
/// mock-framework fluent API would.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<int, CapturedRequest, CancellationToken, Task<HttpResponseMessage>> _responder;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubHttpMessageHandler"/> class.
    /// </summary>
    /// <param name="responder">
    /// Returns the response for the given one-based attempt number and captured request.
    /// </param>
    public StubHttpMessageHandler(Func<int, CapturedRequest, HttpResponseMessage> responder)
        : this((attempt, request, _) => Task.FromResult(responder(attempt, request)))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StubHttpMessageHandler"/> class whose responder
    /// may be asynchronous, for exercising timeouts and cancellation.
    /// </summary>
    /// <param name="responder">
    /// Returns the response for the given one-based attempt number and captured request.
    /// </param>
    public StubHttpMessageHandler(
        Func<int, CapturedRequest, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StubHttpMessageHandler"/> class that always
    /// returns the same response.
    /// </summary>
    /// <param name="response">The response to return.</param>
    public StubHttpMessageHandler(HttpResponseMessage response)
        : this((_, _) => response)
    {
    }

    /// <summary>
    /// Creates a responder that waits before answering, for timeout and cancellation tests.
    /// </summary>
    /// <param name="delay">How long to wait.</param>
    /// <param name="response">The response to return.</param>
    /// <returns>The responder.</returns>
    public static Func<int, CapturedRequest, CancellationToken, Task<HttpResponseMessage>> Slow(
        TimeSpan delay,
        HttpResponseMessage response) =>
        async (_, _, cancellationToken) =>
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return response;
        };

    public List<CapturedRequest> Requests { get; } = [];

    public int Attempts => Requests.Count;

    public CapturedRequest LastRequest => Requests[^1];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        var captured = new CapturedRequest(request.Method, request.RequestUri!, body, headers);
        Requests.Add(captured);

        cancellationToken.ThrowIfCancellationRequested();

        return await _responder(Requests.Count, captured, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a JSON response.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="statusCode">The status code.</param>
    /// <param name="requestId">The value of the <c>x-typesafe-request-id</c> header.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Json(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? requestId = "req_01a0ab8265a27733a1bc672bfe98d906")
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (requestId is not null)
        {
            response.Headers.TryAddWithoutValidation("x-typesafe-request-id", requestId);
        }

        return response;
    }
}

/// <summary>
/// A non-seekable response body of a given length that starts with a prefix and is padded with
/// spaces, so it carries no Content-Length unless the test sets one.
/// </summary>
/// <param name="prefix">The bytes the body starts with.</param>
/// <param name="length">The total length, or <see cref="long.MaxValue"/> for an endless body.</param>
/// <param name="atEnd">
/// Runs when the body is exhausted instead of returning end of stream, so a test can fail or stall
/// the read midway.
/// </param>
internal sealed class FakeBodyStream(byte[] prefix, long length, Func<CancellationToken, Task>? atEnd = null) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (BytesRead >= length)
        {
            if (atEnd is not null)
            {
                await atEnd(cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }

        var count = (int)Math.Min(buffer.Length, length - BytesRead);
        for (var i = 0; i < count; i++)
        {
            var position = BytesRead + i;
            buffer.Span[i] = position < prefix.Length ? prefix[position] : (byte)' ';
        }

        BytesRead += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
