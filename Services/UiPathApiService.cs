using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UiPathJobManager.Models;

namespace UiPathJobManager.Services;

/// <summary>
/// 一个待同步的 Orchestrator 文件夹。Path 为文件夹全路径(如 Finance 或 Finance/AP)。
/// </summary>
public class UiPathFolderOptions
{
    /// <summary>文件夹全路径,对应 /odata/Folders 的 FullyQualifiedName</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>是否一并查询该文件夹下的所有子文件夹</summary>
    public bool IncludeSubfolders { get; set; }

    /// <summary>false 时该条配置不参与同步</summary>
    public bool Enabled { get; set; } = true;
}

public class UiPathOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;

    /// <summary>Orchestrator 登录账号，用于 /api/Account/Authenticate</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Orchestrator 登录密码，用于 /api/Account/Authenticate</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Token 有效期（分钟），Orchestrator 默认 30 分钟</summary>
    public int TokenExpiryMinutes { get; set; } = 30;

    /// <summary>需要同步的文件夹列表，每个可独立决定是否包含子文件夹</summary>
    public List<UiPathFolderOptions> Folders { get; set; } = new();

    /// <summary>文件夹树缓存有效期（分钟），展开子文件夹时会用到</summary>
    public int FolderTreeCacheMinutes { get; set; } = 30;

    public int SyncIntervalMinutes { get; set; } = 5;
    public int PageSize { get; set; } = 100;
    public bool UseMockData { get; set; } = true;
}

public class UiPathApiOptions : IOptions<UiPathOptions>
{
    public UiPathOptions Value { get; set; } = new();
    public UiPathApiOptions() { }
    public UiPathApiOptions(UiPathOptions value) { Value = value; }
}

public interface IUiPathApiService
{
    Task<List<Job>> QueryJobsAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}

public class UiPathApiService : IUiPathApiService
{
    private readonly IHttpClientFactory _factory;
    private readonly UiPathOptions _options;
    private readonly IUiPathTokenProvider _tokens;
    private readonly IUiPathFolderService _folders;
    private readonly ILogger<UiPathApiService> _logger;

    public UiPathApiService(
        IHttpClientFactory factory,
        IOptions<UiPathOptions> options,
        IUiPathTokenProvider tokens,
        IUiPathFolderService folders,
        ILogger<UiPathApiService> logger)
    {
        _factory = factory;
        _options = options.Value;
        _tokens = tokens;
        _folders = folders;
        _logger = logger;
    }

