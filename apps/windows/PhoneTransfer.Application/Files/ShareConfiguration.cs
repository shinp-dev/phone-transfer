namespace PhoneTransfer.Application.Files;

public sealed record ShareConfiguration(string RootPath);

public interface IShareConfigurationStore
{
    ShareConfiguration? Read();
    ShareConfiguration Save(string rootPath);
    void Clear();
}
