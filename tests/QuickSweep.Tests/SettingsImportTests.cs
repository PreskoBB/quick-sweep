using System.Text;
using QuickSweep.Core;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>The installer-side merge (QuickSweep.exe --import-settings): same keys only, nothing written unless the file was usable.
/// The tests pass in-memory load/save callbacks, so no real state folder is read or written.</summary>
public class SettingsImportTests
{
    sealed class Rig
    {
        public AppSettings Current = new() { CpuAlertC = 80, GpuAlertC = 70, TrayPromotedFor = "installed.exe" };
        public AppSettings? Saved;
        public int Saves;
        public bool SaveWorks = true;
        public (AppSettings? settings, string why) Load() => (Current, "");
        public bool Save(AppSettings s) { Saves++; if (SaveWorks) Saved = s; return SaveWorks; }
        public int Run(string? file, out string msg) => SettingsImport.Run(file, Load, Save, out msg);
    }

    const string OwnerLike = """
        { "ExtraGameFolders": ["E:\\Games"],
          "ExtraScanFolders": [ { "Pattern": "D:\\p\\**\\node_modules", "Label": "node_modules", "IdleDays": 30 } ],
          "ProtectedFolders": ["D:\\Keep", "D:\\p\\**\\src"],
          "TrayPromotedFor": "other.exe" }
        """;

    [Fact]
    public void MergesOnlyTheKeysInTheFile()
    {
        using var sb = new Sandbox(); var r = new Rig();
        Assert.Equal(SettingsImport.Done, r.Run(sb.File("s.json", OwnerLike), out var msg));
        Assert.Equal(1, r.Saves);
        var s = r.Saved!;
        Assert.Equal(80, s.CpuAlertC); Assert.Equal(70, s.GpuAlertC);                            // keys not in the file keep their value
        Assert.Contains(@"E:\Games", s.ExtraGameFolders);
        Assert.Equal(30, Assert.Single(s.ExtraScanFolders).IdleDays);
        Assert.Equal(new[] { @"D:\Keep", @"D:\p\**\src" }, s.ProtectedFolders);
        Assert.Equal("installed.exe", s.TrayPromotedFor);                                         // belongs to the installation, never imported
        Assert.Contains("merged 3 key(s)", msg);
    }

    [Fact]
    public void ListsInTheFileReplaceTheCurrentOnesAndOtherListsStay()
    {
        using var sb = new Sandbox(); var r = new Rig();
        r.Current.ProtectedFolders.Add(@"D:\Old"); r.Current.ExtraGameFolders.Add(@"E:\Mine");
        r.Run(sb.File("s.json", """{ "ProtectedFolders": ["D:\\New"] }"""), out _);
        Assert.Equal(new[] { @"D:\New" }, r.Saved!.ProtectedFolders);
        Assert.Equal(new[] { @"E:\Mine" }, r.Saved.ExtraGameFolders);
    }

    [Fact]
    public void LegacyKeysInTheFileAreFoldedIn()
    {
        using var sb = new Sandbox(); var r = new Rig();
        r.Current.ProtectedFolders.Add(@"D:\Old");
        r.Run(sb.File("s.json", """{ "ExtraDenyPatterns": ["D:\\Legacy"] }"""), out _);
        Assert.Contains(@"D:\Legacy", r.Saved!.ProtectedFolders);
        Assert.Contains(@"D:\Old", r.Saved.ProtectedFolders);
    }

    [Fact]
    public void AFileWrittenWithAByteOrderMarkWorks()
    {
        using var sb = new Sandbox(); var r = new Rig();
        var path = Path.Combine(sb.Root, "bom.json");
        File.WriteAllText(path, """{ "LowSpaceGb": 25 }""", new UTF8Encoding(true));            // what Windows PowerShell and Notepad write
        Assert.Equal(SettingsImport.Done, r.Run(path, out _));
        Assert.Equal(25, r.Saved!.LowSpaceGb);
    }

