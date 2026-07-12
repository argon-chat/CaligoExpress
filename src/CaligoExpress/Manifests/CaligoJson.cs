namespace CaligoExpress.Manifests;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Source-generated (AOT-friendly) serialization context for every manifest root type.
/// Null fields are omitted so unsigned manifests don't emit a "signature": null, which keeps
/// the canonical signing payload clean.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ConfigManifest))]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(BootstrapperManifest))]
[JsonSerializable(typeof(PackageManifest))]
[JsonSerializable(typeof(LocalInstallState))]
[JsonSerializable(typeof(CaligoProjectConfig))]
[JsonSerializable(typeof(InstallerRuntimeConfig))]
public partial class CaligoJsonContext : JsonSerializerContext;

/// <summary>Convenience wrappers over <see cref="CaligoJsonContext"/> for compact and pretty output.</summary>
public static class CaligoJson
{
    private static readonly CaligoJsonContext Pretty =
        new(new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, typeof(T), CaligoJsonContext.Default);

    public static string SerializePretty<T>(T value) =>
        JsonSerializer.Serialize(value, typeof(T), Pretty);

    public static T Deserialize<T>(string json) =>
        (T)JsonSerializer.Deserialize(json, typeof(T), CaligoJsonContext.Default)!;
}
