namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     What a link's page says about itself, read once after the link was created: its Open Graph (or standard
///     meta) title, description and image, and when they were read. Stored on the link's content; absent until the
///     fetch has found something, and for good when the page gave nothing or could not be fetched.
/// </summary>
[PublicAPI]
public sealed record LinkPreview
{
    /// <summary>
    ///     The most characters a preview title keeps; the same as a link name may have.
    /// </summary>
    public const int MaxTitleLength = 256;

    public const int MaxDescriptionLength = 512;

    /// <summary>
    ///     The longest image URL kept; the same as a link URL may have.
    /// </summary>
    public const int MaxImageUrlLength = 2048;

    public string? Title { get; init; }

    public string? Description { get; init; }

    /// <summary>
    ///     An absolute http or https URL, as the page gave it. The API never fetches it.
    /// </summary>
    public string? ImageUrl { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }
}
