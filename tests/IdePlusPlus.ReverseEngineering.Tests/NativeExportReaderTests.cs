using Shouldly;
using Xunit;

namespace IdePlusPlus.ReverseEngineering.Tests;

public sealed class NativeExportReaderTests
{
    [Fact]
    public void Reads_named_exports_from_a_system_dll()
    {
        var exports = NativeExportReader.Read(@"C:\Windows\System32\kernel32.dll");

        exports.ModuleName.ShouldContain("kernel32", Case.Insensitive);
        exports.Names.ShouldContain("CreateFileW");
        exports.Names.ShouldContain("GetProcAddress");
    }

    [Fact]
    public void A_binary_without_exports_yields_an_empty_list()
    {
        // find.exe is an application with no export table.
        var exports = NativeExportReader.Read(@"C:\Windows\System32\find.exe");

        exports.Names.ShouldBeEmpty();
    }
}
