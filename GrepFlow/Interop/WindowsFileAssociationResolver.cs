using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GrepFlow.Interop;

public sealed class WindowsFileAssociationResolver : IFileAssociationResolver
{
    private readonly IAssociationExecutableQuery _query;

    public WindowsFileAssociationResolver(IAssociationExecutableQuery query)
    {
        _query = query;
    }

    public string? ResolveDefaultExecutable(string filePath)
    {
        try
        {
            var extension = Path.GetExtension(filePath);
            return string.IsNullOrWhiteSpace(extension) ? null : _query.Query(extension);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class WindowsAssociationExecutableQuery : IAssociationExecutableQuery
{
    private const uint AssocStringExecutable = 2;

    public string? Query(string extension)
    {
        try
        {
            uint length = 0;
            _ = AssocQueryString(0, AssocStringExecutable, extension, null, null, ref length);
            if (length == 0 || length > int.MaxValue) return null;

            var executable = new StringBuilder((int)length);
            var result = AssocQueryString(0, AssocStringExecutable, extension, null, executable, ref length);
            if (result != 0) return null;

            var value = executable.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(
        uint flags,
        uint associationString,
        string association,
        string? extra,
        StringBuilder? output,
        ref uint outputLength);
}
