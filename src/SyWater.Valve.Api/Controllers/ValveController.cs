using Microsoft.AspNetCore.Mvc;
using SyWater.Valve.Api.Security;
using SyWater.Valve.Application.Ports.In;
using SyWater.Valve.Application.Views;
using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Api.Controllers;

/// <summary>Body of POST …/valve/close (HU-020): the 6-digit code ms-iam sent by e-mail.</summary>
public sealed record CloseValveRequest(string? Code);

/// <summary>
/// E5 · Válvula. Every endpoint requires a valid token (fallback policy in Program.cs) and checks
/// with place-service that the place belongs to the user.
/// </summary>
[ApiController]
[Route("api/places/{placeId:guid}/valve")]
public sealed class ValveController : ControllerBase
{
    /// <summary>HU-019: state, communication and the order in progress (if any).</summary>
    [HttpGet]
    public Task<ValveView> Get(Guid placeId, [FromServices] IGetValveUseCase useCase, CancellationToken ct) =>
        useCase.ExecuteAsync(placeId, ct);

    /// <summary>HU-020: close with the e-mailed code. 202 = the order left; poll GET commands/{id}.</summary>
    [HttpPost("close")]
    public async Task<IActionResult> Close(Guid placeId, [FromBody] CloseValveRequest? body,
        [FromServices] IRequestValveCommandUseCase useCase, CancellationToken ct)
    {
        var command = await useCase.ExecuteAsync(User.GetRequester(), placeId, ValveAction.Close, body?.Code, ct);
        return AcceptedAtCommand(placeId, command);
    }

    /// <summary>HU-021: open (no code needed).</summary>
    [HttpPost("open")]
    public async Task<IActionResult> Open(Guid placeId, [FromServices] IRequestValveCommandUseCase useCase, CancellationToken ct)
    {
        var command = await useCase.ExecuteAsync(User.GetRequester(), placeId, ValveAction.Open, null, ct);
        return AcceptedAtCommand(placeId, command);
    }

    /// <summary>Progress of one order: SENT → ACK_SUCCESS | ACK_TIMEOUT | FAILED.</summary>
    [HttpGet("commands/{commandId:guid}", Name = "GetValveCommand")]
    public Task<ValveCommandView> GetCommand(Guid placeId, Guid commandId,
        [FromServices] IGetValveCommandUseCase useCase, CancellationToken ct) =>
        useCase.ExecuteAsync(placeId, commandId, ct);

    /// <summary>HU-022: history. Default: last 30 days. Dates in ISO 8601 (UTC).</summary>
    [HttpGet("commands")]
    public Task<IReadOnlyList<ValveCommandView>> ListCommands(Guid placeId,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromServices] IListValveCommandsUseCase useCase, CancellationToken ct) =>
        useCase.ExecuteAsync(placeId, from?.UtcDateTime, to?.UtcDateTime, ct);

    private AcceptedAtRouteResult AcceptedAtCommand(Guid placeId, ValveCommandView command) =>
        AcceptedAtRoute("GetValveCommand", new { placeId, commandId = command.Id }, command);
}
