using System.Collections.Generic;

namespace Pqax;

/// <summary>A part that the builder writes: its entry name and the slices of tables it holds.</summary>
internal sealed class PartPlan
{
    public PartPlan(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public List<PartSlice> Slices { get; } = new List<PartSlice>();
}
