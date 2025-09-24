namespace DotNetUninstall.Models;

public partial record DotnetInstallEntry(
    string Type,          // sdk | runtime
    string Id,            // identifier category (sdk/runtime)
    string Version,       // version string
    string Architecture,  // arm64/x64/etc.
    bool CanUninstall,
    string? Reason        // null if uninstallable, else reason message
);
