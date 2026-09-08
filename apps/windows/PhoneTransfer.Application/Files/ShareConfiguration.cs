namespace PhoneTransfer.Application.Files;

public sealed record ShareConfiguration(string RootPath, Guid Generation = default);

public interface IShareConfigurationStore
{
    ShareConfiguration? Read();
    ShareConfiguration Save(string rootPath);
    void Clear();
}
