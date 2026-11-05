using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PortaStart;

// Pure decision logic: which executables get a shortcut, and what to create, update or delete.
// Nothing in this file touches the registry, the file system or COM, so it is unit tested directly.

internal enum Subsystem { Unknown, Gui, Console }

/// <summary>A portable package as winget recorded it in the Uninstall registry.</summary>
internal sealed class PortablePackage
{
    public string Key;             // ARP subkey ("product code"), e.g. "Foo.Bar__DefaultSource"
    public string PackageId;       // WinGetPackageIdentifier, falls back to Key
    public string DisplayName;
    public string TargetPath;      // TargetFullPath: the exe winget linked as the command
    public string InstallLocation;
    public bool IsArchive;         // InstallLocation holds winget's "<Key>.db" index: files came from a zip
    public bool MachineScope;
}

internal sealed class ExeInfo
{
    public string Path;
    public Subsystem Subsystem;
    public ushort Machine;         // PE machine type: Arch.X86, Arch.X64, Arch.Arm64, or 0 when unknown
    public ExeInfo(string path, Subsystem subsystem, ushort machine = 0) { Path = path; Subsystem = subsystem; Machine = machine; }
}

internal static class Arch
{
    public const ushort X86 = 0x014C, X64 = 0x8664, Arm64 = 0xAA64;

    /// <summary>How well a program built for <paramref name="exe"/> fits a Windows running on <paramref name="os"/>; below 0 = can't run.</summary>
    public static int Rank(ushort exe, ushort os) => os switch
    {
        Arm64 => exe switch { Arm64 => 4, X64 => 3, X86 => 2, _ => 1 },
        X64 => exe switch { X64 => 3, X86 => 2, Arm64 => -1, _ => 1 },
        _ => exe switch { X86 => 3, X64 => -1, Arm64 => -1, _ => 1 },
    };
}

internal sealed class Wanted
{
    public string PackageKey;
    public string PackageId;
    public string ExePath;
    public string Name;            // shortcut file name without ".lnk"
}

internal sealed class Owned
{
    public string LnkPath;
    public string PackageKey;
    public string ExePath;
    public bool Dismissed;         // the user deleted our shortcut: never recreate it while the package stays
}

internal enum ActionKind { Create, Update, Delete, Forget, Dismiss, Skip }

internal sealed class PlanAction
{
    public ActionKind Kind;
    public string LnkPath;
    public string ExePath;
    public string PackageKey;
    public string Reason;
    public override string ToString() => $"{Kind} {LnkPath} -> {ExePath} ({Reason})";
}

internal static class Selector
{
    // Helpers that ship next to the real app in portable zips. Never the thing you want in Start.
    private static readonly string[] Junk =
    {
        "unins", "uninst", "uninstall", "update", "updater", "autoupdate", "crashpad", "crashpad_handler", "crashreporter", "crashhandler",
        "crash_reporter", "bugreport", "setup", "install", "installer", "vc_redist", "vcredist", "elevate", "helper",
        "launcherhelper", "notification_helper", "createdump", "regasm", "7za", "wget", "curl", "ffmpeg", "ffprobe",
    };

    public static bool IsJunkName(string path)
    {
        string n = Normalize(Path.GetFileNameWithoutExtension(path));
        return Junk.Any(j => n == Normalize(j) || (n.StartsWith(Normalize(j)) && j.Length >= 5 && n.Length <= j.Length + 3));
    }

