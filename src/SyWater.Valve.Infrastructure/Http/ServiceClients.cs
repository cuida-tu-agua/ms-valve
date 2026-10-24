using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Infrastructure.Http;

/// <summary>
/// Gives the access token of the request being processed (implemented in the Api project, it reads
/// the Authorization header). Every call to another service goes WITH THE USER'S OWN TOKEN, so each
/// service applies its own rules for that user.
/// </summary>
public interface IAccessTokenProvider
{
    string? GetAccessToken();
}

internal static class Calls
{
    public static HttpRequestMessage Authorized(HttpMethod method, string url, IAccessTokenProvider tokens)
    {
        var token = tokens.GetAccessToken()
                    ?? throw new InvalidOperationException("There is no access token in the current request.");
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>Connection refused, DNS, timeout… all mean "that service is not available".</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string service, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceUnavailableException(service, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ExternalServiceUnavailableException(service, ex);
        }
    }

    /// <summary>"title" (and "remainingAttempts") of an RFC 9457 Problem Details answer.</summary>
    public static async Task<(string? Title, int? Remaining)> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var title = json.TryGetProperty("title", out var t) ? t.GetString() : null;
            int? remaining = json.TryGetProperty("remainingAttempts", out var r) && r.TryGetInt32(out var n) ? n : null;
            return (title, remaining);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, null);
        }
    }
}

/// <summary>
/// ms-places "GET /api/places/{id}/access": 200 = owner or collaborator (role and permissions), 404 = no access at all
/// or the place does not exist. Any other answer is "service unavailable", never a made-up access.
/// </summary>
public sealed class HttpPlaceAccessChecker(HttpClient http, IAccessTokenProvider tokens) : IPlaceAccessChecker
{
    public const string ServiceName = "place-service";

    private sealed record AccessDto(string Role, string[]? Permissions);

    public async Task<PlaceAccess?> GetAccessAsync(Guid placeId, CancellationToken ct)
    {
        using var request = Calls.Authorized(HttpMethod.Get, $"api/places/{placeId}/access", tokens);
        using var response = await Calls.SendAsync(http, request, ServiceName, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ExternalServiceUnavailableException(ServiceName,
                new HttpRequestException($"Unexpected status {(int)response.StatusCode}."));

        try
        {
            var dto = await response.Content.ReadFromJsonAsync<AccessDto>(ct)
                      ?? throw new ExternalServiceUnavailableException(ServiceName);
            return new PlaceAccess(dto.Role, dto.Permissions ?? []);
        }
        catch (JsonException ex)
        {
            throw new ExternalServiceUnavailableException(ServiceName, ex);
        }
    }
}

/// <summary>
/// ms-iam "POST /api/users/me/action-codes/verify" (HU-020). 204 = right code (and now used).
/// 400 = rejected: the same code of ms-iam (auth.invalid_code, auth.code_expired) goes back to the app.
/// </summary>
public sealed class HttpConfirmationCodeVerifier(HttpClient http, IAccessTokenProvider tokens) : IConfirmationCodeVerifier
{
    public const string ServiceName = "auth-service";

    public async Task VerifyAsync(string action, string code, CancellationToken ct)
    {
        using var request = Calls.Authorized(HttpMethod.Post, "api/users/me/action-codes/verify", tokens);
        request.Content = JsonContent.Create(new { action, code });
        using var response = await Calls.SendAsync(http, request, ServiceName, ct);

        if (response.StatusCode == HttpStatusCode.NoContent) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("ms-iam rejected the token.");

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Forbidden
            or HttpStatusCode.TooManyRequests)
        {
            var (title, remaining) = await Calls.ReadProblemAsync(response, ct);
            throw new ConfirmationCodeRejectedException(title ?? "auth.invalid_code", remaining);
        }

        throw new ExternalServiceUnavailableException(ServiceName,
            new HttpRequestException($"Unexpected status {(int)response.StatusCode}."));
    }
}

/// <summary>
/// ms-devices "POST /api/places/{id}/device/valve-commands" with X-Internal-Key: device-service is the
/// only one that talks to the ESP32 (MQTT). 202 = the order left.
/// </summary>
public sealed class HttpDeviceGateway(HttpClient http, IAccessTokenProvider tokens, string internalKey, TimeProvider clock)
    : IDeviceGateway
{
    public const string ServiceName = "device-service";
    public const string InternalKeyHeader = "X-Internal-Key";

    public async Task<DateTime> SendAsync(Guid placeId, Guid commandId, ValveAction action, CancellationToken ct)
    {
        using var request = Calls.Authorized(HttpMethod.Post, $"api/places/{placeId}/device/valve-commands", tokens);
        request.Headers.Add(InternalKeyHeader, internalKey);
        request.Content = JsonContent.Create(new { commandId, action = action == ValveAction.Close ? "CLOSE" : "OPEN" });

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw new ExternalServiceUnavailableException(ServiceName, ex);   // never connected: it did not leave
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new DeliveryUncertainException(ex);   // timeout / dropped connection: it may have left
        }

        using var _ = response;
        if (response.StatusCode == HttpStatusCode.Accepted) return clock.GetUtcNow().UtcDateTime;

        var (title, _) = await Calls.ReadProblemAsync(response, ct);
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new ValveNotFoundException(placeId),
            HttpStatusCode.Conflict when title == "device.offline" => new ValveDeviceOfflineException(),
            HttpStatusCode.Forbidden => new ExternalServiceUnavailableException(ServiceName,
                new InvalidOperationException("ms-devices refused the internal key: Internal:ApiKey must be the same in both services.")),
            // 503 mqtt.unavailable: ms-devices could not publish. Other 4xx: rejected before publishing.
            HttpStatusCode.ServiceUnavailable => new ExternalServiceUnavailableException(ServiceName,
                new HttpRequestException($"device-service: {title}")),
            >= HttpStatusCode.InternalServerError => new DeliveryUncertainException(
                new HttpRequestException($"Unexpected status {(int)response.StatusCode} ({title}).")),
            _ => new ExternalServiceUnavailableException(ServiceName,
                new HttpRequestException($"Unexpected status {(int)response.StatusCode} ({title}).")),
        };
    }
}
