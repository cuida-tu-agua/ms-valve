using StackExchange.Redis;

namespace SyWater.Valve.Api.Security;

/// <summary>Answers "was this access token revoked in ms-iam?" (logout, password reset, deleted account).</summary>
public interface ITokenRevocationChecker
{
    Task<bool> IsRevokedAsync(string? tokenId, string? userId, long? issuedAtEpochSeconds);
}

/// <summary>
/// Reads the SAME Redis keys that ms-iam writes (RedisTokenRevocationStore.java):
///   iam:revoked:jti:{jti}        exists       → that token was logged out (HU-006)
///   iam:revoked-before:{userId}  epoch secs   → every token with iat &lt;= value is dead (HU-005, HU-008)
/// Without this check a logged-out token would keep working here until it expires (up to 1 h).
/// </summary>
public sealed class RedisTokenRevocationChecker(IConnectionMultiplexer redis) : ITokenRevocationChecker
{
    public const string JtiPrefix = "iam:revoked:jti:";
    public const string UserPrefix = "iam:revoked-before:";

    public async Task<bool> IsRevokedAsync(string? tokenId, string? userId, long? issuedAtEpochSeconds)
    {
        var db = redis.GetDatabase();

        if (!string.IsNullOrEmpty(tokenId) && await db.KeyExistsAsync(JtiPrefix + tokenId))
            return true;

        if (string.IsNullOrEmpty(userId) || issuedAtEpochSeconds is null)
            return false;

        var revokedBefore = await db.StringGetAsync(UserPrefix + userId);
        return revokedBefore.HasValue
               && long.TryParse(revokedBefore.ToString(), out var before)
               && issuedAtEpochSeconds.Value <= before;
    }
}
