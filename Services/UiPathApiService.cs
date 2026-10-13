using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UiPathJobManager.Data;
using UiPathJobManager.Models;

namespace UiPathJobManager.Services;

public class UiPathOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string? FolderId { get; set; }
    public string AuthType { get; set; } = "Bearer";
    public string ApiKey { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
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
    private readonly HttpClient _http;
    private readonly UiPathOptions _options;
    private readonly ILogger<UiPathApiService> _logger;

    private static readonly string[] States =
        { "Successful", "Failed", "Pending", "Running", "Stopped", "Faulted" };

    public UiPathApiService(IHttpClientFactory factory, IOptions<UiPathOptions> options, ILogger<UiPathApiService> logger)
    {
        _http = factory.CreateClient("UiPath");
        _options = options.Value;
        _logger = logger;
    }

    public async Task<List<Job>> QueryJobsAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default)
    {
        if (_options.UseMockData || string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogInformation("使用模拟数据查询 UiPath Jobs {Start} - {End}", startTime, endTime);
            return GenerateMockJobs(startTime, endTime);
        }

        try
        {
            var token = await GetAccessTokenAsync(ct);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = BuildJobsUrl(startTime, endTime);
            var allJobs = new List<Job>();
            int skip = 0;

            while (true)
            {
                var pagedUrl = $"{url}&$top={_options.PageSize}&$skip={skip}";
                var response = await _http.GetAsync(pagedUrl, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var pageJobs = ParseJobs(json);
                if (pageJobs.Count == 0) break;

                allJobs.AddRange(pageJobs);
                if (pageJobs.Count < _options.PageSize) break;
                skip += _options.PageSize;
            }

            return allJobs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "调用 UiPath API 失败，回退到模拟数据");
            return GenerateMockJobs(startTime, endTime);
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        if (_options.UseMockData) return true;
        if (string.IsNullOrWhiteSpace(_options.BaseUrl)) return false;
        try
        {
            var token = await GetAccessTokenAsync(ct);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await _http.GetAsync($"{_options.BaseUrl.TrimEnd('/')}/odata/Jobs?$top=1", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "测试 UiPath 连接失败");
            return false;
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        // PAT (Personal Access Token)
        if (_options.AuthType == "Bearer" && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return _options.ApiKey;
        }

        // OAuth
        var tokenUrl = $"{_options.BaseUrl.TrimEnd('/')}/identity_/connect/token";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["scope"] = "OR.Jobs.Read OR.Folders.Read"
        };
        var resp = await _http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
    }

    private string BuildJobsUrl(DateTime start, DateTime end)
    {
        var sb = new StringBuilder($"{_options.BaseUrl.TrimEnd('/')}/odata/Jobs?$filter=");
        sb.Append($"StartTime ge {start:yyyy-MM-ddTHH:mm:ssZ} and StartTime le {end:yyyy-MM-ddTHH:mm:ssZ}");
        sb.Append("&$orderby=StartTime desc");
        if (!string.IsNullOrWhiteSpace(_options.FolderId))
        {
            sb.Append($"&$filter=FolderId eq {_options.FolderId}");
        }
        return sb.ToString();
    }

    private static List<Job> ParseJobs(string json)
    {
        var result = new List<Job>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("value", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    result.Add(MapJob(el));
                }
            }
        }
        catch { }
        return result;
    }

    private static Job MapJob(JsonElement el)
    {
        return new Job
        {
            Id = el.TryGetProperty("Id", out var id) ? id.GetInt64() : 0,
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
    /// 生成模拟数据，便于本地演示与测试
    /// </summary>
    private static List<Job> GenerateMockJobs(DateTime start, DateTime end)
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

            jobs.Add(new Job
            {
                Id = 100000L + i,
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