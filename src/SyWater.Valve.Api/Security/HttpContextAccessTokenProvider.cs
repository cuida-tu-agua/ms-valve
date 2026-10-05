using Microsoft.Net.Http.Headers;
using SyWater.Valve.Infrastructure.Http;

namespace SyWater.Valve.Api.Security;

/// <summary>Reads "Authorization: Bearer &lt;token&gt;" of the current HTTP request.</summary>
public sealed class HttpContextAccessTokenProvider(IHttpContextAccessor accessor) : IAccessTokenProvider
{
    private const string Scheme = "Bearer ";

    public string? GetAccessToken()
    {
        var header = accessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();
        return header is not null && header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            ? header[Scheme.Length..].Trim()
            : null;
    }
}
