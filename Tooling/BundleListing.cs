using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.DotNet.Tools.Uninstall.MacOs;
using Microsoft.DotNet.Tools.Uninstall.Shared.BundleInfo;
using Microsoft.DotNet.Tools.Uninstall.Shared.Utils;
using Microsoft.DotNet.Tools.Uninstall.Shared.VSVersioning;
using Microsoft.DotNet.Tools.Uninstall.Windows;

namespace DotNetUninstall.Tooling;

public record BundleInfoEntry(
    string Type,          // sdk | runtime
    string Version,
    string Architecture,
    bool CanUninstall,
    string? Reason,
    string DisplayName,
    string UninstallCommand
);

public static class BundleListing
{
    private static (IBundleCollector collector, bool supported) GetCollector()
    {
        if (OperatingSystem.IsWindows()) return (new RegistryQuery(), true);
        if (OperatingSystem.IsMacOS()) return (new FileSystemExplorer(), true);
        return (null!, false);
    }

    public static IReadOnlyList<BundleInfoEntry> List(bool macPreserveVsSdks = false)
    {
        var (collector, supported) = GetCollector();
        if (!supported) return Array.Empty<BundleInfoEntry>();

        var bundles = collector.GetAllInstalledBundles().ToList();
        var reasonMap = VisualStudioSafeVersionsExtractor.GetReasonRequiredStrings(bundles, macPreserveVsSdks);
        var uninstallable = VisualStudioSafeVersionsExtractor.GetUninstallableBundles(bundles, macPreserveVsSdks).ToHashSet();

        List<BundleInfoEntry> list = new();
        foreach (var b in bundles)
        {
            var version = b.Version.ToString();
            string type = b.Version.GetType().Name switch
            {
                var n when n.Contains("Sdk", StringComparison.OrdinalIgnoreCase) => "sdk",
                var n when n.Contains("Runtime", StringComparison.OrdinalIgnoreCase) => "runtime",
                _ => "runtime"
            };
            string arch = b.Arch.ToString().ToLowerInvariant();
            reasonMap.TryGetValue(b, out var reason);
            bool canUninstall = uninstallable.Contains(b) && string.IsNullOrEmpty(reason);
            list.Add(new BundleInfoEntry(type, version, arch, canUninstall, string.IsNullOrEmpty(reason) ? null : reason, b.DisplayName, b.UninstallCommand));
        }
        // Sort for deterministic ordering similar to CLI output: group by type then version descending
        return list
            .OrderBy(e => e.Type)
            .ThenByDescending(e => e.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool TryGetBundle(string type, string version, string architecture, out BundleInfoEntry? entry)
    {
        entry = List().FirstOrDefault(b => b.Type == type && b.Version == version && b.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase));
        return entry is not null;
    }

    public static (bool success, string? error) Uninstall(BundleInfoEntry entry)
    {
        try
        {
            if (!entry.CanUninstall)
            {
                return (false, entry.Reason ?? "Bundle marked as non-removable.");
            }
            if (OperatingSystem.IsWindows())
            {
                // Entry.UninstallCommand is an installer exe path followed by args.
                var parts = SplitCommand(entry.UninstallCommand).ToArray();
                if (parts.Length == 0) return (false, "Invalid uninstall command.");
                var psi = new ProcessStartInfo
                {
                    FileName = parts[0],
                    Arguments = string.Join(' ', parts.Skip(1)),
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (false, "Failed to start uninstall process.");
                proc.WaitForExit();
                if (proc.ExitCode != 0) return (false, $"Uninstaller exited with code {proc.ExitCode}");
                return (true, null);
            }
            else if (OperatingSystem.IsMacOS())
            {
                // Uninstall command is space separated list of directories to remove
                var targets = entry.UninstallCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var t in targets)
                {
                    if (System.IO.Directory.Exists(t))
                    {
                        try
                        {
                            System.IO.Directory.Delete(t, true);
                        }
                        catch (Exception ex)
                        {
                            return (false, $"Failed to delete {t}: {ex.Message}");
                        }
                    }
                }
                return (true, null);
            }
            else
            {
                return (false, "Unsupported OS for uninstall.");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static IEnumerable<string> SplitCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) yield break;
        // naive split respecting quoted segments
        var current = new List<char>();
        bool inQuotes = false;
        for (int i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Count > 0)
                {
                    yield return new string(current.ToArray());
                    current.Clear();
                }
            }
            else
            {
                current.Add(c);
            }
        }
        if (current.Count > 0) yield return new string(current.ToArray());
    }
}
