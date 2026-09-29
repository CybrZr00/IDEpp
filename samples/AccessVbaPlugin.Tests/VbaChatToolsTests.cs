using System.Text.Json;
using System.Text.Json.Nodes;
using IdePlusPlus.PluginContracts;
using Shouldly;
using Xunit;

namespace AccessVbaPlugin.Tests;

/// <summary>
/// Exercises the plugin's chat tools against fakes of the two AccessTools calls and of the tool
/// window's state, so every path here runs without Access installed. The JSON the fakes return is
/// shaped exactly like AccessTools.ListVbeModulesAsync / GetVbeModuleCodeAsync produce it.
/// </summary>
public sealed class VbaChatToolsTests
{
    private static string ModuleListJson(params (string Name, int Type, int Lines)[] modules) =>
        JsonSerializer.Serialize(new
        {
            databasePath = @"C:\db\front.mdb",
            projectName = "FAM",
            modules = modules.Select(m => new { name = m.Name, type = m.Type, typeName = "ignored", lineCount = m.Lines }).ToArray(),
        });

    private static string ModuleCodeJson(string name, string code) =>
        JsonSerializer.Serialize(new { databasePath = @"C:\db\front.mdb", moduleName = name, projectName = "FAM", code });

    private static VbaChatTools Create(
        CurrentModuleSnapshot? snapshot = null,
        string? listJson = null,
        Func<string, string>? codeJson = null,
        Exception? accessFailure = null,
        TimeSpan? timeout = null)
    {
        return new VbaChatTools(
            getCurrentModule: _ => Task.FromResult(snapshot),
            listModulesJson: _ => accessFailure is null
                ? Task.FromResult(listJson ?? ModuleListJson())
                : Task.FromException<string>(accessFailure),
            getModuleJson: (name, _) => accessFailure is null
                ? Task.FromResult(codeJson is null ? ModuleCodeJson(name, "Sub A()\r\nEnd Sub") : codeJson(name))
                : Task.FromException<string>(accessFailure),
            accessTimeout: timeout);
    }

    private static Func<JsonNode?, CancellationToken, Task<PluginToolResult>> Tool(VbaChatTools tools, string name) =>
        tools.Tools.Single(t => t.Definition.Name == name).Execute;

    private static JsonObject Args(string property, string value) => new() { [property] = value };

    [Fact]
    public void OffersThreeTools_WithObjectSchemas_AndNameRequiredOnlyForGetModule()
    {
        var tools = Create().Tools;

        tools.Select(t => t.Definition.Name).ShouldBe(["vba_get_current_module", "vba_list_modules", "vba_get_module"]);
        tools.ShouldAllBe(t => t.Definition.ParametersSchema["type"]!.GetValue<string>() == "object");
        tools.ShouldAllBe(t => t.Definition.Description.Length > 40);

        var getModule = tools.Single(t => t.Definition.Name == "vba_get_module").Definition.ParametersSchema;
        getModule["required"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["name"]);
        getModule["properties"]!["name"]!["type"]!.GetValue<string>().ShouldBe("string");

        var listModules = tools.Single(t => t.Definition.Name == "vba_list_modules").Definition.ParametersSchema;
        listModules["properties"]!["nameContains"].ShouldNotBeNull();
        listModules["required"].ShouldBeNull();
    }

