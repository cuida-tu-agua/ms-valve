using System.Security.Claims;
using SyWater.Valve.Application.Views;

namespace SyWater.Valve.Api.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Who is asking: "sub" (security.users.id) and "name" from the ms-iam token.
    /// It never comes from the URL or the body, so nobody can act on behalf of someone else.
    /// The name is stored with the order so the history (HU-022) can show it without calling ms-iam.
    /// </summary>
    public static Requester GetRequester(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var id))
            throw new UnauthorizedAccessException("The token has no valid 'sub' claim.");

        var name = user.FindFirstValue("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            var first = user.FindFirstValue("given_name");
            var last = user.FindFirstValue("family_name");
            name = string.Join(' ', new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        return new Requester(id, string.IsNullOrWhiteSpace(name) ? null : name);
    }
}
