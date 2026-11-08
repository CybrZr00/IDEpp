using System.Reflection.PortableExecutable;
using System.Text;

namespace IdePlusPlus.ReverseEngineering;

/// <summary>A native binary's export table: the module's own name and its named exports.</summary>
public sealed record NativeExports(string ModuleName, IReadOnlyList<string> Names);

/// <summary>
/// Reads the named exports from a native PE's export directory, in-process, by walking the PE data
/// directory - no dumpbin, no external tool. Exports with only an ordinal (no name) are not listed,
/// since a forwarding .def forwards by name.
/// </summary>
public static class NativeExportReader
{
    public static NativeExports Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);

        var header = peReader.PEHeaders.PEHeader
            ?? throw new InvalidOperationException($"'{Path.GetFileName(path)}' is not a PE image.");

        var directory = header.ExportTableDirectory;
        if (directory.Size == 0 || directory.RelativeVirtualAddress == 0)
        {
            return new NativeExports(Path.GetFileName(path), []);
        }

        // IMAGE_EXPORT_DIRECTORY: skip to the Name RVA (offset 12), then Base/counts/table RVAs.
        var reader = peReader.GetSectionData(directory.RelativeVirtualAddress).GetReader();
        reader.Offset = 12;
        var nameRva = reader.ReadInt32();
        reader.ReadInt32(); // Base (ordinal base) - unused for named exports
        reader.ReadInt32(); // NumberOfFunctions
        var numberOfNames = reader.ReadInt32();
        reader.ReadInt32(); // AddressOfFunctions
        var addressOfNames = reader.ReadInt32();

        var moduleName = nameRva != 0 ? ReadAsciiZ(peReader, nameRva) : Path.GetFileName(path);

        var names = new List<string>(Math.Max(0, numberOfNames));
        if (numberOfNames > 0 && addressOfNames != 0)
        {
            var namePointers = peReader.GetSectionData(addressOfNames).GetReader();
            for (var i = 0; i < numberOfNames; i++)
            {
                var stringRva = namePointers.ReadInt32();
                if (stringRva != 0)
                {
                    names.Add(ReadAsciiZ(peReader, stringRva));
                }
            }
        }

        names.Sort(StringComparer.Ordinal);
        return new NativeExports(moduleName, names);
    }

    private static string ReadAsciiZ(PEReader peReader, int relativeVirtualAddress)
    {
        var reader = peReader.GetSectionData(relativeVirtualAddress).GetReader();
        var builder = new StringBuilder();
        byte b;
        while ((b = reader.ReadByte()) != 0)
        {
            builder.Append((char)b);
        }

        return builder.ToString();
    }
}
