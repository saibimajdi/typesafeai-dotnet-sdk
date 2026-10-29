namespace TypeSafeAI;

/// <summary>
/// An image sent in a request's <see cref="SystemOneRequest.Assets"/>, base64-encoded into the
/// JSON body.
/// </summary>
/// <remarks>
/// <para>
/// Images are an experimental API feature. An asset is keyed by a reference string, and the image
/// takes the place of that reference wherever it appears as or inside a string in the state, a
/// question's instructions, or its criteria. Every asset must be referenced at least once, and
/// object keys are never replaced. Use a reference that cannot occur in your text by accident,
/// such as a new <see cref="Guid"/> per image.
/// </para>
/// <para>
/// The SDK sends the <see cref="TypeSafeDefaults.ImagesHeader"/> header automatically on any
/// request that carries assets.
/// </para>
/// </remarks>
public sealed class ImageAsset
{
    private ImageAsset(ImageFormat format, string dataBase64)
    {
        Format = format;
        DataBase64 = dataBase64;
    }

    /// <summary>
    /// Gets the image's format.
    /// </summary>
    public ImageFormat Format { get; }

    /// <summary>
    /// Gets the image's bytes, base64-encoded with the standard alphabet and padding.
    /// </summary>
    public string DataBase64 { get; }

    /// <summary>
    /// Gets the wire value of <see cref="Format"/>: <c>png</c> or <c>jpg</c>.
    /// </summary>
    internal string WireFormat => Format == ImageFormat.Png ? "png" : "jpg";

    /// <summary>
    /// Creates an asset from raw image bytes, detecting PNG or JPEG from the file signature.
    /// </summary>
    /// <param name="data">The image file's bytes.</param>
    /// <returns>The asset.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is empty or is neither a PNG nor a JPEG file.
    /// </exception>
    public static ImageAsset FromBytes(ReadOnlySpan<byte> data)
    {
        var format = Detect(data) ?? throw new ArgumentException(
            "The image is neither a PNG nor a JPEG file. The API accepts only those two formats; " +
            "convert the image first, or pass the format explicitly if detection is wrong.",
            nameof(data));

        return new ImageAsset(format, Convert.ToBase64String(data));
    }

    /// <summary>
    /// Creates an asset from raw image bytes in a known format.
    /// </summary>
    /// <param name="data">The image file's bytes.</param>
    /// <param name="format">The image's format.</param>
    /// <returns>The asset.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty.</exception>
    public static ImageAsset FromBytes(ReadOnlySpan<byte> data, ImageFormat format)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("The image data cannot be empty.", nameof(data));
        }

        return new ImageAsset(format, Convert.ToBase64String(data));
    }

    /// <summary>
    /// Creates an asset from an already base64-encoded image.
    /// </summary>
    /// <param name="dataBase64">
    /// The image file, base64-encoded with the standard alphabet and <c>=</c> padding, without a
    /// <c>data:</c> prefix or whitespace.
    /// </param>
    /// <param name="format">The image's format.</param>
    /// <returns>The asset.</returns>
    /// <exception cref="ArgumentException"><paramref name="dataBase64"/> is empty, starts with <c>data:</c>, or is not valid base64.</exception>
    public static ImageAsset FromBase64(string dataBase64, ImageFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataBase64);

        if (dataBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Pass the base64 payload without its 'data:' URL prefix.",
                nameof(dataBase64));
        }

        // Base64.IsValid tolerates whitespace, but the API requires none, so it is rejected first.
        if (dataBase64.AsSpan().IndexOfAny(" \t\r\n") >= 0 ||
            !System.Buffers.Text.Base64.IsValid(dataBase64))
        {
            throw new ArgumentException("The image data is not valid base64.", nameof(dataBase64));
        }

        return new ImageAsset(format, dataBase64);
    }

    /// <summary>
    /// Reads an image file and creates an asset from it, detecting PNG or JPEG from its contents.
    /// </summary>
    /// <param name="path">The path of the image file.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The asset.</returns>
    /// <exception cref="ArgumentException">The file is neither a PNG nor a JPEG file.</exception>
    public static async Task<ImageAsset> FromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return FromBytes(data);
    }

    private static ImageFormat? Detect(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        ReadOnlySpan<byte> jpeg = [0xFF, 0xD8, 0xFF];

        if (data.StartsWith(png))
        {
            return ImageFormat.Png;
        }

        if (data.StartsWith(jpeg))
        {
            return ImageFormat.Jpeg;
        }

        return null;
    }
}
