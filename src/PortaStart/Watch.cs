using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PortaStart;

/// <summary>
/// Background mode. Every few seconds it compares the last-write timestamps of the Uninstall keys where winget
/// records portable installs (and of each portable package's own key). When they change, it waits for winget to
/// finish writing, then syncs.
/// Why not RegNotifyChangeKeyValue: winget runs as a packaged (MSIX) app, and its registry writes don't fire change
/// notifications for other processes (tested with winget 1.x on Windows 11). The timestamps do change.
/// </summary>
internal static class Watcher
{
    public const string MutexName = @"Local\PortaStart-watch-7c41";
    public const string StopEventName = @"Local\PortaStart-watch-stop-7c41";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Safety = TimeSpan.FromMinutes(30);

    public static bool IsRunning()
    {
        try { using var m = Mutex.OpenExisting(MutexName); return true; }
        catch { return false; }
    }

    public static void RequestStop()
    {
        try { using var e = EventWaitHandle.OpenExisting(StopEventName); e.Set(); } catch { }
    }

    /// <param name="onlyKeys">Self-test scope; null in normal use.</param>
    /// <param name="ready">Set once the first sync is done and watching has begun.</param>
    public static int Run(Action<string> log, ICollection<string> onlyKeys = null, WaitHandle stop = null, EventWaitHandle ready = null)
    {
        using var mutex = new Mutex(true, onlyKeys == null ? MutexName : MutexName + "-test", out bool first);
        if (!first) { log("PortaStart is already watching."); return 0; }
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, onlyKeys == null ? StopEventName : StopEventName + "-test");
        var stops = stop == null ? new WaitHandle[] { stopEvent } : new WaitHandle[] { stopEvent, stop };
        bool Stopped(TimeSpan wait) => WaitHandle.WaitAny(stops, wait) != WaitHandle.WaitTimeout;

        var roots = Roots();
        var known = DoSync(log, onlyKeys, "start");
        string last = Fingerprint(roots, known);
        var lastSync = DateTime.UtcNow;
        ready?.Set();

        while (true)
        {
            if (Stopped(Poll)) { log("stopping"); return 0; }
            string now = Fingerprint(roots, known);
            bool changed = now != last;
            if (!changed && DateTime.UtcNow - lastSync < Safety) continue;
            if (changed)
            {
                // winget writes several values, sometimes across keys; wait until it has been quiet for a moment.
                while (true)
                {
                    if (Stopped(Settle)) { log("stopping"); return 0; }
                    string again = Fingerprint(roots, known);
                    if (again == now) break;
                    now = again;
                }
            }
            known = DoSync(log, onlyKeys, changed ? "change" : "periodic");
            last = Fingerprint(roots, known);
            lastSync = DateTime.UtcNow;
        }
    }

    /// <returns>The portable packages found, whose own keys are then watched for upgrades.</returns>
    private static List<PortablePackage> DoSync(Action<string> log, ICollection<string> onlyKeys, string why)
    {
        try
        {
            var r = Syncer.Run(false, onlyKeys);
            if (why == "change") log($"change: {r.Packages.Count} portable package(s), {r.Changes} shortcut change(s)");
            foreach (var a in r.Actions.Where(a => a.Kind != ActionKind.Skip || why == "start"))
                log($"{why}: {a.Kind.ToString().ToLowerInvariant()} {a.LnkPath} ({a.Reason})");
            foreach (var e in r.Errors) log("error: " + e);
            return r.Packages;
        }
        catch (Exception ex) { log("sync failed: " + ex.Message); return new List<PortablePackage>(); }
    }

    private sealed class Root
    {
        public RegistryHive Hive;
        public RegistryView View;
        public bool Machine => Hive == RegistryHive.LocalMachine;
    }

    // Opened afresh on every poll: on a new profile the per-user Uninstall key doesn't exist until the first install.
    private static List<Root> Roots()
    {
        var list = new List<Root> { new() { Hive = RegistryHive.CurrentUser, View = RegistryView.Default } };
        if (Environment.Is64BitOperatingSystem)
        {
            list.Add(new Root { Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64 });
            list.Add(new Root { Hive = RegistryHive.LocalMachine, View = RegistryView.Registry32 });
        }
        else list.Add(new Root { Hive = RegistryHive.LocalMachine, View = RegistryView.Default });
        return list;
    }

    /// <summary>
    /// Last-write time and subkey count of each Uninstall key (changes when a package is added or removed), plus the
    /// last-write time of each known portable package's key (changes when winget upgrades it). A few cheap registry
    /// reads, no matter how many programs are installed.
    /// </summary>
    private static string Fingerprint(List<Root> roots, List<PortablePackage> known)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in roots)
        {
            try { AppendRoot(sb, r, known); }
            catch { sb.Append("err|"); }
        }
        return sb.ToString();
    }

    private static void AppendRoot(System.Text.StringBuilder sb, Root r, List<PortablePackage> known)
    {
        using var baseKey = RegistryKey.OpenBaseKey(r.Hive, r.View);
        using var un = baseKey.OpenSubKey(WingetRegistry.UninstallPath);
        if (un == null) { sb.Append("none|"); return; }
        Stamp(un, out long t, out int n);
        sb.Append(t).Append('/').Append(n).Append(';');
        foreach (var p in known.Where(p => p.MachineScope == r.Machine))
        {
            try
            {
                using var k = un.OpenSubKey(p.Key);
                if (k == null) { sb.Append('-'); continue; }
                Stamp(k, out long pt, out _);
                sb.Append(pt).Append(',');
            }
            catch { sb.Append('!'); }
        }
        sb.Append('|');
    }

    private static void Stamp(RegistryKey k, out long lastWrite, out int subKeys)
    {
        lastWrite = 0; subKeys = 0;
        try { RegQueryInfoKey(k.Handle, null, IntPtr.Zero, IntPtr.Zero, out subKeys, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out lastWrite); }
        catch { }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(SafeRegistryHandle hKey, System.Text.StringBuilder lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        out int lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues, IntPtr lpcbMaxValueNameLen,
        IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    /// <summary>Appends to %LOCALAPPDATA%\PortaStart\watch.log, keeping it small.</summary>
    public static void FileLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            string path = Path.Combine(Paths.DataDir, "watch.log");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 256 * 1024)
            {
                File.Delete(path + ".old");
                File.Move(path, path + ".old");
            }
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { }
    }
}

/// <summary>Starts the watcher at sign-in through the user's Run key, with no window.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PortaStart";

    private static string Conhost => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");

    // conhost --headless runs a console program with no window at all (and never hands it to Windows Terminal).
    public static string Command(string exe) => $"\"{Conhost}\" --headless \"{exe}\" watch";

    public static string Current()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) as string;
    }

    public static void Enable(string exe)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue(ValueName, Command(exe));
    }

    public static bool Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k?.GetValue(ValueName) == null) return false;
        k.DeleteValue(ValueName);
        return true;
    }

    public static void StartNow(string exe)
    {
        var psi = new ProcessStartInfo(Conhost, $"--headless \"{exe}\" watch") { UseShellExecute = false, CreateNoWindow = true };
        Process.Start(psi)?.Dispose();
    }
}
