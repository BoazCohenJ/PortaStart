using System;
using System.Collections.Generic;
using System.Linq;
using PortaStart;
using Xunit;

public class SelectorTests
{
    private static PortablePackage Pkg(string id, string name, string target, bool archive = false, string loc = @"C:\P\pkg") =>
        new() { Key = id + "__Microsoft.Winget.Source_8wekyb3d8bbwe", PackageId = id, DisplayName = name, TargetPath = target, InstallLocation = loc, IsArchive = archive };

    private static ExeInfo Gui(string p) => new(p, Subsystem.Gui);
    private static ExeInfo Cli(string p) => new(p, Subsystem.Console);

    [Fact]
    public void SingleGuiExeGetsShortcutNamedAfterPackage()
    {
        var p = Pkg("Foo.Bar", "Bar Viewer", @"C:\P\pkg\bar.exe");
        var r = Selector.Choose(p, new[] { Gui(@"C:\P\pkg\bar.exe") }, false, Arch.X64);
        Assert.Single(r);
        Assert.Equal("Bar Viewer", r[0].Name);
        Assert.Equal(@"C:\P\pkg\bar.exe", r[0].ExePath);
    }

    [Fact]
    public void ConsoleToolGetsNothingUnlessOptedIn()
    {
        var p = Pkg("BurntSushi.ripgrep.MSVC", "RipGrep", @"C:\P\pkg\rg.exe");
        var files = new[] { Cli(@"C:\P\pkg\rg.exe") };
        Assert.Empty(Selector.Choose(p, files, false, Arch.X64));
        Assert.Single(Selector.Choose(p, files, true, Arch.X64));
    }

    [Fact]
    public void UnknownSubsystemIsSkipped()
    {
        var p = Pkg("A.B", "AB", @"C:\P\pkg\ab.exe");
        Assert.Empty(Selector.Choose(p, new[] { new ExeInfo(@"C:\P\pkg\ab.exe", Subsystem.Unknown) }, true, Arch.X64));
    }

    [Fact]
    public void MissingTargetGivesNothingForSingleFile()
    {
        var p = Pkg("A.B", "AB", @"C:\P\pkg\ab.exe");
        Assert.Empty(Selector.Choose(p, new ExeInfo[0], false, Arch.X64));
    }

    [Fact]
    public void ArchivePrefers64BitMainProgramAndSkipsHelpers()
    {
        var p = Pkg("Microsoft.Sysinternals.ProcessExplorer", "Process Explorer", @"C:\P\pkg\procexp64.exe", archive: true);
        var files = new[] { Gui(@"C:\P\pkg\procexp.exe"), Gui(@"C:\P\pkg\procexp64.exe"), Gui(@"C:\P\pkg\uninstall.exe") };
        var r = Selector.Choose(p, files, false, Arch.X64);
        Assert.Single(r);
        Assert.Equal(@"C:\P\pkg\procexp64.exe", r[0].ExePath);
        Assert.Equal("Process Explorer", r[0].Name);

        var r32 = Selector.Choose(p, files, false, Arch.X86);
        Assert.Equal(@"C:\P\pkg\procexp.exe", r32.Single().ExePath);
    }

    [Fact]
    public void ArchiveWithConsoleTargetStillFindsGuiApp()
    {
        var p = Pkg("Example.Viewer", "Viewer", @"C:\P\pkg\viewer-cli.exe", archive: true);
        var files = new[] { Cli(@"C:\P\pkg\viewer-cli.exe"), Gui(@"C:\P\pkg\Viewer.exe"), Gui(@"C:\P\pkg\updater.exe") };
        var r = Selector.Choose(p, files, false, Arch.X64);
        Assert.Equal(@"C:\P\pkg\Viewer.exe", r.Single().ExePath);
    }

    [Fact]
    public void ArchiveMatchesExeByPackageIdWhenDisplayNameDiffers()
    {
        var p = Pkg("Martchus.syncthingtray", "Syncthing Tray", null, archive: true);
        var files = new[] { Gui(@"C:\P\pkg\syncthingtray.exe"), Gui(@"C:\P\pkg\syncthingctl.exe") };
        Assert.Equal(@"C:\P\pkg\syncthingtray.exe", Selector.Choose(p, files, false, Arch.X64).Single().ExePath);
    }

