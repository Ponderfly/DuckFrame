namespace DuckFrame;

/// <summary>DuckFrame 抛出的所有可预期异常的基类。</summary>
public sealed class DuckFrameException : Exception
{
    public DuckFrameException(string message) : base(message) { }

    public DuckFrameException(string message, Exception? innerException)
        : base(message, innerException) { }
}
