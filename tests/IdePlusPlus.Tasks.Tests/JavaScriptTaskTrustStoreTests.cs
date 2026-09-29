using Shouldly;
using Xunit;

namespace IdePlusPlus.Tasks.Tests;

public class JavaScriptTaskTrustStoreTests
{
    [Fact]
    public void NothingIsTrustedByDefault()
    {
        using var directory = new TempDirectory();
        var store = new JavaScriptTaskTrustStore(directory.PathFor("trust.json"));

        store.IsTrusted(@"C:\repo", Fingerprint("gulpfile.js", "task('a')")).ShouldBeFalse();
    }

    [Fact]
    public void TrustThenIsTrusted()
    {
        using var directory = new TempDirectory();
        var store = new JavaScriptTaskTrustStore(directory.PathFor("trust.json"));
        var fingerprint = Fingerprint("gulpfile.js", "task('a')");

        store.Trust(@"C:\repo", fingerprint);

        store.IsTrusted(@"C:\repo", fingerprint).ShouldBeTrue();
    }

    /// <summary>The point of fingerprinting: approving a repo once must not approve whatever its
    /// gulpfile becomes on the next pull.</summary>
    [Fact]
    public void EditingTheManifestRevokesTrustImplicitly()
    {
        using var directory = new TempDirectory();
        var store = new JavaScriptTaskTrustStore(directory.PathFor("trust.json"));
        store.Trust(@"C:\repo", Fingerprint("gulpfile.js", "task('a')"));

        store.IsTrusted(@"C:\repo", Fingerprint("gulpfile.js", "task('a'); rm -rf /")).ShouldBeFalse();
    }

    /// <summary>Adding a second manifest is a change too - approving one gulpfile must not approve a
    /// Gruntfile that appears beside it.</summary>
    [Fact]
    public void AddingAManifestChangesTheFingerprint()
    {
        var one = JavaScriptTaskTrustStore.Fingerprint([(@"C:\repo\gulpfile.js", "task('a')")]);
        var two = JavaScriptTaskTrustStore.Fingerprint(
            [(@"C:\repo\gulpfile.js", "task('a')"), (@"C:\repo\Gruntfile.js", "grunt.registerTask('b')")]);

        one.ShouldNotBe(two);
    }

    [Fact]
    public void FingerprintIgnoresManifestOrder()
    {
        var forwards = JavaScriptTaskTrustStore.Fingerprint([(@"C:\a\gulpfile.js", "x"), (@"C:\b\Gruntfile.js", "y")]);
        var backwards = JavaScriptTaskTrustStore.Fingerprint([(@"C:\b\Gruntfile.js", "y"), (@"C:\a\gulpfile.js", "x")]);

        forwards.ShouldBe(backwards);
    }

    /// <summary>A checkout under a different autocrlf setting is the same manifest.</summary>
    [Fact]
    public void FingerprintIgnoresLineEndings()
    {
        var lf = JavaScriptTaskTrustStore.Fingerprint([(@"C:\repo\gulpfile.js", "a\nb\n")]);
        var crlf = JavaScriptTaskTrustStore.Fingerprint([(@"C:\repo\gulpfile.js", "a\r\nb\r\n")]);

        lf.ShouldBe(crlf);
    }

    [Fact]
    public void TrustIsPerSolution()
    {
        using var directory = new TempDirectory();
        var store = new JavaScriptTaskTrustStore(directory.PathFor("trust.json"));
        var fingerprint = Fingerprint("gulpfile.js", "task('a')");

        store.Trust(@"C:\repo-one", fingerprint);

        store.IsTrusted(@"C:\repo-two", fingerprint).ShouldBeFalse();
    }

    [Fact]
    public void TrustSurvivesANewStoreInstance()
    {
        using var directory = new TempDirectory();
        var path = directory.PathFor("trust.json");
        var fingerprint = Fingerprint("gulpfile.js", "task('a')");
        new JavaScriptTaskTrustStore(path).Trust(@"C:\repo", fingerprint);

        new JavaScriptTaskTrustStore(path).IsTrusted(@"C:\repo", fingerprint).ShouldBeTrue();
    }

    [Fact]
    public void Revoke_RemovesTrust()
    {
        using var directory = new TempDirectory();
        var store = new JavaScriptTaskTrustStore(directory.PathFor("trust.json"));
        var fingerprint = Fingerprint("gulpfile.js", "task('a')");
        store.Trust(@"C:\repo", fingerprint);

        store.Revoke(@"C:\repo");

        store.IsTrusted(@"C:\repo", fingerprint).ShouldBeFalse();
    }

    [Fact]
    public void DifferentSpellingsOfTheSamePathAreTheSameSolution()
    {
        using var directory = new TempDirectory();
        var store = new JavaScriptTaskTrustStore(directory.PathFor("trust.json"));
        var fingerprint = Fingerprint("gulpfile.js", "task('a')");

        store.Trust(@"C:\Repo\", fingerprint);

        store.IsTrusted(@"c:\repo", fingerprint).ShouldBeTrue();
    }

    /// <summary>An unreadable trust file means nothing is trusted, which fails closed.</summary>
    [Fact]
    public void MalformedTrustFile_TrustsNothing()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteFile("trust.json", "{ not json");

        new JavaScriptTaskTrustStore(path).IsTrusted(@"C:\repo", Fingerprint("gulpfile.js", "x")).ShouldBeFalse();
    }

    private static string Fingerprint(string name, string text) =>
        JavaScriptTaskTrustStore.Fingerprint([($@"C:\repo\{name}", text)]);
}
