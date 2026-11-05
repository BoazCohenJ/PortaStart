using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace PortaStart;

/// <summary>
/// Fakes what winget does for portable installs (an Uninstall registry entry plus files), then checks that the
/// watcher reacts: GUI app gets a shortcut, console tool doesn't, a zip gets one shortcut for its main program,
/// uninstalling removes it, and a shortcut the user deleted stays deleted. Touches only its own fake entries.
/// </summary>
internal static class SelfTest
{
    private const string Prefix = "PortaStart.SelfTest.";
    private static readonly string GuiKey = Prefix + "App__selftest", CliKey = Prefix + "Tool__selftest", ZipKey = Prefix + "Viewer__selftest";

    public static int Run(TextWriter o)
    {
        int fails = 0;
        void Check(bool ok, string what) { o.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

        string root = Path.Combine(Path.GetTempPath(), "PortaStart-selftest-" + Process.GetCurrentProcess().Id);
        var keys = new[] { GuiKey, CliKey, ZipKey };
        string startDir = Config.Load().StartDir;
        string guiLnk = Path.Combine(startDir, "PortaStart Self-Test App.lnk");
        string cliLnk = Path.Combine(startDir, "PortaStart Self-Test Tool.lnk");
        string zipLnk = Path.Combine(startDir, "PortaStart Self-Test Viewer.lnk");

        using var stop = new ManualResetEvent(false);
        using var ready = new ManualResetEvent(false);
        var log = new List<string>();
        var watcher = new Thread(() => Watcher.Run(l => { lock (log) log.Add(l); }, keys, stop, ready)) { IsBackground = true };
        try
        {
            o.WriteLine($"Self-test: fake winget portable installs in {root}");
            o.WriteLine($"Shortcuts go to {startDir}");
            string self = Process.GetCurrentProcess().MainModule.FileName;
            string guiExe = MakeExe(self, Path.Combine(root, "App", "FakeApp.exe"), Subsystem.Gui);
            string cliExe = MakeExe(self, Path.Combine(root, "Tool", "faketool.exe"), Subsystem.Console);
            string zipDir = Path.Combine(root, "Viewer");
            MakeExe(self, Path.Combine(zipDir, "Viewer.exe"), Subsystem.Gui);
            string viewer64 = MakeExe(self, Path.Combine(zipDir, "Viewer64.exe"), Subsystem.Gui);
            MakeExe(self, Path.Combine(zipDir, "uninstall.exe"), Subsystem.Gui);
            string viewerCli = MakeExe(self, Path.Combine(zipDir, "viewer-cli.exe"), Subsystem.Console);
            File.WriteAllText(Path.Combine(zipDir, ZipKey + ".db"), "");

            Check(PeInfo.Read(guiExe) == Subsystem.Gui && PeInfo.Read(cliExe) == Subsystem.Console, "PE reader tells GUI and console programs apart");

            watcher.Start();
            Check(ready.WaitOne(TimeSpan.FromSeconds(30)), "watcher started");

            o.WriteLine("Installing three fake portable packages (GUI app, console tool, zip with 4 programs)...");
            WriteArp(GuiKey, "PortaStart Self-Test App", guiExe, Path.GetDirectoryName(guiExe));
            WriteArp(CliKey, "PortaStart Self-Test Tool", cliExe, Path.GetDirectoryName(cliExe));
            WriteArp(ZipKey, "PortaStart Self-Test Viewer", viewerCli, zipDir);

            bool appeared = WaitFor(() => File.Exists(guiLnk) && File.Exists(zipLnk), 15);
            Check(appeared, "watcher created shortcuts after the install");
            Check(File.Exists(guiLnk) && Selector.SamePath(Shortcut.ReadTarget(guiLnk), guiExe), "GUI app shortcut points at its exe");
            Check(File.Exists(zipLnk) && Selector.SamePath(Shortcut.ReadTarget(zipLnk), Environment.Is64BitOperatingSystem ? viewer64 : Path.Combine(zipDir, "Viewer.exe")),
                  "zip package got one shortcut, for the main program matching this OS");
            Check(!File.Exists(cliLnk), "console tool got no shortcut");
            Check(!Directory.GetFiles(startDir, "PortaStart Self-Test Viewer*.lnk").Any(f => !Selector.SamePath(f, zipLnk)), "no shortcuts for the zip's uninstaller or helpers");

            o.WriteLine("Uninstalling the GUI app...");
            DeleteArp(GuiKey);
            Directory.Delete(Path.GetDirectoryName(guiExe), true);
            Check(WaitFor(() => !File.Exists(guiLnk), 15), "shortcut removed after uninstall");

            o.WriteLine("Deleting the Viewer shortcut by hand, then upgrading the Viewer package...");
            File.Delete(zipLnk);
            WriteArp(ZipKey, "PortaStart Self-Test Viewer", viewerCli, zipDir, "1.0.1"); // like a winget upgrade
            bool dismissed = WaitFor(() => State.Load().Any(s => s.PackageKey == ZipKey && s.Dismissed), 15);
            Check(dismissed, "watcher noticed the change and remembered the deletion");
            Check(!File.Exists(zipLnk), "a shortcut you delete stays deleted");
        }
        catch (Exception ex) { Check(false, "unexpected error: " + ex.Message); }
        finally
        {
            stop.Set();
            watcher.Join(TimeSpan.FromSeconds(10));
            foreach (var k in keys) DeleteArp(k);
            try { Syncer.Run(false, keys); } catch { }
            foreach (var l in new[] { guiLnk, cliLnk, zipLnk }) try { File.Delete(l); } catch { }
            try { State.Save(State.Load().Where(s => !keys.Contains(s.PackageKey))); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }

        lock (log) foreach (var l in log) o.WriteLine("    watcher: " + l);
        o.WriteLine(fails == 0 ? "Self-test passed. Everything it created has been removed." : $"Self-test FAILED ({fails} check(s)). Everything it created has been removed.");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>A copy of this exe with its PE subsystem set, standing in for a GUI or console program.</summary>
    private static string MakeExe(string source, string dest, Subsystem sub)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        var b = File.ReadAllBytes(source);
        int pe = BitConverter.ToInt32(b, 0x3C);
        int off = pe + 4 + 20 + 68;
        ushort v = sub == Subsystem.Gui ? (ushort)2 : (ushort)3;
        b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)(v >> 8);
        File.WriteAllBytes(dest, b);
        return dest;
    }

    private static void WriteArp(string key, string name, string target, string location, string version = "1.0.0")
    {
        using var k = Registry.CurrentUser.CreateSubKey(WingetRegistry.UninstallPath + "\\" + key);
        k.SetValue("DisplayName", name);
        k.SetValue("DisplayVersion", version);
        k.SetValue("Publisher", "PortaStart self-test");
        k.SetValue("InstallLocation", location);
        k.SetValue("TargetFullPath", target);
        k.SetValue("WinGetInstallerType", "portable");
        k.SetValue("WinGetPackageIdentifier", key.Substring(0, key.IndexOf("__", StringComparison.Ordinal)));
        k.SetValue("WinGetSourceIdentifier", "selftest");
        k.SetValue("UninstallString", "cmd /c echo PortaStart self-test entry, safe to delete");
        k.SetValue("SystemComponent", 1, RegistryValueKind.DWord); // keep it out of Settings > Apps while it exists
    }

    private static void DeleteArp(string key)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(WingetRegistry.UninstallPath + "\\" + key, false); } catch { }
    }

    private static bool WaitFor(Func<bool> cond, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (cond()) return true; Thread.Sleep(200); }
        return cond();
    }
}
