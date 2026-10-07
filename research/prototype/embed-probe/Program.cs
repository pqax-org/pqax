// Writes a package as the VPAX library does, with a PQAX file as one part that asks for no compression.
// Usage: dotnet run -f <net10.0|net472> -- <file.pqax> <output.vpax>
// The output is not a real VPAX: its DaxModel.json is an empty object. It exists to see how the part is stored.
using System;
using System.IO;
using System.IO.Packaging;
using System.Runtime.InteropServices;

internal static class Program
{
    private static void Main(string[] args)
    {
        string source = args[0], output = args[1];
        File.Delete(output);
        using (var package = Package.Open(output, FileMode.Create))
        {
            var model = package.CreatePart(new Uri("/DaxModel.json", UriKind.Relative), "application/json", CompressionOption.Maximum);
            using (var writer = new StreamWriter(model.GetStream()))
                writer.Write("{}");

            var footprint = package.CreatePart(new Uri("/Footprint.pqax", UriKind.Relative), "application/zip", CompressionOption.NotCompressed);
            using (var target = footprint.GetStream())
            using (var input = File.OpenRead(source))
                input.CopyTo(target);
        }
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}: wrote {new FileInfo(output).Length:N0} bytes from a PQAX file of {new FileInfo(source).Length:N0} bytes");
    }
}
