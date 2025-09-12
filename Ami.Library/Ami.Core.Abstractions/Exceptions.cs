namespace Ami.Core.Abstractions;

/// <summary>
/// Base exception for the AMI library.
/// </summary>
public class AmiException : Exception
{
    public AmiException(string message) : base(message) { }
    public AmiException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a manuscript file is invalid or missing required configuration,
/// such as a manuscriptId.
/// </summary>
public class ManuscriptConfigurationException : AmiException
{
    public string? FilePath { get; }
    public ManuscriptConfigurationException(string message, string? filePath = null) : base(message)
    {
        FilePath = filePath;
    }
}