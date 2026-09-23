namespace Zara.Application.Updates;

public interface IUpdateInstaller
{
    Task<bool> LaunchAsync(string installerPath, CancellationToken cancellationToken = default);
}