    [Fact]
    public async Task GetCurrentModule_NothingPulledYet_SaysSoWithoutAnError()
    {
        var result = await Tool(Create(snapshot: null), "vba_get_current_module")(null, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Text.ShouldContain("No module is open");
        result.Text.ShouldContain("vba_list_modules");
    }

    [Fact]
    public async Task GetCurrentModule_ReportsNameKindStateAndCode()
    {
        var snapshot = new CurrentModuleSnapshot("modOrders", "Standard", "Option Explicit\r\nSub Go()\r\nEnd Sub", HasUnpushedChanges: true, @"C:\db\front.mdb");

        var result = await Tool(Create(snapshot), "vba_get_current_module")(null, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Text.ShouldContain("Module: modOrders (Standard)");
        result.Text.ShouldContain(@"Database: C:\db\front.mdb");
        result.Text.ShouldContain("NOT yet pushed");
        result.Text.ShouldContain("Code (3 lines):");
        result.Text.ShouldEndWith("End Sub");
    }

    [Fact]
    public async Task GetCurrentModule_WithoutEdits_SaysItMatchesAccess()
    {
        var snapshot = new CurrentModuleSnapshot("clsStock", "Class", "'nothing", HasUnpushedChanges: false, null);

        var result = await Tool(Create(snapshot), "vba_get_current_module")(null, CancellationToken.None);

        result.Text.ShouldContain("matches what was last pulled");
        result.Text.ShouldNotContain("Database:");
    }

    [Fact]
    public async Task GetCurrentModule_TruncatesAHugeModuleAndSaysSo()
    {
        var code = string.Join("\r\n", Enumerable.Range(1, 5000).Select(i => $"' line {i} padding padding padding"));
        code.Length.ShouldBeGreaterThan(VbaChatTools.MaxModuleChars);
        var snapshot = new CurrentModuleSnapshot("modHuge", "Standard", code, false, null);

        var result = await Tool(Create(snapshot), "vba_get_current_module")(null, CancellationToken.None);

        result.Text.ShouldContain($"[truncated - showing the first {VbaChatTools.MaxModuleChars} of {code.Length} chars]");
        result.Text.Length.ShouldBeLessThan(code.Length);
    }

    [Fact]
    public async Task ListModules_SortsCaseInsensitivelyAndNamesEachKind()
    {
        var json = ModuleListJson(("zed", 1, 10), ("Form_Orders", 100, 250), ("clsStock", 2, 40), ("alpha", 1, 5), ("UserForm1", 3, 12));

        var result = await Tool(Create(listJson: json), "vba_list_modules")(null, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Text.ShouldStartWith("VBA project 'FAM': 5 module(s).");
        var lines = result.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        lines.ShouldBe(
        [
            "alpha  [Standard, 5 lines]",
            "clsStock  [Class, 40 lines]",
            "Form_Orders  [Document, 250 lines]",
            "UserForm1  [Form, 12 lines]",
            "zed  [Standard, 10 lines]",
        ]);
    }

    [Fact]
    public async Task ListModules_FiltersByNameContains_CaseInsensitively()
    {
        var json = ModuleListJson(("modOrders", 1, 10), ("clsOrderLine", 2, 20), ("modStock", 1, 30));

        var result = await Tool(Create(listJson: json), "vba_list_modules")(Args("nameContains", "ORDER"), CancellationToken.None);

        result.Text.ShouldContain("3 module(s), 2 with 'ORDER' in the name.");
        result.Text.ShouldContain("clsOrderLine");
        result.Text.ShouldContain("modOrders");
        result.Text.ShouldNotContain("modStock");
    }

    [Fact]
    public async Task ListModules_CapsTheListAndPointsAtTheFilter()
    {
        var json = ModuleListJson(Enumerable.Range(1, 450).Select(i => ($"mod{i:0000}", 1, i)).ToArray());

        var result = await Tool(Create(listJson: json), "vba_list_modules")(null, CancellationToken.None);

        result.Text.ShouldContain($"[showing the first {VbaChatTools.MaxListedModules} of 450 - pass nameContains to narrow the list]");
        result.Text.ShouldContain("mod0400  [");
        result.Text.ShouldNotContain("mod0401  [");
    }

    [Fact]
    public async Task ListModules_WhenAccessFails_ReturnsAnErrorResultWithTheMessage()
    {
        var tools = Create(accessFailure: new InvalidOperationException("Access COM automation is disabled by configuration."));

        var result = await Tool(tools, "vba_list_modules")(null, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Text.ShouldBe("Could not list modules from Access: Access COM automation is disabled by configuration.");
    }

    [Fact]
    public async Task GetModule_RequiresAName_ButAcceptsANonStringOne()
    {
        var execute = Tool(Create(), "vba_get_module");

        (await execute(null, CancellationToken.None)).IsError.ShouldBeTrue();
        (await execute(new JsonArray("modOrders"), CancellationToken.None)).IsError.ShouldBeTrue();
        (await execute(Args("name", "   "), CancellationToken.None)).IsError.ShouldBeTrue();

        // A model that sends a number where a string was asked for still gets a sensible lookup.
        var numeric = await execute(new JsonObject { ["name"] = 42 }, CancellationToken.None);
        numeric.IsError.ShouldBeFalse();
        numeric.Text.ShouldStartWith("Module: 42");
    }

    [Fact]
    public async Task GetModule_ReturnsTheLiveCode()
    {
        var tools = Create(codeJson: name => ModuleCodeJson(name, "Public Function Total() As Currency\r\nEnd Function"));

        var result = await Tool(tools, "vba_get_module")(Args("name", " modOrders "), CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Text.ShouldContain("Module: modOrders");
        result.Text.ShouldContain("Source: live from Access.");
        result.Text.ShouldNotContain("unpushed edits");
        result.Text.ShouldContain("Code (2 lines):");
        result.Text.ShouldEndWith("End Function");
    }

    [Fact]
    public async Task GetModule_NotesWhenThatModuleHasUnpushedEditsInTheToolWindow()
    {
        var edited = new CurrentModuleSnapshot("MODORDERS", "Standard", "edited", HasUnpushedChanges: true, null);
        var other = new CurrentModuleSnapshot("modStock", "Standard", "edited", HasUnpushedChanges: true, null);

        var noted = await Tool(Create(snapshot: edited), "vba_get_module")(Args("name", "modOrders"), CancellationToken.None);
        var clean = await Tool(Create(snapshot: other), "vba_get_module")(Args("name", "modOrders"), CancellationToken.None);

        noted.Text.ShouldContain("open in the Access VBA tool window with unpushed edits");
        clean.Text.ShouldNotContain("unpushed edits");
    }

    [Fact]
    public async Task GetModule_WhenAccessFails_ReturnsAnErrorResultWithTheMessage()
    {
        var tools = Create(accessFailure: new InvalidOperationException("Subscript out of range"));

        var result = await Tool(tools, "vba_get_module")(Args("name", "modMissing"), CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Text.ShouldBe("Could not read module 'modMissing' from Access: Subscript out of range");
    }

    [Fact]
    public async Task AnAccessCallThatNeverReturns_BecomesATimeoutError_NotAHang()
    {
        var never = new TaskCompletionSource<string>();
        var tools = new VbaChatTools(
            getCurrentModule: _ => Task.FromResult<CurrentModuleSnapshot?>(null),
            listModulesJson: _ => never.Task,
            getModuleJson: (_, _) => never.Task,
            accessTimeout: TimeSpan.FromMilliseconds(200));

        var listed = await Tool(tools, "vba_list_modules")(null, CancellationToken.None);
        var read = await Tool(tools, "vba_get_module")(Args("name", "modOrders"), CancellationToken.None);

        listed.IsError.ShouldBeTrue();
        listed.Text.ShouldContain("did not answer within 0 seconds");
        listed.Text.ShouldContain("dialog");
        read.IsError.ShouldBeTrue();
        read.Text.ShouldStartWith("Could not read module 'modOrders' from Access: Access did not answer");
    }

    [Fact]
    public async Task CancellingTheChatTurn_PropagatesAsCancellation_NotAsAToolError()
    {
        var never = new TaskCompletionSource<string>();
        var tools = new VbaChatTools(
            getCurrentModule: _ => Task.FromResult<CurrentModuleSnapshot?>(null),
            listModulesJson: _ => never.Task,
            getModuleJson: (_, _) => never.Task);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => Tool(tools, "vba_list_modules")(null, cancellation.Token));
    }
}
