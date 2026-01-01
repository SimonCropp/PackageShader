/// <summary>
/// Output strategy based on modifications required.
/// </summary>
enum ModificationStrategy
{
    /// <summary>
    /// Direct byte patches - no metadata size change.
    /// Only modifying existing rows with indices that already exist in heaps.
    /// </summary>
    InPlacePatch,

    /// <summary>
    /// Metadata needs rebuild but fits in existing section padding.
    /// </summary>
    MetadataRebuildWithPadding,

    /// <summary>
    /// Metadata section must grow, shifting subsequent sections.
    /// </summary>
    FullMetadataSectionRebuild
}