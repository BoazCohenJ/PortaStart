using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PortaStart;

internal static class Program
{
    public static string Version
    {
        get { var v = Assembly.GetExecutingAssembly().GetName().Version; return $"{v.Major}.{v.Minor}.{v.Build}"; }
    }

    private static string Exe => Process.GetCurrentProcess().MainModule.FileName;

    [STAThread]
    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].TrimStart('-', '/').ToLowerInvariant() : "";
        string arg = args.Length > 1 ? string.Join(" ", args.Skip(1)) : null;
        bool doubleClicked = LaunchedFromExplorer();
        int rc;
        try
        {
            rc = cmd switch
            {
                "" => Setup(doubleClicked),
                "sync" => Sync(dryRun: args.Any(a => a == "--dry-run")),
                "list" or "status" => List(),
                "watch" => Watcher.Run(Watcher.FileLog),
                "autostart" => AutostartCmd(arg),
                "exclude" or "include" or "console" or "noconsole" => Rule(cmd, arg),
                "folder" => Folder(arg),
                "remove-all" or "uninstall" => RemoveAll(),
                "self-test" or "selftest" => SelfTest.Run(Console.Out),
                "version" or "v" => Print("PortaStart " + Version),
                _ => Help(cmd is "help" or "h" or "?" ? 0 : 2),
            };
        }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); rc = 1; }

        if (doubleClicked && cmd == "")
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to close.");
            try { Console.ReadKey(true); } catch { }
        }
        return rc;
    }

    /// <summary>No arguments: sync now and turn on the background watcher, so new installs get shortcuts by themselves.</summary>
    private static int Setup(bool doubleClicked)
    {
        Console.WriteLine($"PortaStart {Version}: Start menu shortcuts for portable apps installed with winget");
        Console.WriteLine();
        int rc = Sync(false);
        Console.WriteLine();
        if (Autostart.Current() == null || !Watcher.IsRunning())
        {
            Autostart.Enable(Exe);
            if (!Watcher.IsRunning()) Autostart.StartNow(Exe);
            Console.WriteLine("Watching for winget installs, upgrades and removals in the background (starts at sign-in, no window).");
        }
        else Console.WriteLine("The background watcher is already running.");
        Console.WriteLine("Turn it off with: PortaStart autostart off   |   Undo everything: PortaStart remove-all");
        if (IsTempPath(Exe))
            Console.WriteLine("Note: PortaStart is running from a temporary folder. Move it somewhere permanent (or install it with winget) and run it again, so sign-in can find it.");
        return rc;
    }

    private static int Sync(bool dryRun)
    {
        var r = Syncer.Run(dryRun);
        var shown = r.Actions.Where(a => a.Kind != ActionKind.Forget).ToList();
        string verb(ActionKind k) => (dryRun ? k switch { ActionKind.Create => "would create", ActionKind.Update => "would update", ActionKind.Delete => "would remove", _ => k.ToString().ToLowerInvariant() }
                                              : k switch { ActionKind.Create => "created", ActionKind.Update => "updated", ActionKind.Delete => "removed", ActionKind.Dismiss => "left deleted", _ => "skipped" });
        Console.WriteLine($"Found {r.Packages.Count} portable package(s) from winget.");
        foreach (var a in shown)
            Console.WriteLine($"  {verb(a.Kind),-13} {System.IO.Path.GetFileNameWithoutExtension(a.LnkPath)}  ({a.Reason})");
        foreach (var e in r.Errors) Console.WriteLine("  error: " + e);
        if (r.Changes == 0 && r.Errors.Count == 0) Console.WriteLine("  Start menu is up to date.");
        return r.Errors.Count == 0 ? 0 : 1;
    }

    private static int List()
    {
        var cfg = Config.Load();
        var owned = State.Load();
        var pkgs = WingetRegistry.Read().OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"Shortcut folder: {cfg.StartDir}");
        Console.WriteLine($"Background watcher: {(Watcher.IsRunning() ? "running" : "not running")}, start at sign-in: {(Autostart.Current() != null ? "on" : "off")}");
        Console.WriteLine();
        if (pkgs.Count == 0) { Console.WriteLine("No portable packages installed with winget."); return 0; }
        foreach (var p in pkgs)
        {
            var files = WingetRegistry.Files(p);
            bool console = cfg.Console.Contains(p.PackageId);
            var chosen = Selector.Choose(p, files, console, Paths.OsMachine);
            string status;
            if (cfg.Exclude.Contains(p.PackageId)) status = "excluded";
            else if (chosen.Count == 0)
                status = files.Count == 0 ? "no program found" : files.All(f => f.Subsystem == Subsystem.Console) ? "console tool, no shortcut (opt in: PortaStart console " + p.PackageId + ")" : "no suitable program";
            else status = string.Join("; ", chosen.Select(c =>
            {
                var o = owned.FirstOrDefault(x => x.PackageKey == p.Key && Selector.SamePath(x.ExePath, c.ExePath));
                string state = o == null ? "not created yet" : o.Dismissed ? "you deleted it" : "shortcut";
                return $"{c.Name} -> {System.IO.Path.GetFileName(c.ExePath)} [{state}]";
            }));
            Console.WriteLine($"{p.PackageId}{(p.MachineScope ? " (machine)" : "")}");
            Console.WriteLine($"    {status}");
        }
        return 0;
    }

    private static int AutostartCmd(string arg)
    {
        switch ((arg ?? "status").Trim().ToLowerInvariant())
        {
            case "on":
                Autostart.Enable(Exe);
                if (!Watcher.IsRunning()) Autostart.StartNow(Exe);
                return Print("Background watcher on: starts at sign-in, running now.");
            case "off":
                Autostart.Disable();
                Watcher.RequestStop();
                return Print("Background watcher off. Run `PortaStart sync` after winget installs, or turn it back on with `PortaStart autostart on`.");
            default:
                return Print($"Start at sign-in: {(Autostart.Current() != null ? "on" : "off")}; running now: {(Watcher.IsRunning() ? "yes" : "no")}");
        }
    }

    private static int Rule(string cmd, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Print($"Usage: PortaStart {cmd} <PackageId>   (see ids with: PortaStart list)", 2);
        id = id.Trim();
        var cfg = Config.Load();
        switch (cmd)
        {
            case "exclude": cfg.Exclude.Add(id); break;
            case "include":
                cfg.Exclude.Remove(id);
                // Also bring back a shortcut the user had deleted.
                var keys = WingetRegistry.Read().Where(p => p.PackageId.Equals(id, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList();
                State.Save(State.Load().Where(o => !(o.Dismissed && keys.Contains(o.PackageKey, StringComparer.OrdinalIgnoreCase))));
                break;
            case "console": cfg.Console.Add(id); break;
            case "noconsole": cfg.Console.Remove(id); break;
        }
        cfg.Save();
        return Sync(false);
    }

    private static int Folder(string name)
    {
        var cfg = Config.Load();
        if (name == null) return Print($"Shortcuts go to: {cfg.StartDir}\nChange with: PortaStart folder \"WinGet Apps\"   (or PortaStart folder \"\" for the top level)");
        name = name.Trim().Trim('"');
        cfg.Folder = name.Length == 0 ? "" : Selector.CleanName(name);
        cfg.Save();
        Console.WriteLine($"Shortcuts now go to: {cfg.StartDir}");
        return Sync(false);
    }

    private static int RemoveAll()
    {
        Autostart.Disable();
        Watcher.RequestStop();
        int n = Syncer.RemoveAll(Console.Out);
        return Print($"Removed {n} shortcut(s) and turned off the background watcher. Settings remain in {Paths.DataDir}.");
    }

    private static int Help(int rc)
    {
        Console.WriteLine($@"PortaStart {Version}
Start menu shortcuts for portable apps installed with winget (winget-cli issue #2299).

  PortaStart                 Sync now and keep watching in the background (starts at sign-in, no window)
  PortaStart sync [--dry-run]  Create, update and remove shortcuts once
  PortaStart list            Show winget portable packages and their shortcuts
  PortaStart exclude <id>    Never make a shortcut for this package (removes it if present)
  PortaStart include <id>    Undo exclude, and bring back a shortcut you deleted
  PortaStart console <id>    Also make shortcuts for this package's console programs
  PortaStart folder [name]   Put shortcuts in a Start menu folder (default: top level)
  PortaStart autostart on|off  Turn the background watcher on or off
  PortaStart remove-all      Remove every shortcut PortaStart made and stop watching
  PortaStart self-test       Fake some winget installs and check that everything works
  PortaStart version

Only GUI programs get shortcuts. Shortcuts PortaStart did not make are never touched,
and a shortcut you delete stays deleted.");
        return rc;
    }

    private static int Print(string s, int rc = 0) { Console.WriteLine(s); return rc; }

    private static bool IsTempPath(string p) =>
        p.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) || p.IndexOf(@"\Temp\", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>True when this process owns its console window, i.e. it was double-clicked rather than run from a terminal.</summary>
    private static bool LaunchedFromExplorer()
    {
        try { return GetConsoleProcessList(new uint[2], 2) == 1; } catch { return false; }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] list, uint count);
}
