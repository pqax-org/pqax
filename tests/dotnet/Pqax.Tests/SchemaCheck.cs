using System.Text.Json.Nodes;
using Json.Schema;

namespace Pqax.Tests;

/// <summary>
/// Checks the documents of a PQAX file against the JSON Schema of the kit: manifest.json against
/// schema/manifest.schema.json, summary.json against #/$defs/summaryPart, and every other part against
/// #/$defs/detailPart (conformance/README.md, "The schema").
/// </summary>
public static class SchemaCheck
{
    private sealed record Schemas(JsonSchema Manifest, JsonSchema Summary, JsonSchema Detail, EvaluationOptions Options);

    private static readonly Lazy<Schemas> Loaded = new(Load);

    private static Schemas Load()
    {
        string folder = Path.Combine(Kit.Conformance, "schema");
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        JsonSchema manifest = JsonSchema.FromFile(Path.Combine(folder, "manifest.schema.json"));
        JsonSchema part = JsonSchema.FromFile(Path.Combine(folder, "part.schema.json"));
        options.SchemaRegistry.Register(manifest);
        options.SchemaRegistry.Register(part);
        JsonSchema summary = new JsonSchemaBuilder().Ref(new Uri(part.BaseUri, "#/$defs/summaryPart")).Build();
        JsonSchema detail = new JsonSchemaBuilder().Ref(new Uri(part.BaseUri, "#/$defs/detailPart")).Build();
        return new Schemas(manifest, summary, detail, options);
    }

    /// <summary>Every error of the schema in the manifest and in the parts that it lists.</summary>
    public static List<string> Errors(byte[] pqax)
    {
        Schemas schemas = Loaded.Value;
        var errors = new List<string>();
        Dictionary<string, byte[]> entries = Kit.Entries(pqax).ToDictionary(entry => entry.Name, entry => entry.Content);
        JsonNode manifest = JsonNode.Parse(entries["manifest.json"])!;
        Collect(errors, "manifest.json", schemas.Manifest.Evaluate(manifest, schemas.Options));
        foreach (JsonNode? part in manifest["parts"]!.AsArray())
        {
            string name = (string)part!["name"]!;
            JsonSchema schema = name == "summary.json" ? schemas.Summary : schemas.Detail;
            Collect(errors, name, schema.Evaluate(JsonNode.Parse(entries[name]), schemas.Options));
        }

        return errors;
    }

    private static void Collect(List<string> errors, string document, EvaluationResults results)
    {
        if (results.IsValid)
        {
            return;
        }

        int before = errors.Count;
        foreach (EvaluationResults detail in results.Details.Where(detail => detail.HasErrors && detail.Errors != null))
        {
            foreach (KeyValuePair<string, string> error in detail.Errors!)
            {
                errors.Add($"{document} {detail.InstanceLocation}: {error.Key} {error.Value}");
            }
        }

        if (errors.Count == before)
        {
            errors.Add($"{document}: does not match the schema");
        }
    }
}