    /// <summary>
    /// Picks the executables of one package that deserve a Start menu shortcut.
    /// Single-file packages: the target winget installed, if it is a GUI program (or console programs were opted in).
    /// Archive packages: the GUI programs in the folder, narrowed to the one that matches the package name.
    /// </summary>
    public static List<Wanted> Choose(PortablePackage pkg, IList<ExeInfo> files, bool includeConsole, ushort osMachine)
    {
        var result = new List<Wanted>();
        bool Allowed(ExeInfo e) => e.Subsystem == Subsystem.Gui || (includeConsole && e.Subsystem == Subsystem.Console);
        string name = CleanName(string.IsNullOrWhiteSpace(pkg.DisplayName) ? pkg.PackageId : pkg.DisplayName);

        var target = string.IsNullOrEmpty(pkg.TargetPath) ? null
            : files.FirstOrDefault(f => SamePath(f.Path, pkg.TargetPath));

        if (!pkg.IsArchive)
        {
            if (target != null && Allowed(target))
                result.Add(Make(pkg, target.Path, name));
            return result;
        }

        // Archive: winget's target is just one of the commands it put on PATH. Look at every program in the folder.
        bool IsTarget(ExeInfo f) => target != null && SamePath(f.Path, target.Path);
        var pool = files.Where(f => Allowed(f) && Arch.Rank(f.Machine, osMachine) >= 0 && (IsTarget(f) || !IsJunkName(f.Path)))
                        .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (pool.Count == 0) return result;
        pool = CollapseArchitectures(pool, osMachine);
        if (pool.Count == 1) { result.Add(Make(pkg, pool[0].Path, name)); return result; }

        // Several programs: keep the one(s) named like the package.
        var keys = new[] { Normalize(pkg.DisplayName), Normalize(LastSegment(pkg.PackageId)) }.Where(k => k.Length > 0).ToArray();
        var scored = pool.Select(f => (f, s: Score(BaseKey(f.Path), keys))).ToList();
        int best = scored.Max(x => x.s);
        var top = scored.Where(x => x.s == best).Select(x => x.f).ToList();
        if (best > 0 && top.Count > 1 && top.Any(IsTarget)) top = top.Where(IsTarget).ToList();
        if (best > 0 && top.Count == 1) { result.Add(Make(pkg, top[0].Path, name)); return result; }

        // No clear main program: one shortcut each (bounded), labelled with the program's own name.
        var chosen = best > 0 ? top : pool;
        foreach (var f in chosen.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(5))
            result.Add(Make(pkg, f.Path, CleanName(name + " - " + Path.GetFileNameWithoutExtension(f.Path))));
        return result;
    }

    /// <summary>3 = exe name equals the package name, 2 = one contains the other, 1 = abbreviation ("procexp" for "Process Explorer").</summary>
    internal static int Score(string exe, string[] keys)
    {
        if (exe.Length == 0) return 0;
        int s = 0;
        foreach (var k in keys)
        {
            if (k == exe) s = Math.Max(s, 3);
            else if (exe.Length >= 3 && k.Contains(exe)) s = Math.Max(s, 2);
            else if (k.Length >= 3 && exe.Contains(k)) s = Math.Max(s, 2);
            else if (exe.Length >= 3 && k.Length > 0 && exe[0] == k[0] && IsSubsequence(exe, k)) s = Math.Max(s, 1);
        }
        return s;
    }

    private static bool IsSubsequence(string small, string big)
    {
        int i = 0;
        foreach (char c in big) if (i < small.Length && small[i] == c) i++;
        return i == small.Length;
    }

    /// <summary>
    /// The same program built for several CPUs ("app.exe", "app64.exe", "app64a.exe", or x86 and x64 folders):
    /// keep the build that runs best on this Windows. Uses the real machine type from the exe; names only break ties.
    /// </summary>
    internal static List<ExeInfo> CollapseArchitectures(List<ExeInfo> pool, ushort osMachine)
    {
        var outList = new List<ExeInfo>();
        bool os64 = osMachine != Arch.X86;
        foreach (var g in pool.GroupBy(f => BaseKey(f.Path)))
        {
            var items = g.ToList();
            if (items.Count == 1) { outList.Add(items[0]); continue; }
            int bestRank = items.Max(i => Arch.Rank(i.Machine, osMachine));
            items = items.Where(i => Arch.Rank(i.Machine, osMachine) == bestRank).ToList();
            if (items.Count > 1)
            {
                // Machine type unknown or equal: fall back to the name ("64" in it, or not).
                var byName = items.Where(i => LooksArch64(Path.GetFileNameWithoutExtension(i.Path)) == os64).ToList();
                if (byName.Count > 0) items = byName;
            }
            // Same file name in several folders with nothing to tell them apart: one is enough.
            if (items.Select(i => Path.GetFileName(i.Path).ToLowerInvariant()).Distinct().Count() == 1) items = items.Take(1).ToList();
            outList.AddRange(items);
        }
        return outList;
    }

    // Longest first, so "app_arm64" loses "_arm64", not just "64".
    private static readonly string[] ArchSuffixes =
    {
        "_x86_64", "-x86_64", "_arm64", "-arm64", ".arm64", "arm64", "_amd64", "-amd64", "amd64", "_x64", "-x64", ".x64", "x64",
        "_x86", "-x86", ".x86", "x86", "64a", "a64", "_64", "-64", "64", "_32", "-32", "32",
    };

