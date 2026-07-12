namespace CaligoExpress.Manifests;

using System.Text.Json.Serialization;

/// <summary>One content-defined chunk (the unit of storage, dedup and download). <see cref="Hash"/> is
/// the SHA-256 of the chunk's plaintext and doubles as its CDN key (<see cref="CaligoExpress.Publishing.CdnKeys.Chunk"/>).
/// A big file's chunks are slices of just that file; a "pack" chunk concatenates many small files, so one
/// object covers them all (fewer billed S3 ops). Shared chunks dedup across files and versions.</summary>
public sealed record ChunkEntry(
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("size")] int Size);

/// <summary>A slice of a chunk: bytes <c>[Offset, Offset+Length)</c> of <see cref="Chunk"/>'s plaintext.
/// A whole-chunk region (Offset 0, Length == chunk size) is a big file's own chunk; a partial region is a
/// small file living inside a shared pack chunk.</summary>
public sealed record RegionEntry(
    [property: JsonPropertyName("chunk")] string Chunk,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("length")] int Length);

/// <summary>One installed file. <see cref="Path"/> is relative to the package's target root, using forward
/// slashes. <see cref="Hash"/> is the SHA-256 of the whole (uncompressed) file content; <see cref="Regions"/>
/// is the ordered list of chunk slices whose concatenation reproduces the file (empty only for a zero-byte
/// file). The client reassembles each file from chunk bytes it already has on disk plus the ones it downloads.</summary>
public sealed record FileEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("regions")] List<RegionEntry> Regions,
    [property: JsonPropertyName("exec")] bool Exec = false);

/// <summary>An idempotent post-apply action. Runs inside the transaction so failures roll back.</summary>
public sealed record HookDef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("query")] string? Query = null,
    [property: JsonPropertyName("origin")] string? Origin = null,
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("over")] bool Override = false);

/// <summary>
/// The full file listing for one package version. Used to compute file-level diffs and to
/// verify installed state. Its bytes are hash-covered by the release manifest (PackageRef.Manifest.Hash).
/// </summary>
public sealed record PackageManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("targetRoot")] string TargetRoot,
    [property: JsonPropertyName("chunks")] List<ChunkEntry> Chunks,
    [property: JsonPropertyName("files")] List<FileEntry> Files,
    [property: JsonPropertyName("hooks")] List<HookDef> Hooks);
