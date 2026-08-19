namespace GrepFlow.Interop;

public interface IFileAssociationResolver
{
    string? ResolveDefaultExecutable(string filePath);
}

public interface IAssociationExecutableQuery
{
    string? Query(string extension);
}
