namespace Zara.Application.Updates;

/// <summary>A failure code suitable for the update screen, without sensitive transport details.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string errorCode, Exception? innerException = null)
        : base(errorCode, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