    [Fact]
    public void ArchiveWithNoClearMainProgramMakesOneEachLabelled()
    {
        var p = Pkg("Suite.Tools", "Suite", null, archive: true);
        var files = new[] { Gui(@"C:\P\pkg\alpha.exe"), Gui(@"C:\P\pkg\beta.exe") };
        var r = Selector.Choose(p, files, false, Arch.X64);
        Assert.Equal(new[] { "Suite - alpha", "Suite - beta" }, r.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void ArchivePoolIsBounded()
    {
        var p = Pkg("Big.Suite", "Big", null, archive: true);
        var files = Enumerable.Range(0, 12).Select(i => Gui($@"C:\P\pkg\tool{(char)('a' + i)}.exe")).ToArray();
        Assert.Equal(5, Selector.Choose(p, files, false, Arch.X64).Count);
    }

    // The real Microsoft.Sysinternals.ProcessExplorer zip: x86, x64 and ARM64 builds side by side, and
    // winget's target can be any of them. "procexp" only matches "Process Explorer" as an abbreviation.
    [Theory]
    [InlineData(Arch.X64, @"C:\P\pkg\procexp64.exe")]
    [InlineData(Arch.Arm64, @"C:\P\pkg\procexp64a.exe")]
    [InlineData(Arch.X86, @"C:\P\pkg\procexp.exe")]
    public void ProcessExplorerPicksTheBuildForThisCpu(ushort os, string expected)
    {
        var p = Pkg("Microsoft.Sysinternals.ProcessExplorer", "Process Explorer", @"C:\P\pkg\procexp64a.exe", archive: true);
        var files = new[]
        {
            new ExeInfo(@"C:\P\pkg\procexp.exe", Subsystem.Gui, Arch.X86),
            new ExeInfo(@"C:\P\pkg\procexp64.exe", Subsystem.Gui, Arch.X64),
            new ExeInfo(@"C:\P\pkg\procexp64a.exe", Subsystem.Gui, Arch.Arm64),
        };
        var r = Selector.Choose(p, files, false, os);
        Assert.Equal(expected, r.Single().ExePath);
        Assert.Equal("Process Explorer", r.Single().Name);
    }

    [Fact]
    public void ArmBuildIsNeverChosenOnX64()
    {
        var p = Pkg("A.Tool", "Tool", null, archive: true);
        var files = new[] { new ExeInfo(@"C:\P\pkg\tool-arm64.exe", Subsystem.Gui, Arch.Arm64) };
        Assert.Empty(Selector.Choose(p, files, false, Arch.X64));
    }

    // The real BurntSushi.ripgrep.MSVC zip: console rg.exe in three architecture folders.
    [Fact]
    public void RipgrepGetsNothingAndOptInGivesOne()
    {
        var p = Pkg("BurntSushi.ripgrep.MSVC", "RipGrep MSVC", @"C:\P\pkg\ripgrep-x86_64\rg.exe", archive: true);
        var files = new[]
        {
            new ExeInfo(@"C:\P\pkg\ripgrep-x86_64\rg.exe", Subsystem.Console, Arch.X64),
            new ExeInfo(@"C:\P\pkg\ripgrep-i686\rg.exe", Subsystem.Console, Arch.X86),
            new ExeInfo(@"C:\P\pkg\ripgrep-aarch64\rg.exe", Subsystem.Console, Arch.Arm64),
        };
        Assert.Empty(Selector.Choose(p, files, false, Arch.X64));
        Assert.Equal(@"C:\P\pkg\ripgrep-x86_64\rg.exe", Selector.Choose(p, files, true, Arch.X64).Single().ExePath);
    }

    [Theory]
    [InlineData("procexp", "processexplorer", 1)]
    [InlineData("syncthingtray", "syncthingtray", 3)]
    [InlineData("tray", "syncthingtray", 2)]
    [InlineData("xyz", "processexplorer", 0)]
    [InlineData("explorer", "processexplorer", 2)]
    public void Scores(string exe, string key, int expected) => Assert.Equal(expected, Selector.Score(exe, new[] { key }));

    [Theory]
    [InlineData("unins000.exe", true)]
    [InlineData("Uninstall.exe", true)]
    [InlineData("updater.exe", true)]
    [InlineData("crashpad_handler.exe", true)]
    [InlineData("CrashReporter.exe", true)]
    [InlineData("notepad++.exe", false)]
    [InlineData("Updatey Game.exe", false)]
    [InlineData("setup.exe", true)]
    public void JunkNames(string file, bool junk) => Assert.Equal(junk, Selector.IsJunkName(file));

    [Theory]
    [InlineData("A:B*C?", "A B C")]
    [InlineData("  Trailing dots...  ", "Trailing dots")]
    [InlineData("", "App")]
    [InlineData("<>", "App")]
    public void CleanName(string input, string expected) => Assert.Equal(expected, Selector.CleanName(input));

    [Theory]
    [InlineData("app64", "app")]
    [InlineData("app_x64", "app")]
    [InlineData("App-x64", "App")]
    [InlineData("64", "64")]
    [InlineData("tool", "tool")]
    public void StripBitness(string input, string expected) => Assert.Equal(expected, Selector.StripBitness(input));
}

public class PlannerTests
{
    private const string Dir = @"C:\Start";

    private sealed class FakeWorld : IWorld
    {
        public Dictionary<string, string> Lnks = new(StringComparer.OrdinalIgnoreCase); // lnk -> target
        public HashSet<string> Others = new(StringComparer.OrdinalIgnoreCase);
        public bool LnkExists(string l) => Lnks.ContainsKey(l);
        public string LnkTarget(string l) => Lnks.TryGetValue(l, out var t) ? t : null;
        public bool OtherShortcutTargets(string exe) => Others.Contains(exe);
    }

    private static Wanted W(string key, string exe, string name) => new() { PackageKey = key, PackageId = key, ExePath = exe, Name = name };
    private static Owned O(string name, string key, string exe, bool dismissed = false) => new() { LnkPath = $@"{Dir}\{name}.lnk", PackageKey = key, ExePath = exe, Dismissed = dismissed };

    [Fact]
    public void CreatesNewShortcut()
    {
        var a = Planner.Plan(new[] { W("k", @"C:\a.exe", "A") }, new Owned[0], Dir, new FakeWorld());
        Assert.Equal(ActionKind.Create, a.Single().Kind);
        Assert.Equal(@"C:\Start\A.lnk", a.Single().LnkPath);
    }

    [Fact]
    public void SkipsWhenAppAlreadyHasAShortcutElsewhere()
    {
        var w = new FakeWorld(); w.Others.Add(@"C:\a.exe");
        Assert.Equal(ActionKind.Skip, Planner.Plan(new[] { W("k", @"C:\a.exe", "A") }, new Owned[0], Dir, w).Single().Kind);
    }

    [Fact]
    public void NeverOverwritesSomeoneElsesShortcutWithTheSameName()
    {
        var w = new FakeWorld(); w.Lnks[@"C:\Start\A.lnk"] = @"C:\other.exe";
        var a = Planner.Plan(new[] { W("k", @"C:\a.exe", "A") }, new Owned[0], Dir, w).Single();
        Assert.Equal(ActionKind.Skip, a.Kind);
    }

    [Fact]
    public void UpToDateMeansNoActions()
    {
        var w = new FakeWorld(); w.Lnks[@"C:\Start\A.lnk"] = @"C:\a.exe";
        Assert.Empty(Planner.Plan(new[] { W("k", @"C:\a.exe", "A") }, new[] { O("A", "k", @"C:\a.exe") }, Dir, w));
    }

    [Fact]
    public void DeletesShortcutWhenPackageIsUninstalled()
    {
        var w = new FakeWorld(); w.Lnks[@"C:\Start\A.lnk"] = @"C:\a.exe";
        Assert.Equal(ActionKind.Delete, Planner.Plan(new Wanted[0], new[] { O("A", "k", @"C:\a.exe") }, Dir, w).Single().Kind);
    }

    [Fact]
    public void DoesNotDeleteAShortcutTheUserRepointed()
    {
        var w = new FakeWorld(); w.Lnks[@"C:\Start\A.lnk"] = @"D:\mine.exe";
        Assert.Equal(ActionKind.Forget, Planner.Plan(new Wanted[0], new[] { O("A", "k", @"C:\a.exe") }, Dir, w).Single().Kind);
    }

    [Fact]
    public void UserDeletedShortcutIsDismissedThenStaysAway()
    {
        var owned = new[] { O("A", "k", @"C:\a.exe") };
        var wanted = new[] { W("k", @"C:\a.exe", "A") };
        var a = Planner.Plan(wanted, owned, Dir, new FakeWorld());
        Assert.Equal(ActionKind.Dismiss, a.Single().Kind);
        var after = Planner.Apply(owned, a);
        Assert.True(after.Single().Dismissed);
        Assert.Empty(Planner.Plan(wanted, after, Dir, new FakeWorld()));
    }

    [Fact]
    public void DismissedEntryIsForgottenOnUninstallSoReinstallGetsAShortcut()
    {
        var owned = new[] { O("A", "k", @"C:\a.exe", dismissed: true) };
        var a = Planner.Plan(new Wanted[0], owned, Dir, new FakeWorld());
        Assert.Equal(ActionKind.Forget, a.Single().Kind);
        Assert.Empty(Planner.Apply(owned, a));
    }

    [Fact]
    public void UpdatesTargetWhenProgramMoves()
    {
        var w = new FakeWorld(); w.Lnks[@"C:\Start\A.lnk"] = @"C:\v1\a.exe";
        var a = Planner.Plan(new[] { W("k", @"C:\v2\a.exe", "A") }, new[] { O("A", "k", @"C:\v1\a.exe") }, Dir, w).Single();
        Assert.Equal(ActionKind.Update, a.Kind);
        Assert.Equal(@"C:\v2\a.exe", a.ExePath);
    }

    [Fact]
    public void DuplicateDisplayNamesGetPackageIdSuffix()
    {
        var a = Planner.Plan(new[] { W("one", @"C:\1.exe", "Tool"), W("two", @"C:\2.exe", "Tool") }, new Owned[0], Dir, new FakeWorld());
        Assert.Equal(new[] { @"C:\Start\Tool.lnk", @"C:\Start\Tool (two).lnk" }, a.Select(x => x.LnkPath).ToArray());
    }

    [Fact]
    public void ChangingFolderMovesShortcuts()
    {
        var w = new FakeWorld(); w.Lnks[@"C:\Start\A.lnk"] = @"C:\a.exe";
        var a = Planner.Plan(new[] { W("k", @"C:\a.exe", "A") }, new[] { O("A", "k", @"C:\a.exe") }, @"C:\Start\WinGet", w);
        Assert.Contains(a, x => x.Kind == ActionKind.Delete && x.LnkPath == @"C:\Start\A.lnk");
        Assert.Contains(a, x => x.Kind == ActionKind.Create && x.LnkPath == @"C:\Start\WinGet\A.lnk");
    }
}

public class PeInfoTests
{
    private static byte[] Pe(ushort subsystem, ushort characteristics = 0x0102)
    {
        var b = new byte[512];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);
        b[0x80] = (byte)'P'; b[0x81] = (byte)'E';
        BitConverter.GetBytes(characteristics).CopyTo(b, 0x80 + 4 + 18);
        BitConverter.GetBytes(subsystem).CopyTo(b, 0x80 + 4 + 20 + 68);
        return b;
    }

    [Fact] public void Gui() => Assert.Equal(Subsystem.Gui, PeInfo.Parse(Pe(2), 512));
    [Fact] public void Console() => Assert.Equal(Subsystem.Console, PeInfo.Parse(Pe(3), 512));
    [Fact] public void Dll() => Assert.Equal(Subsystem.Unknown, PeInfo.Parse(Pe(2, 0x2102), 512));
    [Fact] public void NotPe() => Assert.Equal(Subsystem.Unknown, PeInfo.Parse(new byte[512], 512));
    [Fact] public void Truncated() => Assert.Equal(Subsystem.Unknown, PeInfo.Parse(Pe(2), 0x90));

    [Fact]
    public void RealSystemFiles()
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Assert.Equal(Subsystem.Gui, PeInfo.Read(System.IO.Path.Combine(sys, "notepad.exe")));
        Assert.Equal(Subsystem.Console, PeInfo.Read(System.IO.Path.Combine(sys, "cmd.exe")));
    }
}

public class ConfigTests
{
    [Fact]
    public void RoundTrips()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "portastart-cfg-" + Guid.NewGuid() + ".ini");
        try
        {
            var c = new Config { Folder = "WinGet Apps" };
            c.Exclude.Add("A.B"); c.Console.Add("C.D");
            c.Save(path);
            var d = Config.Load(path);
            Assert.Equal("WinGet Apps", d.Folder);
            Assert.Contains("a.b", d.Exclude);
            Assert.Contains("C.D", d.Console);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void StateRoundTrips()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "portastart-state-" + Guid.NewGuid() + ".tsv");
        try
        {
            State.Save(new[] { new Owned { LnkPath = @"C:\S\A.lnk", PackageKey = "k", ExePath = @"C:\a.exe", Dismissed = true } }, path);
            State.Save(new[] { new Owned { LnkPath = @"C:\S\B.lnk", PackageKey = "k2", ExePath = @"C:\b.exe" } }, path);
            var s = State.Load(path).Single();
            Assert.Equal(@"C:\S\B.lnk", s.LnkPath);
            Assert.False(s.Dismissed);
        }
        finally { System.IO.File.Delete(path); }
    }
}
