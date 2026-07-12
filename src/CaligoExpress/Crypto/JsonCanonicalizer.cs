namespace CaligoExpress.Crypto;

using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Produces a deterministic byte representation of a JSON document so that a signature is stable
/// regardless of whitespace or property ordering introduced by intermediate tooling/CDNs.
/// Object keys are sorted ordinally and output is compact. We never emit floating-point numbers,
/// so number raw text is preserved verbatim, which keeps the form fully deterministic.
/// </summary>
public static class JsonCanonicalizer
{
    public static byte[] Canonicalize(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        // Minimal escaping (only ", \ and control chars) so the output matches a plain JSON.stringify
        // in other languages — the default STJ encoder escapes '+', '<', '&', etc. as \uXXXX, which
        // would diverge from the TypeScript canonicalizer and break cross-language signature checks.
        var options = new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var writer = new Utf8JsonWriter(buffer, options))
            Write(writer, node);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Canonical bytes of <paramref name="json"/> with the top-level signature property removed,
    /// i.e. exactly what gets signed and verified.
    /// </summary>
    public static byte[] CanonicalizeForSigning(string json, string signatureProperty = "signature")
    {
        var node = JsonNode.Parse(json) ?? throw new ArgumentException("invalid json", nameof(json));
        if (node is JsonObject obj)
            obj.Remove(signatureProperty);
        return Canonicalize(node);
    }

    private static void Write(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case null:
                w.WriteNullValue();
                break;
            case JsonObject obj:
                w.WriteStartObject();
                foreach (var kv in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(kv.Key);
                    Write(w, kv.Value);
                }
                w.WriteEndObject();
                break;
            case JsonArray arr:
                w.WriteStartArray();
                foreach (var item in arr)
                    Write(w, item);
                w.WriteEndArray();
                break;
            case JsonValue val:
                WriteValue(w, val);
                break;
        }
    }

    private static void WriteValue(Utf8JsonWriter w, JsonValue val)
    {
        if (val.TryGetValue<JsonElement>(out var el))
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    w.WriteStringValue(el.GetString());
                    break;
                case JsonValueKind.Number:
                    w.WriteRawValue(el.GetRawText()); // preserve exact integer text
                    break;
                case JsonValueKind.True:
                    w.WriteBooleanValue(true);
                    break;
                case JsonValueKind.False:
                    w.WriteBooleanValue(false);
                    break;
                case JsonValueKind.Null:
                    w.WriteNullValue();
                    break;
                default:
                    w.WriteRawValue(el.GetRawText());
                    break;
            }
        }
        else
        {
            val.WriteTo(w);
        }
    }
}
