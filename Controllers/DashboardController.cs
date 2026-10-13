using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UiPathJobManager.Data;
using UiPathJobManager.Models;
using UiPathJobManager.Services;

namespace UiPathJobManager.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly AppDbContext _db;
    private readonly IJobSyncService _sync;
    private readonly IUiPathApiService _api;

    public DashboardController(AppDbContext db, IJobSyncService sync, IUiPathApiService api)
    {
        _db = db;
        _sync = sync;
        _api = api;
    }

    public async Task<IActionResult> Index()
    {
        var model = new SyncStatusViewModel();

        model.LastSyncTime = await _sync.GetLastSyncTimeAsync();
        model.TotalJobs = await _db.Jobs.CountAsync();

        var today = DateTime.UtcNow.Date;
        var tomorrow = today.AddDays(1);
        var todayList = await _db.Jobs.Where(j => j.StartTime >= today && j.StartTime < tomorrow).ToListAsync();

        model.TodayJobs = todayList.Count;
        model.SuccessfulToday = todayList.Count(j => j.State == "Successful");
        model.FailedToday = todayList.Count(j => j.State == "Failed");
        model.RunningNow = todayList.Count(j => j.State == "Running");

        // 趋势: 近 7 天每日成功/失败
        var last7 = DateTime.UtcNow.Date.AddDays(-6);
        var trend = await _db.Jobs
            .Where(j => j.StartTime >= last7)
            .Select(j => new { j.StartTime, j.State })
            .ToListAsync();
        model.TrendData = trend
            .GroupBy(j => j.StartTime.Date)
            .Select(g => new ChartDataPoint
            {
                Label = g.Key.ToString("MM-dd"),
                Value = g.Count(x => x.State == "Successful")
            })
            .OrderBy(p => p.Label)
            .ToList();

        // 状态分布
        var allStates = await _db.Jobs.GroupBy(j => j.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync();
        model.StateDistribution = allStates
            .Select(g => new ChartDataPoint { Label = g.State, Value = g.Count })
            .ToList();

        // 热门流程
        var topProcs = await _db.Jobs.GroupBy(j => j.ProcessName)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(8)
            .ToListAsync();
        model.TopProcesses = topProcs
            .Select(g => new ChartDataPoint { Label = g.Name, Value = g.Count })
            .ToList();

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncNow()
    {
        try
        {
            await _sync.SyncAsync();
            TempData["Message"] = "同步成功";
            TempData["AlertType"] = "success";
        }
        catch (Exception ex)
        {
            TempData["Message"] = $"同步失败：{ex.Message}";
            TempData["AlertType"] = "danger";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> TestConnection()
    {
        var ok = await _api.TestConnectionAsync();
        return Json(new { success = ok, message = ok ? "连接成功" : "连接失败，请检查配置或启用模拟数据" });
    }
}