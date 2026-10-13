# UiPath Job 管理平台

基于 **ASP.NET Core 10** 的 UiPath Orchestrator Job 后台管理系统,支持登录、看板、列表、表单编辑,使用 API 按时间段增量拉取 Job 数据并写入本地 SQLite。

## 功能特性

| 模块 | 说明 |
|------|------|
| 登录页 | Cookie 认证 + BCrypt 加密,默认账号 `admin / Admin@123456` |
| 数据看板 | 今日 Job / 成功 / 失败 / 运行中、近 7 天趋势、状态分布、Top 流程 |
| Job 列表 | 按时间段、状态、流程名、关键字筛选;分页与排序 |
| Job 编辑 | 仅 Admin 可编辑本地扩展字段(Remark / Tag) |
| 系统配置 | UiPath 连接参数、API Key、OAuth、同步间隔 |
| API 增量同步 | 按 JobId 去重,已存在则更新、不存在则新增 |
| 后台周期同步 | IHostedService 周期任务 |
| 响应式布局 | Bootstrap 5,自适应 PC / 平板 / 手机 |
| 模拟数据 | 无 UiPath 环境也能演示 |

## 技术栈

- **ASP.NET Core 10** MVC + Razor Views
- **Entity Framework Core 10** + **SQLite**
- **Cookie 认证** + **BCrypt.Net-Next** 密码加密
- **HttpClient** 调用 UiPath Orchestrator OData API
- **Bootstrap 5.3** + **Chart.js 4.4**
- **IHostedService** 后台周期任务

## 目录结构

```
UiPathJobManager/
├── UiPathJobManager.csproj   # 项目文件 (.NET 10)
├── Program.cs                # 启动入口
├── appsettings.json          # 配置
├── appsettings.Development.json
├── Properties/
│   └── launchSettings.json   # 启动配置 (http://localhost:5080)
├── Controllers/
│   ├── AccountController.cs  # 登录/登出
│   ├── DashboardController.cs # 看板
│   ├── JobsController.cs     # Job CRUD
│   └── ConfigController.cs   # 系统配置
├── Models/
│   ├── User.cs
│   ├── Job.cs                # 实体,Id 即 UiPath JobId
│   └── AppConfig.cs          # 视图模型
├── Data/
│   └── AppDbContext.cs       # EF Core + DbSeeder
├── Services/
│   ├── AuthService.cs        # 认证服务
│   ├── UiPathApiService.cs   # 调用 UiPath Orchestrator API
│   └── JobSyncService.cs     # 增量同步 (Upsert)
├── Views/
│   ├── _ViewImports.cshtml
│   ├── _ViewStart.cshtml
│   ├── Shared/
│   │   ├── _Layout.cshtml
│   │   └── _ValidationScriptsPartial.cshtml
│   ├── Account/Login.cshtml
│   ├── Dashboard/Index.cshtml
│   ├── Jobs/Index.cshtml
│   ├── Jobs/Details.cshtml
│   ├── Jobs/Edit.cshtml
│   └── Config/Index.cshtml
└── wwwroot/
    ├── css/site.css
    └── js/site.js
```

## 快速开始

### 前置条件

- .NET 10 SDK
- Windows / macOS / Linux 任一系统

### 安装与运行

```bash
# 1) 进入项目目录
cd UiPathJobManager

# 2) 还原依赖
dotnet restore

# 3) 构建
dotnet build

# 4) 启动 (默认监听 http://localhost:5080)
dotnet run
```

启动后访问 `http://localhost:5080`,自动跳转登录页。

默认账号:
- 管理员: `admin / Admin@123456`
- 普通用户: `demo / Demo@123`

> SQLite 文件会自动创建在项目根目录 `uipath_jobs.db`。
> 默认使用模拟数据,无需 UiPath 环境即可体验完整流程。

### 切换到真实 UiPath Orchestrator

1. 登录后进入 **系统配置**
2. 修改:
   - `UiPath.BaseUrl`:`https://cloud.uipath.com/{org}/{tenant}/orchestrator_`
   - `UiPath.TenantName`:租户名
   - `UiPath.FolderId`:文件夹 ID
   - `UiPath.ApiKey`:PAT 或 OAuth ClientId/Secret
   - `UiPath.UseMockData`:`false`
3. 保存 → **测试连接** → **Job 列表页 → 调用 API 拉取并存入本地 SQLite**

## 关键设计

### 1) 按 JobId 增量同步

`Job.Id` 即 UiPath JobId(主键),同步逻辑在 `JobSyncService.UpsertJobsAsync`:

```csharp
var existing = await _db.Jobs.Where(j => ids.Contains(j.Id))
    .ToDictionaryAsync(j => j.Id);

foreach (var job in list) {
    if (existing.TryGetValue(job.Id, out var old)) {
        // 存在:更新,保留本地字段 (Remark/Tag)
        _db.Entry(old).CurrentValues.SetValues(job);
    } else {
        // 不存在:插入
        _db.Jobs.Add(job);
    }
}
```

### 2) 后台周期同步

`JobSyncBackgroundService` 启动 5 秒首次同步,之后按配置间隔(默认 5 分钟)循环拉取。

### 3) UiPath OData 查询

```
GET {BaseUrl}/odata/Jobs?$filter=StartTime ge {start} and StartTime le {end}
                     &$orderby=StartTime desc&$top=100&$skip={n}
```

支持 Bearer Token(PAT)与 OAuth(client_credentials)两种鉴权。

## 响应式布局

- 桌面:4 列 KPI 卡 + 2 列图表
- 平板:2 列 KPI
- 手机:单列堆叠,折叠导航栏,次要字段隐藏

## 常见问题

**Q: 没有 UiPath 怎么演示?**
A: 保持 `UiPath.UseMockData = true`,启动后会自动生成 50-120 条模拟 Job,完整跑通看板、列表、编辑。

**Q: 数据库在哪里?**
A: 项目根目录 `uipath_jobs.db`,EF Core 自动创建。

**Q: 如何清空数据?**
A: 删除 `uipath_jobs.db` 后重启,或调用列表页的「删除」按钮逐条删除。

**Q: 如何修改默认管理员密码?**
A: 在 `appsettings.json` 的 `DefaultAdmin` 节修改,但首次启动后会写入数据库,后续修改需要在数据库或通过应用界面调整。