    private static bool LooksArch64(string n) =>
        ArchSuffixes.Where(s => s.Contains("64")).Any(s => n.EndsWith(s, StringComparison.OrdinalIgnoreCase) && n.Length > s.Length);

    internal static string StripBitness(string n)
    {
        foreach (var s in ArchSuffixes)
            if (n.EndsWith(s, StringComparison.OrdinalIgnoreCase) && n.Length > s.Length) return n.Substring(0, n.Length - s.Length);
        return n;
    }

    /// <summary>Normalized exe name without its architecture suffix: "procexp64a.exe" and "procexp.exe" both give "procexp".</summary>
    internal static string BaseKey(string path) => Normalize(StripBitness(Path.GetFileNameWithoutExtension(path)));

    private static Wanted Make(PortablePackage p, string exe, string name) =>
        new Wanted { PackageKey = p.Key, PackageId = p.PackageId, ExePath = exe, Name = name };

    internal static string LastSegment(string id) => string.IsNullOrEmpty(id) ? "" : id.Substring(id.LastIndexOf('.') + 1);

    internal static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>A display name made safe as a file name.</summary>
    internal static string CleanName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "App";
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(bad.Contains(c) ? ' ' : c);
        string r = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (r.Length > 80) r = r.Substring(0, 80).TrimEnd('.', ' ');
        return r.Length == 0 ? "App" : r;
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(NormPath(a), NormPath(b), StringComparison.OrdinalIgnoreCase);

    internal static string NormPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        try { p = Path.GetFullPath(p.Trim().Trim('"')); } catch { }
        return p.TrimEnd('\\');
    }
}

/// <summary>What the planner can ask about the outside world.</summary>
internal interface IWorld
{
    bool LnkExists(string lnkPath);
    /// <summary>Target of a shortcut, or null when it can't be read.</summary>
    string LnkTarget(string lnkPath);
    /// <summary>True when some other Start menu shortcut (not one of ours) already launches this exe.</summary>
    bool OtherShortcutTargets(string exePath);
}

internal static class Planner
{
    public static List<PlanAction> Plan(IList<Wanted> wanted, IList<Owned> owned, string startDir, IWorld world)
    {
        var actions = new List<PlanAction>();
        var wantByLnk = new Dictionary<string, Wanted>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in wanted)
        {
            string lnk = Path.Combine(startDir, w.Name + ".lnk");
            // Two packages with the same display name: second one gets its id appended.
            if (wantByLnk.ContainsKey(lnk)) lnk = Path.Combine(startDir, Selector.CleanName(w.Name + " (" + w.PackageId + ")") + ".lnk");
            if (!wantByLnk.ContainsKey(lnk)) wantByLnk[lnk] = w;
        }

