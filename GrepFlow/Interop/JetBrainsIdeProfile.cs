using System.Collections.Frozen;

namespace GrepFlow.Interop;

internal sealed record JetBrainsIdeProfile
{
    public JetBrainsIdeProfile(
        string sourceId,
        string displayName,
        IEnumerable<string> executableFileNames,
        string vendorDirectory,
        string stateDirectoryPrefix)
    {
        SourceId = sourceId;
        DisplayName = displayName;
        ExecutableFileNames = executableFileNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        VendorDirectory = vendorDirectory;
        StateDirectoryPrefix = stateDirectoryPrefix;
    }

    public string SourceId { get; }

    public string DisplayName { get; }

    public IReadOnlySet<string> ExecutableFileNames { get; }

    public string VendorDirectory { get; }

    public string StateDirectoryPrefix { get; }
}
