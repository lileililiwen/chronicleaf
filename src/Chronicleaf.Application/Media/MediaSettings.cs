using Chronicleaf.Core;
using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Application.Media;

/// <summary>
/// Where the images live, and how large one may be (LOCAL-MEDIA-STORAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MediaRootPath"/> is the single root every image is stored under, read from
/// <c>Chronicleaf:MediaRoot</c> and defaulted to <c>runtime/media</c>. It is resolved once, to an
/// absolute path, before the first request is served - which is what makes it a root rather than a
/// per-request parameter: nothing downstream accepts a path, a key or a filename from a caller, so
/// there is no code path through which a request can choose where bytes are read from or written to.
/// </para>
/// <para>
/// The default sits beside the application, outside the published <c>wwwroot</c>, because the static
/// file middleware serves that directory. A root inside it would publish every stored image as a
/// static asset to an unauthenticated caller, which is the one outcome this package exists to
/// prevent. Registration refuses such a root instead of quietly serving it, and the resolution
/// rejects it again at run time, so a misconfiguration is a failed start rather than a leak.
/// </para>
/// <para>
/// The two size values default to the design's bounds and are configurable so a deployment can tighten
/// them. They are only ever lowered in effect by a caller: whatever an operator configures, the
/// stored type, the decoded-type check and the key shape are not configurable at all.
/// </para>
/// </remarks>
public sealed class MediaSettings
{
    /// <summary>
    /// The configuration section, and the environment-variable prefix
    /// (<c>Chronicleaf__MediaRoot</c>) an operator sets.
    /// </summary>
    public const string SectionName = "Chronicleaf";

    /// <summary>The configuration key the MediaRoot is read from.</summary>
    public const string MediaRootSettingName = "Chronicleaf:MediaRoot";

    /// <summary>The configuration key the byte limit is read from.</summary>
    public const string MaximumByteLengthSettingName = "Chronicleaf:Media:MaximumByteLength";

    /// <summary>The configuration key the pixel limit is read from.</summary>
    public const string MaximumPixelCountSettingName = "Chronicleaf:Media:MaximumPixelCount";

    /// <summary>
    /// The root used when nothing is configured, relative to the application's content root and
    /// therefore outside <c>wwwroot</c>.
    /// </summary>
    public const string DefaultMediaRoot = "runtime/media";

    /// <summary>The directory name <c>wwwroot</c> has in a published application.</summary>
    public const string WebRootDirectoryName = "wwwroot";

    /// <summary>The configured root, exactly as it was configured. May be relative.</summary>
    public string MediaRootPath { get; init; } = DefaultMediaRoot;

    /// <summary>How many bytes one image may be. Never above <see cref="MediaLimits.MaximumByteLength"/>.</summary>
    public long MaximumByteLength { get; init; } = MediaLimits.MaximumByteLength;

    /// <summary>How many pixels a decoded image may have. Never above <see cref="MediaLimits.MaximumPixelCount"/>.</summary>
    public long MaximumPixelCount { get; init; } = MediaLimits.MaximumPixelCount;

    /// <summary>
    /// Reads the settings, clamping both limits to the design's bounds.
    /// </summary>
    /// <remarks>
    /// A configured value larger than the design's bound is clamped rather than refused: the
    /// constants in <see cref="MediaLimits"/> are the package's promise about what it will accept,
    /// and a configuration file cannot widen it. A value of zero or less falls back to the bound,
    /// because a store that can accept nothing is never what an operator meant.
    /// </remarks>
    public static MediaSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredRoot = configuration[MediaRootSettingName];

        return new MediaSettings
        {
            MediaRootPath = string.IsNullOrWhiteSpace(configuredRoot) ? DefaultMediaRoot : configuredRoot,
            MaximumByteLength = Clamp(
                ReadPositiveLong(configuration[MaximumByteLengthSettingName]),
                MediaLimits.MaximumByteLength),
            MaximumPixelCount = Clamp(
                ReadPositiveLong(configuration[MaximumPixelCountSettingName]),
                MediaLimits.MaximumPixelCount),
        };
    }

    private static long Clamp(long? configured, long bound) =>
        configured is > 0 and var value ? Math.Min(value, bound) : bound;

    /// <summary>
    /// A configured positive whole number, or <c>null</c> for anything else. A value that is not a
    /// number is treated as absent rather than as an error, so a mistyped limit falls back to the
    /// design's bound instead of failing a start-up over a number in a configuration file.
    /// </summary>
    private static long? ReadPositiveLong(string? configured) =>
        long.TryParse(configured, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
}
