namespace Pqax;

/// <summary>The type of a field of the object model, as the serialization writes it.</summary>
internal enum FieldKind
{
    /// <summary>The <c>id</c> of a row: a positive integer.</summary>
    Id,

    /// <summary>An integer.</summary>
    Integer,

    /// <summary>A reference: the <c>id</c> of a row of another table.</summary>
    Reference,

    /// <summary>A string.</summary>
    String,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>A timestamp written as <c>2026-10-05T08:15:30.000Z</c>.</summary>
    Timestamp,

    /// <summary>A list of strings.</summary>
    StringList,

    /// <summary>A list of references.</summary>
    ReferenceList,

    /// <summary>A map of string to string.</summary>
    Map,

    /// <summary>A nested object: a distribution, the Delta details, the Iceberg details.</summary>
    Object,
}
