using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace UiPathJobManager.Services;

public interface IUiPathTokenProvider
{
    /// <summary>
    /// 获取可用的 Bearer Token，未过期时直接复用缓存。
    /// </summary>
    Task<string> GetTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// 丢弃缓存，下次调用重新认证。
    /// </summary>
    void Invalidate();
}

/// <summary>
/// 调用 UiPath Orchestrator 的 /api/Account/Authenticate (ROPC) 获取 Bearer Token，
/// 并在内存中缓存，过期前提前刷新。注册为 Singleton，供所有作用域共享。
/// </summary>
public class UiPathTokenProvider : IUiPathTokenProvider
{
    /// <summary>提前多少秒续期，避免拿到"刚好过期"的 token</summary>
    private const int RefreshBufferSeconds = 120;

    private readonly IHttpClientFactory _factory;
    private readonly IOptions<UiPathOptions> _options;
    private readonly ILogger<UiPathTokenProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTimeOffset _expiresAt;
    private string _cacheKey = string.Empty;

    public UiPathTokenProvider(
        IHttpClientFactory factory,
        IOptions<UiPathOptions> options,
        ILogger<UiPathTokenProvider> logger)
    {
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        var o = _options.Value;
        ValidateCredentials(o);

        var key = BuildCacheKey(o);
        if (IsUsable(key)) return _token!;

        await _gate.WaitAsync(ct);
        try
        {
            // 双检：等待锁期间可能已有其他线程完成认证
            if (IsUsable(key)) return _token!;

            _token = await AuthenticateAsync(o, ct);
            _expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, o.TokenExpiryMinutes));
            _cacheKey = key;
            _logger.LogInformation("UiPath Token 已获取，有效期至 {Expire}", _expiresAt);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _token = null;
        _cacheKey = string.Empty;
        _expiresAt = default;
    }

    private bool IsUsable(string key) =>
        !string.IsNullOrEmpty(_token)
        && _cacheKey == key
        && DateTimeOffset.UtcNow.AddSeconds(RefreshBufferSeconds) < _expiresAt;

    private static string BuildCacheKey(UiPathOptions o) =>
        $"{o.BaseUrl.TrimEnd('/')}|{o.TenantName}|{o.Username}";

    private static void ValidateCredentials(UiPathOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.BaseUrl))
            throw new InvalidOperationException("未配置 UiPath.BaseUrl，无法认证");
        if (string.IsNullOrWhiteSpace(o.TenantName))
            throw new InvalidOperationException("未配置 UiPath.TenantName，无法认证");
        if (string.IsNullOrWhiteSpace(o.Username) || string.IsNullOrWhiteSpace(o.Password))
            throw new InvalidOperationException("未配置 UiPath.Username / UiPath.Password，无法认证");
    }

    private async Task<string> AuthenticateAsync(UiPathOptions o, CancellationToken ct)
    {
        var url = $"{o.BaseUrl.TrimEnd('/')}/api/Account/Authenticate";
        var payload = new
        {
            tenancyName = o.TenantName,
            usernameOrEmailAddress = o.Username,
            password = o.Password
        };

        var client = _factory.CreateClient("UiPath");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("UiPath 认证失败 HTTP {Status}: {Body}", (int)response.StatusCode, Truncate(json));
            throw new InvalidOperationException(
                $"UiPath 认证失败 (HTTP {(int)response.StatusCode})，请检查租户名与账号密码");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            var message = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                          && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            _logger.LogError("UiPath 认证被拒绝: {Message}", message ?? json);
            throw new InvalidOperationException($"UiPath 认证被拒绝：{message ?? "用户名或密码错误"}");
        }

        // 标准响应: { "result": "<token>", "success": true, "__abp": true }
        // 部分版本返回 access_token / token
        var token = TryGetString(root, "result")
                    ?? TryGetString(root, "access_token")
                    ?? TryGetString(root, "token");

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("UiPath 认证响应中未包含 token: {Body}", Truncate(json));
            throw new InvalidOperationException("UiPath 认证响应中未包含 token");
        }

        return token;
    }

    private static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "...";
}
