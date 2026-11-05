using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PortaStart;

internal sealed class Config
{
    public string Folder = "";                 // subfolder of Start menu\Programs; empty = top level (Windows guidelines)
    public HashSet<string> Exclude = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Console = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath => Path.Combine(Paths.DataDir, "config.ini");

    public static Config Load(string path = null)
    {
        var c = new Config();
        path ??= FilePath;
        if (!File.Exists(path)) return c;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
            if (k == "folder") c.Folder = v.Length == 0 ? "" : Selector.CleanName(v);
            else if (k == "exclude" && v.Length > 0) c.Exclude.Add(v);
            else if (k == "console" && v.Length > 0) c.Console.Add(v);
        }
        return c;
    }

    public void Save(string path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var sb = new StringBuilder();
        sb.AppendLine("# PortaStart settings. Edit by hand or with: PortaStart exclude|include|console|folder");
        sb.AppendLine("# folder  = subfolder of Start menu Programs for the shortcuts (empty = top level)");
        sb.AppendLine("# exclude = package id that never gets a shortcut");
        sb.AppendLine("# console = package id whose console programs get shortcuts too");
        sb.AppendLine("folder = " + Folder);
        foreach (var e in Exclude.OrderBy(x => x)) sb.AppendLine("exclude = " + e);
        foreach (var e in Console.OrderBy(x => x)) sb.AppendLine("console = " + e);
        File.WriteAllText(path, sb.ToString());
    }

    public string StartDir => Folder.Length == 0 ? Paths.UserPrograms : Path.Combine(Paths.UserPrograms, Folder);
}

internal static class State
{
    public static string FilePath => Path.Combine(Paths.DataDir, "shortcuts.tsv");

    public static List<Owned> Load(string path = null)
    {
        path ??= FilePath;
        var list = new List<Owned>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadAllLines(path))
        {
            var f = line.Split('\t');
            if (f.Length < 4 || line.StartsWith("#")) continue;
            list.Add(new Owned { LnkPath = f[0], PackageKey = f[1], ExePath = f[2], Dismissed = f[3] == "dismissed" });
        }
        return list;
    }

    public static void Save(IEnumerable<Owned> owned, string path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var sb = new StringBuilder("# Shortcuts PortaStart made. Only these are ever changed or removed.\n");
        foreach (var o in owned) sb.Append($"{o.LnkPath}\t{o.PackageKey}\t{o.ExePath}\t{(o.Dismissed ? "dismissed" : "active")}\n");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }
}

internal sealed class SyncResult
{
    public List<PortablePackage> Packages = new();
    public List<Wanted> Wanted = new();
    public List<PlanAction> Actions = new();
    public List<string> Errors = new();
    public int Changes => Actions.Count(a => a.Kind is ActionKind.Create or ActionKind.Update or ActionKind.Delete);
}

internal static class Syncer
{
    private const string MutexName = @"Local\PortaStart-sync-7c41";

    /// <param name="onlyKeys">Limit to these package keys (the self-test uses this so it never touches real apps).</param>
    public static SyncResult Run(bool dryRun, ICollection<string> onlyKeys = null)
    {
        using var mutex = new Mutex(false, MutexName);
        bool got;
        try { got = mutex.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { got = true; }
        try { return RunLocked(dryRun, onlyKeys); }
        finally { if (got) mutex.ReleaseMutex(); }
    }

    private static SyncResult RunLocked(bool dryRun, ICollection<string> onlyKeys)
    {
        var r = new SyncResult();
        var cfg = Config.Load();
        var allOwned = State.Load();
        bool InScope(string key) => onlyKeys == null || onlyKeys.Contains(key);
        var owned = allOwned.Where(o => InScope(o.PackageKey)).ToList();

        r.Packages = WingetRegistry.Read().Where(p => InScope(p.Key)).ToList();
        foreach (var p in r.Packages)
        {
            if (cfg.Exclude.Contains(p.PackageId) || cfg.Exclude.Contains(p.Key)) continue;
            bool console = cfg.Console.Contains(p.PackageId) || cfg.Console.Contains(p.Key);
            r.Wanted.AddRange(Selector.Choose(p, WingetRegistry.Files(p), console, Paths.OsMachine));
        }

        var world = new RealWorld(allOwned.Select(o => o.LnkPath));
        r.Actions = Planner.Plan(r.Wanted, owned, cfg.StartDir, world);
        if (dryRun) return r;

        var done = new List<PlanAction>();
        foreach (var a in r.Actions)
        {
            try
            {
                switch (a.Kind)
                {
                    case ActionKind.Create:
                    case ActionKind.Update:
                        var pkg = r.Packages.FirstOrDefault(p => p.Key == a.PackageKey);
                        Shortcut.Write(a.LnkPath, a.ExePath, $"{pkg?.DisplayName ?? a.PackageKey} (winget portable, shortcut by PortaStart)");
                        break;
                    case ActionKind.Delete:
                        File.Delete(a.LnkPath);
                        TryRemoveEmptyFolder(Path.GetDirectoryName(a.LnkPath));
                        break;
                }
                if (a.Kind != ActionKind.Skip) done.Add(a);
            }
            catch (Exception ex) { r.Errors.Add($"{a.Kind} {a.LnkPath}: {ex.Message}"); }
        }

        if (done.Count > 0)
        {
            var keep = allOwned.Where(o => !InScope(o.PackageKey)).ToList();
            State.Save(keep.Concat(Planner.Apply(owned, done)).OrderBy(o => o.LnkPath, StringComparer.OrdinalIgnoreCase));
        }
        return r;
    }

    private static void TryRemoveEmptyFolder(string dir)
    {
        try
        {
            if (!Selector.SamePath(dir, Paths.UserPrograms) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }

    /// <summary>Removes every shortcut PortaStart made (that still points where we left it) and forgets them.</summary>
    public static int RemoveAll(TextWriter log)
    {
        int n = 0;
        var owned = State.Load();
        foreach (var o in owned)
        {
            if (o.Dismissed || !File.Exists(o.LnkPath)) continue;
            var t = Shortcut.ReadTarget(o.LnkPath);
            if (t != null && !Selector.SamePath(t, o.ExePath)) { log.WriteLine($"  kept (you changed it): {o.LnkPath}"); continue; }
            try { File.Delete(o.LnkPath); n++; log.WriteLine($"  removed {o.LnkPath}"); TryRemoveEmptyFolder(Path.GetDirectoryName(o.LnkPath)); }
            catch (Exception ex) { log.WriteLine($"  could not remove {o.LnkPath}: {ex.Message}"); }
        }
        State.Save(new List<Owned>());
        return n;
    }
}
