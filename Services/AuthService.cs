using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using UiPathJobManager.Data;
using UiPathJobManager.Models;

namespace UiPathJobManager.Services;

public interface IAuthService
{
    Task<User?> ValidateAsync(string username, string password);
    Task SignInAsync(HttpContext httpContext, User user, bool rememberMe);
    Task SignOutAsync(HttpContext httpContext);
    Task<User?> GetCurrentUserAsync(HttpContext httpContext);
}

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AuthService> _logger;

    public AuthService(AppDbContext db, ILogger<AuthService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<User?> ValidateAsync(string username, string password)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsActive);
        if (user == null) return null;
        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            _logger.LogWarning("登录失败：密码错误 {Username}", username);
            return null;
        }
        return user;
    }

    public async Task SignInAsync(HttpContext httpContext, User user, bool rememberMe)
    {
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("DisplayName", user.DisplayName ?? user.Username),
            new(ClaimTypes.Role, user.Role),
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddDays(7) : DateTimeOffset.UtcNow.AddHours(8)
            });
    }

    public async Task SignOutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public Task<User?> GetCurrentUserAsync(HttpContext httpContext)
    {
        var idStr = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(idStr, out var id)) return Task.FromResult<User?>(null);
        return _db.Users.FirstOrDefaultAsync(u => u.Id == id);
    }
}