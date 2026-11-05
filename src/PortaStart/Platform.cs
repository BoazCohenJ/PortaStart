using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace PortaStart;

/// <summary>Finds portable installs in the Uninstall registry keys winget writes them to.</summary>
internal static class WingetRegistry
{
    public const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<PortablePackage> Read()
    {
        var list = new List<PortablePackage>();
        ReadHive(list, RegistryHive.CurrentUser, RegistryView.Default, false);
        if (Environment.Is64BitOperatingSystem)
        {
            ReadHive(list, RegistryHive.LocalMachine, RegistryView.Registry64, true);
            ReadHive(list, RegistryHive.LocalMachine, RegistryView.Registry32, true);
        }
        else ReadHive(list, RegistryHive.LocalMachine, RegistryView.Default, true);
        // Same key under both machine views is the same package.
        return list.GroupBy(p => (p.MachineScope ? "m|" : "u|") + p.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static void ReadHive(List<PortablePackage> list, RegistryHive hive, RegistryView view, bool machine)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var un = root.OpenSubKey(UninstallPath);
            if (un == null) return;
            foreach (var name in un.GetSubKeyNames())
            {
                try
                {
                    using var k = un.OpenSubKey(name);
                    if (k == null) continue;
                    if (!string.Equals(k.GetValue("WinGetInstallerType") as string, "portable", StringComparison.OrdinalIgnoreCase)) continue;
                    var loc = Expand(k.GetValue("InstallLocation") as string);
                    var p = new PortablePackage
                    {
                        Key = name,
                        PackageId = (k.GetValue("WinGetPackageIdentifier") as string) ?? name,
                        DisplayName = k.GetValue("DisplayName") as string,
                        TargetPath = Expand(k.GetValue("TargetFullPath") as string),
                        InstallLocation = loc,
                        MachineScope = machine,
                    };
                    // Zips: winget keeps a "<key>.db" index in the folder, and records no single target exe.
                    p.IsArchive = !string.IsNullOrEmpty(loc) && (File.Exists(Path.Combine(loc, name + ".db")) || string.IsNullOrEmpty(p.TargetPath));
                    list.Add(p);
                }
                catch { /* one unreadable key shouldn't hide the rest */ }
            }
        }
        catch { }
    }

    private static string Expand(string s) => string.IsNullOrEmpty(s) ? s : Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));

    /// <summary>Executables winget put in the package folder: the target, plus (for zips) the folder and one level below.</summary>
    public static List<ExeInfo> Files(PortablePackage p)
    {
        var paths = new List<string>();
        if (!string.IsNullOrEmpty(p.TargetPath) && File.Exists(p.TargetPath)) paths.Add(p.TargetPath);
        if (p.IsArchive && Directory.Exists(p.InstallLocation))
        {
            try
            {
                paths.AddRange(Directory.GetFiles(p.InstallLocation, "*.exe"));
                foreach (var d in Directory.GetDirectories(p.InstallLocation))
                    paths.AddRange(Directory.GetFiles(d, "*.exe"));
            }
            catch { }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(PeInfo.ReadInfo).ToList();
    }
}

/// <summary>Creates and reads .lnk files through the shell's own IShellLink.</summary>
internal static class Shortcut
{
    public static void Write(string lnkPath, string target, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target));
            link.SetIconLocation(target, 0);
            link.SetDescription(description);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    public static string ReadTarget(string lnkPath)
    {
        object link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            var s = sb.ToString();
            return s.Length == 0 ? null : Environment.ExpandEnvironmentVariables(s);
        }
        catch { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

/// <summary>The real file system and Start menu, as the planner sees them.</summary>
internal sealed class RealWorld : IWorld
{
    private readonly HashSet<string> _ours;
    private HashSet<string> _otherTargets;

    public RealWorld(IEnumerable<string> ourLnks) { _ours = new HashSet<string>(ourLnks, StringComparer.OrdinalIgnoreCase); }

    public bool LnkExists(string lnkPath) => File.Exists(lnkPath);
    public string LnkTarget(string lnkPath) => Shortcut.ReadTarget(lnkPath);

    public bool OtherShortcutTargets(string exePath)
    {
        if (_otherTargets == null)
        {
            _otherTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { Paths.UserPrograms, Paths.CommonPrograms })
            {
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> lnks;
                try { lnks = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).ToList(); }
                catch { continue; }
                foreach (var l in lnks)
                {
                    if (_ours.Contains(l)) continue;
                    var t = Shortcut.ReadTarget(l);
                    if (t != null) _otherTargets.Add(Selector.NormPath(t));
                }
            }
        }
        return _otherTargets.Contains(Selector.NormPath(exePath));
    }
}

internal static class Paths
{
    /// <summary>The CPU Windows itself runs on (an x64 process on ARM64 Windows still sees ARM64).</summary>
    public static ushort OsMachine
    {
        get
        {
            try
            {
                if (IsWow64Process2(System.Diagnostics.Process.GetCurrentProcess().Handle, out _, out ushort native) && native != 0) return native;
            }
            catch (EntryPointNotFoundException) { }
            return Environment.Is64BitOperatingSystem ? Arch.X64 : Arch.X86;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    public static string UserPrograms => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public static string CommonPrograms => Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortaStart");
}