        var ownedLnks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in owned)
        {
            ownedLnks.Add(o.LnkPath);
            if (wantByLnk.TryGetValue(o.LnkPath, out var w) && string.Equals(w.PackageKey, o.PackageKey, StringComparison.OrdinalIgnoreCase))
            {
                if (o.Dismissed) continue;
                if (!world.LnkExists(o.LnkPath))
                {
                    actions.Add(new PlanAction { Kind = ActionKind.Dismiss, LnkPath = o.LnkPath, ExePath = o.ExePath, PackageKey = o.PackageKey, Reason = "you deleted this shortcut, so it stays deleted" });
                    continue;
                }
                string t = world.LnkTarget(o.LnkPath);
                if (t == null || !Selector.SamePath(t, w.ExePath))
                {
                    // Only re-point it if it still points where we left it; a shortcut the user edited is theirs.
                    if (t == null || Selector.SamePath(t, o.ExePath))
                        actions.Add(new PlanAction { Kind = ActionKind.Update, LnkPath = o.LnkPath, ExePath = w.ExePath, PackageKey = w.PackageKey, Reason = "program moved" });
                    else
                        actions.Add(new PlanAction { Kind = ActionKind.Forget, LnkPath = o.LnkPath, ExePath = o.ExePath, PackageKey = o.PackageKey, Reason = "you changed this shortcut, leaving it alone" });
                }
                continue;
            }

            // Package uninstalled, excluded, renamed, or its shortcut folder changed.
            if (!o.Dismissed && world.LnkExists(o.LnkPath))
            {
                string t = world.LnkTarget(o.LnkPath);
                if (t == null || Selector.SamePath(t, o.ExePath))
                {
                    actions.Add(new PlanAction { Kind = ActionKind.Delete, LnkPath = o.LnkPath, ExePath = o.ExePath, PackageKey = o.PackageKey, Reason = "package removed or excluded" });
                    continue;
                }
            }
            actions.Add(new PlanAction { Kind = ActionKind.Forget, LnkPath = o.LnkPath, ExePath = o.ExePath, PackageKey = o.PackageKey, Reason = "no longer tracked" });
        }

        foreach (var kv in wantByLnk)
        {
            if (ownedLnks.Contains(kv.Key)) continue;
            var w = kv.Value;
            if (world.OtherShortcutTargets(w.ExePath))
                actions.Add(new PlanAction { Kind = ActionKind.Skip, LnkPath = kv.Key, ExePath = w.ExePath, PackageKey = w.PackageKey, Reason = "already has a Start menu shortcut" });
            else if (world.LnkExists(kv.Key))
                actions.Add(new PlanAction { Kind = ActionKind.Skip, LnkPath = kv.Key, ExePath = w.ExePath, PackageKey = w.PackageKey, Reason = "a different shortcut with this name exists" });
            else
                actions.Add(new PlanAction { Kind = ActionKind.Create, LnkPath = kv.Key, ExePath = w.ExePath, PackageKey = w.PackageKey, Reason = "new portable app" });
        }
        return actions;
    }

    /// <summary>State after applying the actions that succeeded.</summary>
    public static List<Owned> Apply(IList<Owned> owned, IEnumerable<PlanAction> done)
    {
        var map = owned.ToDictionary(o => o.LnkPath, o => o, StringComparer.OrdinalIgnoreCase);
        foreach (var a in done)
        {
            switch (a.Kind)
            {
                case ActionKind.Create:
                case ActionKind.Update:
                    map[a.LnkPath] = new Owned { LnkPath = a.LnkPath, PackageKey = a.PackageKey, ExePath = a.ExePath };
                    break;
                case ActionKind.Delete:
                case ActionKind.Forget:
                    map.Remove(a.LnkPath);
                    break;
                case ActionKind.Dismiss:
                    if (map.TryGetValue(a.LnkPath, out var o)) o.Dismissed = true;
                    break;
            }
        }
        return map.Values.OrderBy(o => o.LnkPath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Reads the Windows PE header to tell GUI programs from console programs.</summary>
internal static class PeInfo
{
    public static Subsystem Read(string path) => ReadInfo(path).Subsystem;

    public static ExeInfo ReadInfo(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[4096];
            int n = fs.Read(buf, 0, buf.Length);
            return new ExeInfo(path, Parse(buf, n), Machine(buf, n));
        }
        catch { return new ExeInfo(path, Subsystem.Unknown); }
    }

    public static ushort Machine(byte[] b, int n)
    {
        if (n < 0x40 || b[0] != 'M' || b[1] != 'Z') return 0;
        int pe = BitConverter.ToInt32(b, 0x3C);
        if (pe <= 0 || pe + 6 > n) return 0;
        return BitConverter.ToUInt16(b, pe + 4);
    }

    public static Subsystem Parse(byte[] b, int n)
    {
        if (n < 0x40 || b[0] != 'M' || b[1] != 'Z') return Subsystem.Unknown;
        int pe = BitConverter.ToInt32(b, 0x3C);
        int sub = pe + 4 + 20 + 68; // signature, COFF header, then Subsystem sits at offset 68 in both PE32 and PE32+
        if (pe <= 0 || sub + 2 > n) return Subsystem.Unknown;
        if (b[pe] != 'P' || b[pe + 1] != 'E' || b[pe + 2] != 0 || b[pe + 3] != 0) return Subsystem.Unknown;
        ushort characteristics = BitConverter.ToUInt16(b, pe + 4 + 18);
        if ((characteristics & 0x2000) != 0) return Subsystem.Unknown; // a DLL renamed .exe
        return BitConverter.ToUInt16(b, sub) switch { 2 => Subsystem.Gui, 3 => Subsystem.Console, _ => Subsystem.Unknown };
    }
}
