using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>A part that the builder writes: its entry name, the slices of tables it holds, and the members of its descriptor that a producer added.</summary>
internal sealed class PartPlan
{
    public PartPlan(string name, IReadOnlyDictionary<string, JsonElement>? extensions = null)
    {
        Name = name;
        Extensions = extensions;
    }

    public string Name { get; }

    public List<PartSlice> Slices { get; } = new List<PartSlice>();

    /// <summary>The members of the descriptor other than <c>name</c> and <c>tables</c>, from the part of the document that <c>From</c> copied; null for a new part.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; }
}
