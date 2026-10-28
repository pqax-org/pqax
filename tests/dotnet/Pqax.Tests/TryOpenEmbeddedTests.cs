using System.Reflection;
using System.Text;

namespace Pqax.Tests;

/// <summary>
/// <see cref="PqaxDocument.TryOpenEmbedded(string, out PqaxDocument?)"/> and its overloads: true and the footprint that
/// a package holds, false and null exactly when the package has no part of that name, with the file or the stream
/// closed, and every other failure thrown as <c>OpenEmbedded</c> throws it (the contract of the hardening before 1.0,
/// section 3).
/// </summary>
public class TryOpenEmbeddedTests
{
    public static TheoryData<string, string> Packages => new()
    {
        { "embedded-stored.vpax", "stored" },
        { "embedded-deflated.vpax", "deflated" },
    };

    private static string CodeOf(Action action) => Assert.Throws<PqaxFormatException>(action).Code;

    /// <summary>The document has the tables and the shape of sample.pqax.</summary>
    private static void AssertSample(PqaxDocument? doc, string compression)
    {
        Assert.NotNull(doc);
        using PqaxDocument sample = PqaxDocument.Open(Path.Combine(Kit.Samples, "sample.pqax"));
        Assert.Equal(Kit.EmbeddedPart, doc.EmbeddedPart);
        Assert.Equal(compression, doc.EmbeddedCompression);
        Assert.Equal(sample.Manifest.Collection, doc.Manifest.Collection);
        Assert.Equal(sample.Summary.Tables.Rows, doc.Summary.Tables.Rows);
        Assert.Equal(sample.Summary.Columns.Rows, doc.Summary.Columns.Rows);
        Assert.Equal(sample.ReadPartitions().Rows, doc.ReadPartitions().Rows);
        Assert.Equal(sample.ReadFiles().Rows, doc.ReadFiles().Rows);
        Assert.Equal(sample.ReadRowGroups().Rows, doc.ReadRowGroups().Rows);
        Assert.Equal(sample.ReadColumnChunks().Rows, doc.ReadColumnChunks().Rows);
        Assert.Equal(sample.ReadCommits().Rows, doc.ReadCommits().Rows);
        Assert.Empty(Kit.Differences(Kit.Shape(doc), Kit.Expected("sample"), Kit.ShapeKeys));
    }

    /// <summary>A VPAX-like package: the entries of embedded-stored.vpax, with these bytes as Footprint.pqax.</summary>
    private static byte[] PackageWith(byte[] footprint) =>
        Kit.Rewrite(Kit.SampleBytes("embedded-stored.vpax"), (name, content) => name == Kit.EmbeddedPart ? footprint : content);

    /// <summary>No handle is left on the file: it can be opened for exclusive access at once.</summary>
    private static void AssertClosed(string path)
    {
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
    }

    // ---- the signature ----

