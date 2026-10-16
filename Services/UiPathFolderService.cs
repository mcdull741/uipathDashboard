using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace UiPathJobManager.Services;

/// <summary>
/// 展开后的待查询文件夹
/// </summary>
/// <param name="Id">Orchestrator FolderId</param>
/// <param name="Path">文件夹全路径，用于 X-UIPATH-FolderPath-Encoded</param>
/// <param name="DisplayName">文件夹展示名</param>
public sealed record UiPathFolder(long Id, string Path, string DisplayName);

public interface IUiPathFolderService
{
    /// <summary>
    /// 按配置解析出实际要查询的文件夹列表。
    /// IncludeSubfolders = true 的项会展开为其全部后代文件夹，结果按路径去重。
    /// </summary>
    Task<IReadOnlyList<UiPathFolder>> ResolveTargetFoldersAsync(CancellationToken ct = default);

    /// <summary>丢弃文件夹树缓存</summary>
    void Invalidate();
}

/// <summary>
/// 调用 /odata/Folders 取回文件夹树并缓存，按 ParentId 递归展开子文件夹。
/// Orchestrator 每个请求只能指定一个文件夹，因此多文件夹必须在上层逐个查询。
/// </summary>
public class UiPathFolderService : IUiPathFolderService
{
    private sealed record FolderNode(long Id, string Path, string DisplayName, long? ParentId);

    private readonly IHttpClientFactory _factory;
    private readonly IUiPathTokenProvider _tokens;
    private readonly IOptions<UiPathOptions> _options;
    private readonly ILogger<UiPathFolderService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private List<FolderNode> _tree = new();
    private DateTimeOffset _treeExpiresAt;

    public UiPathFolderService(
        IHttpClientFactory factory,
        IUiPathTokenProvider tokens,
        IOptions<UiPathOptions> options,
        ILogger<UiPathFolderService> logger)
    {
        _factory = factory;
        _tokens = tokens;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UiPathFolder>> ResolveTargetFoldersAsync(CancellationToken ct = default)
    {
        var o = _options.Value;
        var configured = o.Folders
            .Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Path))
            .ToList();

        if (configured.Count == 0)
        {
            _logger.LogWarning("未配置任何 UiPath 文件夹，本次同步不查询任何 folder");
            return Array.Empty<UiPathFolder>();
        }

        // 模拟数据模式下不访问 Orchestrator，直接用配置里的路径
        if (o.UseMockData)
        {
            return configured
                .Select((f, i) => new UiPathFolder(1000 + i, Normalize(f.Path), f.Path))
                .ToList();
        }

        var tree = await GetTreeAsync(ct);
        var result = new List<UiPathFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cfg in configured)
        {
            var rootPath = Normalize(cfg.Path);
            var root = tree.FirstOrDefault(n => Normalize(n.Path) == rootPath);
            if (root is null)
            {
                _logger.LogWarning("配置的文件夹在 Orchestrator 中不存在，已跳过: {Path}", cfg.Path);
                continue;
            }

            var before = result.Count;
            Collect(root, cfg.IncludeSubfolders, tree, result, seen);
            _logger.LogInformation(
                "文件夹 {Path} 展开出 {Count} 个待查询文件夹 (IncludeSubfolders={Include})",
                cfg.Path, result.Count - before, cfg.IncludeSubfolders);
        }

        return result;
    }

    public void Invalidate()
    {
        _tree = new List<FolderNode>();
        _treeExpiresAt = default;
    }

    private static void Collect(
        FolderNode node,
        bool includeSubfolders,
        List<FolderNode> tree,
        List<UiPathFolder> result,
        HashSet<string> seen)
    {
        if (seen.Add(Normalize(node.Path)))
        {
            result.Add(new UiPathFolder(node.Id, node.Path, node.DisplayName));
        }

        if (!includeSubfolders) return;

        foreach (var child in tree.Where(n => n.ParentId == node.Id))
        {
            Collect(child, true, tree, result, seen);
        }
    }

    private async Task<List<FolderNode>> GetTreeAsync(CancellationToken ct)
    {
        if (_tree.Count > 0 && DateTimeOffset.UtcNow < _treeExpiresAt) return _tree;

        await _gate.WaitAsync(ct);
        try
        {
            if (_tree.Count > 0 && DateTimeOffset.UtcNow < _treeExpiresAt) return _tree;

            var o = _options.Value;
            var token = await _tokens.GetTokenAsync(ct);
            _tree = await FetchFoldersAsync(o, token, ct);
            _treeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, o.FolderTreeCacheMinutes));
            _logger.LogInformation("已加载 {Count} 个 Orchestrator 文件夹，缓存至 {Expire}", _tree.Count, _treeExpiresAt);
            return _tree;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<FolderNode>> FetchFoldersAsync(UiPathOptions o, string token, CancellationToken ct)
    {
        var url = $"{o.BaseUrl.TrimEnd('/')}/odata/Folders?$top=1000";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrWhiteSpace(o.TenantName))
        {
            request.Headers.TryAddWithoutValidation("X-UIPATH-TenantName", o.TenantName);
        }

        var client = _factory.CreateClient("UiPath");
        using var response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("获取 Orchestrator 文件夹失败 HTTP {Status}", (int)response.StatusCode);
            throw new InvalidOperationException(
                $"获取 /odata/Folders 失败 (HTTP {(int)response.StatusCode})，请确认账号有 Folders 读取权限");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var nodes = new List<FolderNode>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return nodes;
        }

        foreach (var el in arr.EnumerateArray())
        {
            if (!el.TryGetProperty("Id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;

            var path = el.TryGetProperty("FullyQualifiedName", out var fq) ? fq.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(path)) continue;

            var display = el.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? path : path;
            long? parentId = el.TryGetProperty("ParentId", out var p)
                             && p.ValueKind == JsonValueKind.Number
                             && p.TryGetInt64(out var pv)
                ? pv
                : null;

            nodes.Add(new FolderNode(id, path, display, parentId));
        }

        return nodes;
    }

    private static string Normalize(string path) => path.Trim().Trim('/');
}
