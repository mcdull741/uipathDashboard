using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UiPathJobManager.Data;
using UiPathJobManager.Models;

namespace UiPathJobManager.Services;

public interface IJobSyncService
{
    Task<DateTime?> SyncAsync(DateTime? lastSyncTime = null, CancellationToken ct = default);
    Task<DateTime?> GetLastSyncTimeAsync();
    Task UpsertJobsAsync(IEnumerable<Job> jobs, CancellationToken ct = default);
}

public class JobSyncService : IJobSyncService
{
    private readonly AppDbContext _db;
    private readonly IUiPathApiService _api;
    private readonly UiPathOptions _options;
    private readonly ILogger<JobSyncService> _logger;

    public JobSyncService(
        AppDbContext db,
        IUiPathApiService api,
        IOptions<UiPathOptions> options,
        ILogger<JobSyncService> logger)
    {
        _db = db;
        _api = api;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DateTime?> SyncAsync(DateTime? lastSyncTime = null, CancellationToken ct = default)
    {
        var start = lastSyncTime ?? DateTime.UtcNow.AddHours(-24);
        var end = DateTime.UtcNow;

        _logger.LogInformation("开始同步 UiPath Jobs: {Start} - {End}", start, end);

        // 一次调用内部会遍历所有已配置文件夹(含展开的子文件夹)并合并结果
        var jobs = await _api.QueryJobsAsync(start, end, ct);

        if (jobs.Count > 0)
        {
            var byFolder = jobs
                .GroupBy(j => string.IsNullOrWhiteSpace(j.FolderName) ? "(未标注)" : j.FolderName)
                .Select(g => $"{g.Key}={g.Count()}");
            _logger.LogInformation("本次同步按文件夹统计: {Stats}", string.Join(", ", byFolder));
        }

        await UpsertJobsAsync(jobs, ct);

        // 记录最后同步时间
        var configKey = "Sync.LastSyncTime";
        var cfg = await _db.AppConfigs.FirstOrDefaultAsync(c => c.ConfigKey == configKey, ct);
        var nowUtc = DateTime.UtcNow;
        if (cfg == null)
        {
            cfg = new AppConfig
            {
                ConfigKey = configKey,
                ConfigValue = nowUtc.ToString("O"),
                Description = "最后一次同步时间",
                UpdatedAt = nowUtc
            };
            _db.AppConfigs.Add(cfg);
        }
        else
        {
            cfg.ConfigValue = nowUtc.ToString("O");
            cfg.UpdatedAt = nowUtc;
        }
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("同步完成,共 {Total} 条", jobs.Count);
        return nowUtc;
    }

    public async Task<DateTime?> GetLastSyncTimeAsync()
    {
        var cfg = await _db.AppConfigs.FirstOrDefaultAsync(c => c.ConfigKey == "Sync.LastSyncTime");
        if (cfg != null && DateTime.TryParse(cfg.ConfigValue, out var dt))
        {
            return dt;
        }
        return null;
    }

    /// <summary>
    /// 按 JobId 增量写入：存在则更新，不存在则插入
    /// </summary>
    public async Task UpsertJobsAsync(IEnumerable<Job> jobs, CancellationToken ct = default)
    {
        var list = jobs.ToList();
        if (!list.Any()) return;

        var ids = list.Select(j => j.Id).ToList();
        var existing = await _db.Jobs
            .Where(j => ids.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, ct);

        int inserted = 0, updated = 0;
        foreach (var job in list)
        {
            if (existing.TryGetValue(job.Id, out var old))
            {
                // 保留本地扩展字段 (Remark, Tag)
                job.Remark = old.Remark;
                job.Tag = old.Tag;
                job.CreatedAt = old.CreatedAt;
                job.UpdatedAt = DateTime.UtcNow;
                _db.Entry(old).CurrentValues.SetValues(job);
                updated++;
            }
            else
            {
                job.CreatedAt = DateTime.UtcNow;
                job.UpdatedAt = DateTime.UtcNow;
                _db.Jobs.Add(job);
                inserted++;
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("增量写入完成，新增 {Inserted}, 更新 {Updated}", inserted, updated);
    }
}

/// <summary>
/// 后台周期同步任务
/// </summary>
public class JobSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobSyncBackgroundService> _logger;

    public JobSyncBackgroundService(IServiceScopeFactory scopeFactory, ILogger<JobSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后 5 秒执行首次同步
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<IJobSyncService>();
                await sync.SyncAsync(ct: stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台同步任务异常");
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var opts = scope.ServiceProvider.GetRequiredService<IOptions<UiPathOptions>>().Value;
                var interval = TimeSpan.FromMinutes(Math.Max(1, opts.SyncIntervalMinutes));
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取同步间隔失败，使用默认值");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}