    [Fact]
    public void The_four_overloads_have_a_document_that_is_not_null_when_they_return_true()
    {
        MethodInfo[] methods = typeof(PqaxDocument).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(PqaxDocument.TryOpenEmbedded)).ToArray();
        Assert.Equal(
            ["Stream,PqaxDocument&", "Stream,String,Boolean,PqaxDocument&", "String,PqaxDocument&", "String,String,PqaxDocument&"],
            methods.Select(method => string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name))).Order(StringComparer.Ordinal));
        var context = new NullabilityInfoContext();
        foreach (MethodInfo method in methods)
        {
            Assert.Equal(typeof(bool), method.ReturnType);
            ParameterInfo document = method.GetParameters()[^1];
            Assert.True(document.IsOut, method.ToString());
            Assert.Equal(NullabilityState.Nullable, context.Create(document).ReadState);
            CustomAttributeData? notNullWhen = document.GetCustomAttributesData()
                .SingleOrDefault(attribute => attribute.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.NotNullWhenAttribute");
            Assert.NotNull(notNullWhen);
            Assert.True((bool)notNullWhen.ConstructorArguments.Single().Value!, method.ToString());
        }
    }

    // ---- a package with the footprint ----

    [Theory]
    [MemberData(nameof(Packages))]
    public void A_package_with_the_footprint_gives_true_and_the_document_of_sample(string file, string compression)
    {
        string path = Path.Combine(Kit.Samples, file);
        Assert.True(PqaxDocument.TryOpenEmbedded(path, out PqaxDocument? byPath));
        using (byPath)
        {
            AssertSample(byPath, compression);
        }

        Assert.True(PqaxDocument.TryOpenEmbedded(path, Kit.EmbeddedPart, out PqaxDocument? byPathAndName));
        using (byPathAndName)
        {
            AssertSample(byPathAndName, compression);
        }

        using var stream = new MemoryStream(Kit.SampleBytes(file));
        Assert.True(PqaxDocument.TryOpenEmbedded(stream, out PqaxDocument? byStream));
        using (byStream)
        {
            AssertSample(byStream, compression);
        }

        using var other = new MemoryStream(Kit.SampleBytes(file));
        Assert.True(PqaxDocument.TryOpenEmbedded(other, Kit.EmbeddedPart, false, out PqaxDocument? byStreamAndName));
        using (byStreamAndName)
        {
            AssertSample(byStreamAndName, compression);
        }
    }

    [Theory]
    [InlineData("/Footprint.pqax")]
    [InlineData("footprint.PQAX")]
    [InlineData("/FOOTPRINT.pqax")]
    public void The_part_is_found_as_OpenEmbedded_finds_it(string partName)
    {
        foreach (string file in new[] { "embedded-stored.vpax", "embedded-deflated.vpax" })
        {
            string path = Path.Combine(Kit.Samples, file);
            Assert.True(PqaxDocument.TryOpenEmbedded(path, partName, out PqaxDocument? doc));
            using (doc)
            {
                using PqaxDocument expected = PqaxDocument.OpenEmbedded(path, partName);
                Assert.Equal(expected.EmbeddedPart, doc!.EmbeddedPart);
                Assert.Equal(Kit.EmbeddedPart, doc.EmbeddedPart);
                Assert.Equal(52, doc.ReadColumnChunks().Count);
            }

            using var stream = new MemoryStream(Kit.SampleBytes(file));
            Assert.True(PqaxDocument.TryOpenEmbedded(stream, partName, false, out PqaxDocument? fromStream));
            using (fromStream)
            {
                Assert.Equal(Kit.EmbeddedPart, fromStream!.EmbeddedPart);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Packages))]
    public void A_stream_is_closed_with_the_document_unless_leaveOpen(string file, string compression)
    {
        var kept = new CountingStream(Kit.SampleBytes(file));
        Assert.True(PqaxDocument.TryOpenEmbedded(kept, Kit.EmbeddedPart, true, out PqaxDocument? doc));
        Assert.Equal(compression, doc!.EmbeddedCompression);
        doc.Dispose();
        Assert.False(kept.Disposed);

        var closed = new CountingStream(Kit.SampleBytes(file));
        Assert.True(PqaxDocument.TryOpenEmbedded(closed, out PqaxDocument? owner));
        owner!.Dispose();
        Assert.True(closed.Disposed);
    }

    // ---- a package without the part ----

    [Fact]
    public void A_package_without_the_part_gives_false_and_null()
    {
        using TemporaryFile file = Kit.Temporary(Kit.PackageWithoutFootprint(), ".vpax");
        Assert.False(PqaxDocument.TryOpenEmbedded(file.Path, out PqaxDocument? byPath));
        Assert.Null(byPath);
        Assert.False(PqaxDocument.TryOpenEmbedded(file.Path, Kit.EmbeddedPart, out PqaxDocument? byPathAndName));
        Assert.Null(byPathAndName);
        Assert.False(PqaxDocument.TryOpenEmbedded(new MemoryStream(Kit.PackageWithoutFootprint()), out PqaxDocument? byStream));
        Assert.Null(byStream);
        Assert.False(PqaxDocument.TryOpenEmbedded(new MemoryStream(Kit.PackageWithoutFootprint()), Kit.EmbeddedPart, true, out PqaxDocument? byStreamAndName));
        Assert.Null(byStreamAndName);

        string stored = Path.Combine(Kit.Samples, "embedded-stored.vpax");
        Assert.False(PqaxDocument.TryOpenEmbedded(stored, "Nothing.pqax", out PqaxDocument? nothing));
        Assert.Null(nothing);
        Assert.False(PqaxDocument.TryOpenEmbedded(new MemoryStream(Kit.SampleBytes("embedded-deflated.vpax")), "Nothing.pqax", false, out nothing));
        Assert.Null(nothing);
    }

    [Fact]
    public void False_leaves_the_file_closed()
    {
        using TemporaryFile file = Kit.Temporary(Kit.PackageWithoutFootprint(), ".vpax");
        Assert.False(PqaxDocument.TryOpenEmbedded(file.Path, out _));
        AssertClosed(file.Path);
        Assert.False(PqaxDocument.TryOpenEmbedded(file.Path, "Nothing.pqax", out _));
        AssertClosed(file.Path);
    }

    [Fact]
    public void False_closes_the_stream_unless_leaveOpen()
    {
        var byDefault = new CountingStream(Kit.PackageWithoutFootprint());
        Assert.False(PqaxDocument.TryOpenEmbedded(byDefault, out _));
        Assert.True(byDefault.Disposed);

        var closed = new CountingStream(Kit.PackageWithoutFootprint());
        Assert.False(PqaxDocument.TryOpenEmbedded(closed, Kit.EmbeddedPart, false, out _));
        Assert.True(closed.Disposed);

        var kept = new CountingStream(Kit.PackageWithoutFootprint());
        Assert.False(PqaxDocument.TryOpenEmbedded(kept, Kit.EmbeddedPart, true, out _));
        Assert.False(kept.Disposed);
    }

    [Fact]
    public void OpenEmbedded_still_throws_embedded_partMissing()
    {
        using TemporaryFile file = Kit.Temporary(Kit.PackageWithoutFootprint(), ".vpax");
        Assert.Equal("embedded.partMissing", CodeOf(() => PqaxDocument.OpenEmbedded(file.Path).Dispose()));
        Assert.Equal("embedded.partMissing", CodeOf(() => PqaxDocument.OpenEmbedded(new MemoryStream(Kit.PackageWithoutFootprint())).Dispose()));
        Assert.Equal("embedded.partMissing",
            CodeOf(() => PqaxDocument.OpenEmbedded(Path.Combine(Kit.Samples, "embedded-stored.vpax"), "Nothing.pqax").Dispose()));
    }

    // ---- every other failure throws ----

    [Fact]
    public void A_package_that_is_not_a_zip_archive_throws_container_manifestMissing()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("not a zip archive");
        using TemporaryFile file = Kit.Temporary(bytes, ".vpax");
        Assert.Equal("container.manifestMissing", CodeOf(() => PqaxDocument.TryOpenEmbedded(file.Path, out _)));
        AssertClosed(file.Path);
        Assert.Equal("container.manifestMissing", CodeOf(() => PqaxDocument.TryOpenEmbedded(file.Path, Kit.EmbeddedPart, out _)));
        Assert.Equal("container.manifestMissing", CodeOf(() => PqaxDocument.TryOpenEmbedded(new MemoryStream(bytes), out _)));
        Assert.Equal("container.manifestMissing", CodeOf(() => PqaxDocument.TryOpenEmbedded(new MemoryStream(bytes), Kit.EmbeddedPart, true, out _)));
        Assert.Equal("container.manifestMissing", CodeOf(() => PqaxDocument.OpenEmbedded(new MemoryStream(bytes)).Dispose()));
    }

    public static TheoryData<string?, string> Footprints => new()
    {
        { null, "container.manifestMissing" },
        { "container.manifestMissing.pqax", "container.manifestMissing" },
        { "manifest.format.pqax", "manifest.format" },
        { "manifest.versionUnknown.pqax", "manifest.versionUnknown" },
    };

    [Theory]
    [MemberData(nameof(Footprints))]
    public void A_footprint_that_is_not_a_pqax_file_throws_its_code(string? invalid, string code)
    {
        byte[] package = PackageWith(invalid == null ? Encoding.UTF8.GetBytes("not a zip archive") : Kit.InvalidBytes(invalid));
        using TemporaryFile file = Kit.Temporary(package, ".vpax");
        Assert.Equal(code, CodeOf(() => PqaxDocument.OpenEmbedded(file.Path).Dispose()));
        Assert.Equal(code, CodeOf(() => PqaxDocument.TryOpenEmbedded(file.Path, out _)));
        AssertClosed(file.Path);
        Assert.Equal(code, CodeOf(() => PqaxDocument.TryOpenEmbedded(file.Path, Kit.EmbeddedPart, out _)));
        Assert.Equal(code, CodeOf(() => PqaxDocument.TryOpenEmbedded(new MemoryStream(package), out _)));
        Assert.Equal(code, CodeOf(() => PqaxDocument.TryOpenEmbedded(new MemoryStream(package), Kit.EmbeddedPart, true, out _)));
    }

    [Fact]
    public void Arguments_and_files_that_OpenEmbedded_refuses_are_refused_alike()
    {
        string stored = Path.Combine(Kit.Samples, "embedded-stored.vpax");
        Assert.Throws<ArgumentNullException>(() => PqaxDocument.TryOpenEmbedded(stored, null!, out _));
        Assert.Throws<ArgumentNullException>(() => PqaxDocument.TryOpenEmbedded((string)null!, out _));
        Assert.Throws<ArgumentNullException>(() => PqaxDocument.TryOpenEmbedded((Stream)null!, out _));
        Assert.Throws<ArgumentNullException>(() => PqaxDocument.TryOpenEmbedded(new MemoryStream(Kit.SampleBytes("embedded-stored.vpax")), null!, false, out _));

        string missing = Path.Combine(Path.GetTempPath(), "pqax-tests-" + Guid.NewGuid().ToString("N") + ".vpax");
        Type expected = Assert.ThrowsAny<IOException>(() => PqaxDocument.OpenEmbedded(missing).Dispose()).GetType();
        Assert.Equal(expected, Assert.ThrowsAny<IOException>(() => PqaxDocument.TryOpenEmbedded(missing, out _)).GetType());
        Assert.Equal(expected, Assert.ThrowsAny<IOException>(() => PqaxDocument.TryOpenEmbedded(missing, Kit.EmbeddedPart, out _)).GetType());
    }
}
