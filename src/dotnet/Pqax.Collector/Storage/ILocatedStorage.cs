using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>
/// A storage that knows where its paths are outside the collector, so that <see cref="TableDiscovery"/> can record the
/// location of a source and of its tables. <see cref="LocalStorage"/> and <see cref="OneLakeStorage"/> implement it; a
/// storage that does not, such as one that wraps another, gives tables and sources with no location.
/// </summary>
internal interface ILocatedStorage
{
    /// <summary>The location to record for a folder of the storage: an item folder or a table folder.</summary>
    /// <param name="path">The folder, relative to the root of the storage, with forward slashes.</param>
    /// <returns>An absolute location, such as an ABFSS URI or a full path on disk.</returns>
    string LocationOf(string path);

    /// <summary>The identifiers that the platform assigns to an item, when the caller named it by id.</summary>
    /// <param name="item">The item folder or id, as the caller gave it to the discovery.</param>
    /// <returns>The identifiers, such as <c>workspaceId</c> and <c>itemId</c>. Null when there are none.</returns>
    IReadOnlyDictionary<string, string>? IdentifiersOf(string item);
}
