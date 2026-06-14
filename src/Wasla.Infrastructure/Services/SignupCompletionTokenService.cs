using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Wasla.Application.Abstractions.Auth;

namespace Wasla.Infrastructure.Services;

public sealed class SignupCompletionTokenService : ISignupCompletionTokenService
{
    private const string CachePrefix = "signup-complete:";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    private readonly IMemoryCache _cache;

    public SignupCompletionTokenService(IMemoryCache cache)
    {
        _cache = cache;
    }

    public string CreateToken(SignupCompletionPayload payload)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        _cache.Set(CachePrefix + token, payload, TokenLifetime);
        return token;
    }

    public SignupCompletionPayload? ValidateAndConsume(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var key = CachePrefix + token.Trim();
        if (!_cache.TryGetValue(key, out SignupCompletionPayload? payload) || payload is null)
            return null;

        _cache.Remove(key);
        return payload;
    }
}