    [Fact]
    public void AnEmptyObjectChangesNothingAndWritesNothing()
    {
        using var sb = new Sandbox(); var r = new Rig();
        Assert.Equal(SettingsImport.Done, r.Run(sb.File("s.json", "{}"), out _));
        Assert.Equal(0, r.Saves);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    [InlineData("\"just a string\"")]
    public void AFileThatIsNotSettingsIsRefusedAndNothingIsWritten(string text)
    {
        using var sb = new Sandbox(); var r = new Rig();
        Assert.Equal(SettingsImport.NotSettings, r.Run(sb.File("s.json", text), out _));
        Assert.Equal(0, r.Saves);
    }

    [Fact]
    public void AMissingOrEmptyNameIsRefused()
    {
        using var sb = new Sandbox(); var r = new Rig();
        Assert.Equal(SettingsImport.Unreadable, r.Run(Path.Combine(sb.Root, "nope.json"), out var msg));
        Assert.Contains("not found", msg);
        Assert.Equal(SettingsImport.Unreadable, r.Run(null, out _));
        Assert.Equal(SettingsImport.Unreadable, r.Run("  ", out _));
        Assert.Equal(0, r.Saves);
    }

    [Fact]
    public void ALargeFileIsRefused()
    {
        using var sb = new Sandbox(); var r = new Rig();
        var path = sb.File("big.json", "{ \"ProtectedFolders\": [\"" + new string('x', SettingsImport.MaxBytes) + "\"] }");
        Assert.Equal(SettingsImport.Unreadable, r.Run(path, out var msg));
        Assert.Contains("larger than", msg);
        Assert.Equal(0, r.Saves);
    }

    [Fact]
    public void AFileReachedThroughAJunctionIsRefused()
    {
        using var sb = new Sandbox(); var r = new Rig();
        sb.File(@"real\s.json", OwnerLike);
        var link = sb.Junction("via-link", Path.Combine(sb.Root, "real"));
        Assert.Equal(SettingsImport.Unreadable, r.Run(Path.Combine(link, "s.json"), out _));
        Assert.Equal(0, r.Saves);
    }

    [Fact]
    public void CurrentSettingsThatCannotBeReadAreNeverReplaced()
    {
        using var sb = new Sandbox();
        int saves = 0;
        int code = SettingsImport.Run(sb.File("s.json", OwnerLike), () => (null, "settings.json cannot be read: a link"), _ => { saves++; return true; }, out var msg);
        Assert.Equal(SettingsImport.NotSaved, code);
        Assert.Equal(0, saves);
        Assert.Contains("nothing changed", msg);
    }

    [Fact]
    public void AFailedSaveIsReported()
    {
        using var sb = new Sandbox(); var r = new Rig { SaveWorks = false };
        Assert.Equal(SettingsImport.NotSaved, r.Run(sb.File("s.json", OwnerLike), out _));
        Assert.Equal(1, r.Saves);
    }

    [Theory]
    [InlineData(new[] { "--import-settings=C:\\x\\s.json" }, "C:\\x\\s.json", true)]
    [InlineData(new[] { "--import-settings", "C:\\x y\\s.json" }, "C:\\x y\\s.json", true)]
    [InlineData(new[] { "--dev", "--import-settings=\"C:\\x\\s.json\"" }, "C:\\x\\s.json", true)]
    [InlineData(new[] { "--import-settings" }, null, true)]                                       // a bare switch is an error, never a normal start
    [InlineData(new[] { "--dev", "--open" }, null, false)]
    [InlineData(new string[0], null, false)]
    public void TheCommandLineIsRead(string[] args, string? file, bool requested)
    {
        Assert.Equal(file, SettingsImport.FileFromArgs(args, out var got));
        Assert.Equal(requested, got);
    }

    [Fact]
    public void SaveAndLoadForImportAgreeWithTheNormalSettingsFile()
    {
        // the real loader maps "no file" to defaults and a refused/unreadable file to null; checked on the parser it uses
        Assert.NotNull(AppSettings.Parse("{}"));
        Assert.Throws<System.Text.Json.JsonException>(() => AppSettings.Parse("{ not valid"));
    }
}
