using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UiPathJobManager.Data;
using UiPathJobManager.Models;
using UiPathJobManager.Services;

namespace UiPathJobManager.Controllers;

[Authorize(Roles = "Admin")]
public class ConfigController : Controller
{
    private readonly AppDbContext _db;
    private readonly IUiPathApiService _api;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(AppDbContext db, IUiPathApiService api, ILogger<ConfigController> logger)
    {
        _db = db;
        _api = api;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var configs = await _db.AppConfigs.OrderBy(c => c.ConfigKey).ToListAsync();
        return View(configs);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Dictionary<string, string> values)
    {
        if (values == null || values.Count == 0)
        {
            TempData["Message"] = "未提供配置值";
            TempData["AlertType"] = "warning";
            return RedirectToAction(nameof(Index));
        }

        var keys = values.Keys.ToList();
        var configs = await _db.AppConfigs.Where(c => keys.Contains(c.ConfigKey)).ToListAsync();

        foreach (var kv in values)
        {
            var cfg = configs.FirstOrDefault(c => c.ConfigKey == kv.Key);
            if (cfg != null)
            {
                cfg.ConfigValue = kv.Value ?? "";
                cfg.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.AppConfigs.Add(new AppConfig
                {
                    ConfigKey = kv.Key,
                    ConfigValue = kv.Value ?? "",
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
        await _db.SaveChangesAsync();

        TempData["Message"] = "配置已保存，应用将在下一轮同步生效";
        TempData["AlertType"] = "success";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestConnection()
    {
        var ok = await _api.TestConnectionAsync();
        return Json(new { success = ok, message = ok ? "连接正常" : "连接失败" });
    }
}