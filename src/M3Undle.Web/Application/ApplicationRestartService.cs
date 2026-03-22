using Microsoft.Extensions.Hosting;

namespace M3Undle.Web.Application;

public interface IApplicationRestartService
{
    Task RequestRestartAsync(CancellationToken ct = default);
}

public sealed class ApplicationRestartService(IHostApplicationLifetime lifetime) : IApplicationRestartService
{
    public Task RequestRestartAsync(CancellationToken ct = default)
    {
        // The restart is unconditional once requested — fire-and-forget with a brief
        // grace delay so the calling response can complete before the process stops.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            lifetime.StopApplication();
        });

        return Task.CompletedTask;
    }
}
