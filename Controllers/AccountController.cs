using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UiPathJobManager.Models;
using UiPathJobManager.Services;

namespace UiPathJobManager.Controllers;

public class AccountController : Controller
{
    private readonly IAuthService _auth;
    private readonly ILogger<AccountController> _logger;

    public AccountController(IAuthService auth, ILogger<AccountController> logger)
    {
        _auth = auth;
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Dashboard");
        }
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _auth.ValidateAsync(model.Username, model.Password);
        if (user == null)
        {
            ModelState.AddModelError(string.Empty, "用户名或密码错误");
            return View(model);
        }

        await _auth.SignInAsync(HttpContext, user, model.RememberMe);
        _logger.LogInformation("用户 {Username} 登录成功", user.Username);

        if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
        {
            return Redirect(model.ReturnUrl);
        }
        return RedirectToAction("Index", "Dashboard");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _auth.SignOutAsync(HttpContext);
        return RedirectToAction("Login");
    }

    [AllowAnonymous]
    public IActionResult AccessDenied() => View();
}