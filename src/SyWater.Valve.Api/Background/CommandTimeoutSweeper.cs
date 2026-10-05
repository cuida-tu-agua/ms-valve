using SyWater.Valve.Application.Ports.In;

namespace SyWater.Valve.Api.Background;

/// <summary>
/// HU-020 "si el dispositivo no responde en 30 segundos, se informa el fallo": every 5 s, the SENT
/// orders whose timeout passed become ACK_TIMEOUT. The app sees it on its next poll.
/// </summary>
public sealed class CommandTimeoutSweeper(IServiceScopeFactory scopes, ILogger<CommandTimeoutSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<ITimeOutCommandsUseCase>().ExecuteAsync(stoppingToken);
                if (count > 0) logger.LogInformation("{Count} valve order(s) timed out (no answer from the device)", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Timeout sweep failed; retrying in {Seconds}s", Every.TotalSeconds);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
