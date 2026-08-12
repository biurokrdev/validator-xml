namespace D2ViewerEditor.Domain.Common;

public sealed class InvalidOoxmlPackageException : Exception
{
    public InvalidOoxmlPackageException(string message)
        : base(message)
    {
    }
}
