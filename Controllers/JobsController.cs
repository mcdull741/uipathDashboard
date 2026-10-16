using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UiPathJobManager.Data;
using UiPathJobManager.Models;
using UiPathJobManager.Services;

namespace UiPathJobManager.Controllers;

[Authorize]
public class JobsController : Controller
{
    private readonly AppDbContext _db;
    private readonly IJobSyncService _sync;
    private readonly IUiPathApiService _api;

    public JobsController(AppDbContext db, IJobSyncService sync, IUiPathApiService api)
    {
        _db = db;
        _sync = sync;
        _api = api;
    }

    public async Task<IActionResult> Index(JobQueryViewModel query)
    {
        // 同步查询参数到 ViewData
        ViewData["StateList"] = new[] { "Successful", "Failed", "Running", "Pending", "Stopped", "Faulted" };

        // 文件夹下拉：取库里已有的来源文件夹
        ViewData["FolderList"] = await _db.Jobs
            .Where(j => j.FolderName != null && j.FolderName != "")
            .Select(j => j.FolderName!)
            .Distinct()
            .OrderBy(f => f)
            .ToListAsync();

        if (query.PageSize <= 0) query.PageSize = 20;
        if (query.PageSize > 200) query.PageSize = 200;
        if (query.PageIndex <= 0) query.PageIndex = 1;

        var q = _db.Jobs.AsQueryable();

        if (query.StartDate.HasValue)
        {
            var start = query.StartDate.Value.Date;
            q = q.Where(j => j.StartTime >= start);
        }
        else
        {
            var defaultStart = DateTime.UtcNow.AddDays(-7);
            q = q.Where(j => j.StartTime >= defaultStart);
            query.StartDate = defaultStart;
        }

        if (query.EndDate.HasValue)
        {
            var end = query.EndDate.Value.Date.AddDays(1);
            q = q.Where(j => j.StartTime < end);
        }

        if (!string.IsNullOrWhiteSpace(query.State))
            q = q.Where(j => j.State == query.State);

        if (!string.IsNullOrWhiteSpace(query.FolderName))
            q = q.Where(j => j.FolderName == query.FolderName);

        if (!string.IsNullOrWhiteSpace(query.ProcessName))
            q = q.Where(j => j.ProcessName.Contains(query.ProcessName));

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.Trim();
            q = q.Where(j =>
                j.ProcessName.Contains(kw) ||
                j.RobotName!.Contains(kw) ||
                j.MachineName!.Contains(kw) ||
                j.FaultReason!.Contains(kw) ||
                j.Key!.Contains(kw));
        }

        query.TotalCount = await q.CountAsync();
        query.Results = await q
            .OrderByDescending(j => j.StartTime)
            .Skip((query.PageIndex - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return View(query);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Fetch(JobQueryViewModel query)
    {
        var start = query.StartDate ?? DateTime.UtcNow.AddHours(-24);
        var end = query.EndDate?.Date.AddDays(1).AddTicks(-1) ?? DateTime.UtcNow;

        try
        {
            var jobs = await _api.QueryJobsAsync(start, end);
            await _sync.UpsertJobsAsync(jobs);
            TempData["Message"] = $"成功获取 {jobs.Count} 条 Job 数据";
            TempData["AlertType"] = "success";
        }
        catch (Exception ex)
        {
            TempData["Message"] = $"拉取失败：{ex.Message}";
            TempData["AlertType"] = "danger";
        }

        return RedirectToAction(nameof(Index), new
        {
            StartDate = query.StartDate,
            EndDate = query.EndDate,
            State = query.State,
            ProcessName = query.ProcessName,
            Keyword = query.Keyword,
            FolderName = query.FolderName
        });
    }

    public async Task<IActionResult> Details(long id)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound();
        return View(job);
    }

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Edit(long id)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound();

        var vm = new JobEditViewModel
        {
            Id = job.Id,
            ProcessName = job.ProcessName,
            State = job.State,
            StartTime = job.StartTime,
            EndTime = job.EndTime,
            RobotName = job.RobotName,
            MachineName = job.MachineName,
            InputArguments = job.InputArguments,
            OutputArguments = job.OutputArguments,
            FaultReason = job.FaultReason,
            Remark = job.Remark,
            Tag = job.Tag
        };
        return View(vm);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(JobEditViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);

        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == vm.Id);
        if (job == null) return NotFound();

        job.Remark = vm.Remark;
        job.Tag = vm.Tag;
        job.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Message"] = "保存成功";
        TempData["AlertType"] = "success";
        return RedirectToAction(nameof(Details), new { id = vm.Id });
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(long id)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound();

        _db.Jobs.Remove(job);
        await _db.SaveChangesAsync();
        TempData["Message"] = "已删除";
        TempData["AlertType"] = "success";
        return RedirectToAction(nameof(Index));
    }
}