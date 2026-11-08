using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

namespace IdePlusPlus.ReverseEngineering;

/// <summary>A .NET assembly's name, version, type count and references.</summary>
public sealed record ManagedAssemblyOverview(
    string Name, string Version, int TypeCount, IReadOnlyList<string> References);

/// <summary>
/// Decompiles a .NET assembly in-process with ICSharpCode.Decompiler. It reads the assembly's
/// metadata and IL only - the target is never loaded into the process or run - so it is safe to point
/// at an untrusted DLL. This is the path that recovered the DigitalJeeves/QuestPDF code.
/// </summary>
public sealed class ManagedDecompiler
{
    public const int DefaultMaxTypes = 500;

    /// <summary>Assembly name, version, type count and referenced assemblies, read straight from the
    /// PE metadata without decompiling anything.</summary>
    public ManagedAssemblyOverview Overview(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);

        bool hasMetadata;
        try
        {
            hasMetadata = peReader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            // A file that isn't a PE image at all - reading the headers fails.
            hasMetadata = false;
        }

        if (!hasMetadata)
        {
            throw new InvalidOperationException($"'{Path.GetFileName(assemblyPath)}' is not a managed .NET assembly.");
        }

        var metadata = peReader.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        var name = assembly.GetAssemblyName();

        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetAssemblyReference(handle).GetAssemblyName().Name ?? "")
            .Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // TypeDefinitions includes the synthetic <Module> row; subtract it for a real type count.
        var typeCount = Math.Max(0, metadata.TypeDefinitions.Count - 1);

        return new ManagedAssemblyOverview(
            name.Name ?? Path.GetFileNameWithoutExtension(assemblyPath),
            name.Version?.ToString() ?? "",
            typeCount,
            references);
    }

    /// <summary>Full names of the assembly's types, optionally filtered by a case-insensitive substring.</summary>
    public IReadOnlyList<string> ListTypes(string assemblyPath, string? filter = null, int max = DefaultMaxTypes)
    {
        var decompiler = CreateDecompiler(assemblyPath);
        IEnumerable<string> names = decompiler.TypeSystem.MainModule.TypeDefinitions
            .Select(type => type.FullName)
            .Where(fullName => !string.IsNullOrEmpty(fullName));

        if (!string.IsNullOrWhiteSpace(filter))
        {
            names = names.Where(fullName => fullName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return names.OrderBy(fullName => fullName, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    /// <summary>Decompiles one type to C#. The name is matched exactly first, then loosely (by suffix
    /// or simple name), so the model can pass "Foo" or "Namespace.Foo".</summary>
    public string DecompileType(string assemblyPath, string typeName)
    {
        var decompiler = CreateDecompiler(assemblyPath);
        var definitions = decompiler.TypeSystem.MainModule.TypeDefinitions.ToList();

        var match =
            definitions.FirstOrDefault(type => type.FullName == typeName || type.ReflectionName == typeName)
            ?? definitions.FirstOrDefault(type =>
                type.FullName.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                || type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
            ?? definitions.FirstOrDefault(type => type.FullName.EndsWith("." + typeName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new ArgumentException($"Type '{typeName}' was not found in '{Path.GetFileName(assemblyPath)}'.");
        }

        return decompiler.DecompileTypeAsString(new FullTypeName(match.ReflectionName));
    }

    private static CSharpDecompiler CreateDecompiler(string assemblyPath)
    {
        // Resolve errors are tolerated: we only need the target's own metadata, and a missing
        // dependency should degrade a reference rather than throw.
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        return new CSharpDecompiler(assemblyPath, settings);
    }
}
