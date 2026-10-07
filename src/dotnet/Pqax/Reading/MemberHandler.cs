using System.Text.Json;

namespace Pqax;

/// <summary>Handles one member of the top-level object of a part. It must leave the reader on the last token of the value.</summary>
/// <param name="name">The name of the member.</param>
/// <param name="reader">The reader, on the first token of the value.</param>
internal delegate void MemberHandler(string name, ref Utf8JsonReader reader);