    public async Task<List<Job>> QueryJobsAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default)
    {
        if (_options.UseMockData || string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogInformation("使用模拟数据查询 UiPath Jobs {Start} - {End}", startTime, endTime);
            return GenerateMockJobs(startTime, endTime);
        }

        var folders = await _folders.ResolveTargetFoldersAsync(ct);
        if (folders.Count == 0) return new List<Job>();

        var token = await _tokens.GetTokenAsync(ct);
        var allJobs = new List<Job>();
        var failed = 0;

        foreach (var folder in folders)
        {
            try
            {
                allJobs.AddRange(await QueryFolderJobsAsync(folder, startTime, endTime, token, ct));
            }
            catch (Exception ex)
            {
                // 单个文件夹失败不影响其它文件夹，但不回退模拟数据，避免假数据入库
                failed++;
                _logger.LogError(ex, "查询文件夹 {Folder} 的 Jobs 失败，已跳过", folder.Path);
            }
        }

        _logger.LogInformation(
            "从 {Total} 个文件夹拉取 Jobs 共 {Count} 条，失败 {Failed} 个", folders.Count, allJobs.Count, failed);
        return allJobs;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        if (_options.UseMockData) return true;
        if (string.IsNullOrWhiteSpace(_options.BaseUrl)) return false;

        try
        {
            // 认证 + 拉文件夹树 + 取一条 Job，任一环节失败都算连接失败
            var folders = await _folders.ResolveTargetFoldersAsync(ct);
            if (folders.Count == 0)
            {
                _logger.LogWarning("连接测试失败：没有解析出任何可用文件夹");
                return false;
            }

            var token = await _tokens.GetTokenAsync(ct);
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{_options.BaseUrl.TrimEnd('/')}/odata/Jobs?$top=1");
            ApplyHeaders(request, token, folders[0].Path);

            var client = _factory.CreateClient("UiPath");
            using var response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("UiPath 连接测试失败 HTTP {Status}", (int)response.StatusCode);
            }
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "测试 UiPath 连接失败");
            return false;
        }
    }

    private async Task<List<Job>> QueryFolderJobsAsync(
        UiPathFolder folder, DateTime start, DateTime end, string token, CancellationToken ct)
    {
        var jobs = new List<Job>();
        int skip = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildJobsUrl(start, end, skip));
            ApplyHeaders(request, token, folder.Path);

            var client = _factory.CreateClient("UiPath");
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var pageJobs = ParseJobs(json, folder);
            if (pageJobs.Count == 0) break;

            jobs.AddRange(pageJobs);
            if (pageJobs.Count < _options.PageSize) break;
            skip += _options.PageSize;
        }

        _logger.LogInformation("文件夹 {Folder} 拉取 {Count} 条", folder.Path, jobs.Count);
        return jobs;
    }

    /// <summary>
    /// 附加 Bearer Token、租户与文件夹请求头。
    /// Orchestrator 每个请求只能指定一个文件夹，故 folderPath 逐次传入。
    /// </summary>
    private void ApplyHeaders(HttpRequestMessage request, string token, string? folderPath)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (!string.IsNullOrWhiteSpace(_options.TenantName))
        {
            request.Headers.TryAddWithoutValidation("X-UIPATH-TenantName", _options.TenantName);
        }
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            request.Headers.TryAddWithoutValidation("X-UIPATH-FolderPath-Encoded", EncodeFolderPath(folderPath));
        }
    }

    /// <summary>
    /// 文件夹路径需按 Base64(UTF-16LE) 编码，直接传明文在中文路径下会出问题
    /// </summary>
    private static string EncodeFolderPath(string path) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(path));

    private string BuildJobsUrl(DateTime start, DateTime end, int skip)
    {
        // 文件夹由 X-UIPATH-FolderPath-Encoded 头指定，filter 只负责时间段
        var filter = $"StartTime ge {ToODataUtc(start)} and StartTime le {ToODataUtc(end)}";

        return $"{_options.BaseUrl.TrimEnd('/')}/odata/Jobs"
               + $"?$filter={Uri.EscapeDataString(filter)}"
               + $"&$orderby={Uri.EscapeDataString("StartTime desc")}"
               + $"&$top={_options.PageSize}&$skip={skip}";
    }

    private static string ToODataUtc(DateTime dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static List<Job> ParseJobs(string json, UiPathFolder folder)
    {
        var result = new List<Job>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("value", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    result.Add(MapJob(el, folder));
                }
            }
        }
        catch { }
        return result;
    }

    private static Job MapJob(JsonElement el, UiPathFolder folder)
    {
        return new Job
        {
            Id = el.TryGetProperty("Id", out var id) ? id.GetInt64() : 0,
            FolderId = folder.Id,
            FolderName = folder.Path,
            ProcessName = el.TryGetProperty("ReleaseName", out var r) ? r.GetString() ?? "" : "",
            RobotName = el.TryGetProperty("Robot", out var rob) && rob.ValueKind == JsonValueKind.Object
                ? rob.TryGetProperty("Name", out var rn) ? rn.GetString() : null : null,
            MachineName = el.TryGetProperty("Machine", out var m) && m.ValueKind == JsonValueKind.Object
                ? m.TryGetProperty("Name", out var mn) ? mn.GetString() : null : null,
            State = el.TryGetProperty("State", out var st) ? st.GetString() ?? "" : "",
            StartTime = el.TryGetProperty("StartTime", out var s) && s.TryGetDateTime(out var sd) ? sd : default,
            EndTime = el.TryGetProperty("EndTime", out var e) && e.ValueKind == JsonValueKind.String && e.TryGetDateTime(out var ed) ? ed : null,
            InputArguments = el.TryGetProperty("InputArguments", out var ia) && ia.ValueKind == JsonValueKind.String ? ia.GetString() : null,
            OutputArguments = el.TryGetProperty("OutputArguments", out var oa) && oa.ValueKind == JsonValueKind.String ? oa.GetString() : null,
            FaultReason = el.TryGetProperty("FaultedReason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null,
            Key = el.TryGetProperty("Key", out var k) ? k.GetString() : null,
            ReleaseName = el.TryGetProperty("ReleaseName", out var rn2) ? rn2.GetString() : null,
            ReleaseVersion = el.TryGetProperty("ReleaseVersion", out var rv) ? rv.GetString() : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 生成模拟数据，便于本地演示与测试。会按配置的文件夹(含模拟子文件夹)分配归属
    /// </summary>
    private List<Job> GenerateMockJobs(DateTime start, DateTime end)
    {
        var rnd = new Random((int)(start.Ticks & 0x7FFFFFFF));
        var processes = new[]
        {
            "财务对账_DailyReconciliation",
            "发票开具_GenerateInvoice",
            "订单处理_ProcessOrder",
            "客户数据同步_SyncCustomer",
            "邮件通知_SendNotification",
            "数据报表_DailyReport",
            "库存盘点_InventoryCheck",
            "审批流程_ApprovalWorkflow"
        };

        // 模拟文件夹池：IncludeSubfolders 的项额外生成两个模拟子文件夹
        var folderPool = new List<(long Id, string Path)>();
        long mockFolderId = 1000;
        foreach (var f in _options.Folders.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Path)))
        {
            folderPool.Add((mockFolderId++, f.Path.Trim()));
            if (f.IncludeSubfolders)
            {
                folderPool.Add((mockFolderId++, $"{f.Path.Trim()}/子流程A"));
                folderPool.Add((mockFolderId++, $"{f.Path.Trim()}/子流程B"));
            }
        }
        if (folderPool.Count == 0) folderPool.Add((mockFolderId, "Default"));

        var jobs = new List<Job>();
        var span = end - start;
        if (span.TotalDays > 30) span = TimeSpan.FromDays(30);
        var total = rnd.Next(50, 120);

        for (int i = 0; i < total; i++)
        {
            var proc = processes[rnd.Next(processes.Length)];
            var stateRoll = rnd.NextDouble();
            string state = stateRoll switch
            {
                < 0.7 => "Successful",
                < 0.85 => "Failed",
                < 0.95 => "Running",
                _ => "Pending"
            };

            var offset = rnd.Next(0, (int)span.TotalMinutes);
            var startTime = start.AddMinutes(offset);
            DateTime? endTime = state is "Running" or "Pending" ? null : startTime.AddSeconds(rnd.Next(30, 600));
            var robot = $"Robot-{rnd.Next(1, 6)}";
            var machine = $"Machine-{rnd.Next(1, 4)}";
            var pick = folderPool[rnd.Next(folderPool.Count)];

            jobs.Add(new Job
            {
                Id = 100000L + i,
                FolderId = pick.Id,
                FolderName = pick.Path,
                ProcessName = proc,
                RobotName = robot,
                MachineName = machine,
                State = state,
                StartTime = startTime,
                EndTime = endTime,
                InputArguments = $"{{\"batchId\":{rnd.Next(1000, 9999)}}}",
                OutputArguments = state == "Successful" ? $"{{\"records\":{rnd.Next(10, 500)}}}" : null,
                FaultReason = state == "Failed" ? "业务规则校验未通过：缺少必填字段" : null,
                Key = $"key-{Guid.NewGuid():N}".Substring(0, 16),
                ReleaseName = proc,
                ReleaseVersion = $"1.{rnd.Next(0, 9)}.{rnd.Next(0, 20)}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        return jobs;
    }
}