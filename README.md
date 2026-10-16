# UiPath Job 管理平台

基于 **ASP.NET Core 10** 的 UiPath Orchestrator Job 后台管理系统,支持登录、看板、列表、表单编辑,使用 API 按时间段增量拉取 Job 数据并写入本地 SQLite。

## 功能特性

| 模块 | 说明 |
|------|------|
| 登录页 | Cookie 认证 + BCrypt 加密,默认账号 `admin / Admin@123456` |
| 数据看板 | 今日 Job / 成功 / 失败 / 运行中、近 7 天趋势、状态分布、Top 流程 |
| Job 列表 | 按时间段、状态、流程名、关键字筛选;分页与排序 |
| Job 编辑 | 仅 Admin 可编辑本地扩展字段(Remark / Tag) |
| 系统配置 | UiPath 连接参数、Orchestrator 账号、多文件夹与子文件夹、同步间隔 |
| API 增量同步 | 多文件夹合并拉取,按 JobId 去重,已存在则更新、不存在则新增 |
| 后台周期同步 | IHostedService 周期任务 |
| 响应式布局 | Bootstrap 5,自适应 PC / 平板 / 手机 |
| 模拟数据 | 无 UiPath 环境也能演示 |

## 技术栈

- **ASP.NET Core 10** MVC + Razor Views
- **Entity Framework Core 10** + **SQLite**
- **Cookie 认证** + **BCrypt.Net-Next** 密码加密
- **HttpClient** 调用 UiPath Orchestrator 标准接口(认证 `/api/Account/Authenticate`,取数 `/odata/Jobs`)
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
│   ├── UiPathTokenProvider.cs # /api/Account/Authenticate 认证 + Token 缓存
│   └── UiPathFolderService.cs # /odata/Folders 文件夹树 + 子文件夹递归展开
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
   - `UiPath.Username` / `UiPath.Password`:Orchestrator 登录账号密码
   - `UiPath.Folders`:待同步文件夹列表(见下节)
   - `UiPath.UseMockData`:`false`
3. 保存 → **测试连接** → **Job 列表页 → 调用 API 拉取并存入本地 SQLite**

### 配置多个文件夹(含子文件夹)

`appsettings.json` 的 `UiPath.Folders` 是一个数组,每项配置一个待同步文件夹:

```json
"Folders": [
  { "Path": "Finance",      "IncludeSubfolders": true,  "Enabled": true },
  { "Path": "HR",           "IncludeSubfolders": false, "Enabled": true },
  { "Path": "Shared/Temp",  "IncludeSubfolders": true,  "Enabled": false }
]
```

| 字段 | 说明 |
|------|------|
| `Path` | 文件夹全路径,对应 `/odata/Folders` 的 `FullyQualifiedName`,如 `Finance` 或 `Finance/AP` |
| `IncludeSubfolders` | `true` 时自动展开该文件夹的**全部后代**一并查询 |
| `Enabled` | `false` 时跳过该项 |

子文件夹展开流程:同步时先拉一次 `/odata/Folders` 得到文件夹树(字段 `Id` / `FullyQualifiedName` / `ParentId`),
按 `ParentId` 递归找出目标文件夹的所有后代,再**逐个文件夹**查询 Jobs ——
Orchestrator 的 folder 请求头一次只能指定一个文件夹,因此 N 个文件夹就是 N 组请求。
结果按路径去重,父文件夹与子文件夹同时配置时不会重复拉取。
文件夹树默认缓存 30 分钟(`FolderTreeCacheMinutes`)。

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

### 3) 认证:ROPC 换取 Bearer Token

```
POST {BaseUrl}/api/Account/Authenticate
Content-Type: application/json

{ "tenancyName": "{tenant}", "usernameOrEmailAddress": "{user}", "password": "{pwd}" }
```

响应取 `result` 字段即为 Bearer Token:

```json
{ "result": "{access_token}", "success": true, "unAuthorizedRequest": false, "__abp": true }
```

Token 默认 30 分钟有效,由 `UiPathTokenProvider`(Singleton)缓存,过期前 2 分钟自动重新认证,
缓存键为 `BaseUrl|TenantName|Username`,配置变更即自动失效。

### 4) 文件夹解析

```
GET {BaseUrl}/odata/Folders?$top=1000
Authorization: Bearer {token}
X-UIPATH-TenantName: {tenant}
```

由 `UiPathFolderService`(Singleton)按 `ParentId` 递归展开子文件夹,树结构缓存 `FolderTreeCacheMinutes` 分钟。

### 5) UiPath OData 查询

```
GET {BaseUrl}/odata/Jobs?$filter=StartTime ge {start} and StartTime le {end}
                     &$orderby=StartTime desc&$top=100&$skip={n}
Authorization: Bearer {token}
X-UIPATH-TenantName: {tenant}
X-UIPATH-FolderPath-Encoded: Base64(UTF-16LE(folderPath))
```

- 文件夹由请求头指定,`$filter` 只负责时间段
- 路径需按 Base64(UTF-16LE) 编码,中文文件夹名才不会出问题
- 按时间段分页拉取,直到返回不足一页
- 单个文件夹查询失败只跳过该文件夹,不影响其它文件夹;**不再回退模拟数据**,避免假数据入库

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

**Q: Job 表新增了 FolderId / FolderName,已有的 uipath_jobs.db 怎么办?**
A: 项目用 `EnsureCreated` 建库,不会给已存在的表自动加列。二选一:
   - 删掉 `uipath_jobs.db` 重启重建(会丢失已录入的 Remark / Tag)
   - 手动补列:`ALTER TABLE Jobs ADD COLUMN FolderId INTEGER NULL;` /
     `ALTER TABLE Jobs ADD COLUMN FolderName TEXT NULL;`

**Q: 配置了子文件夹但没查到数据?**
A: 按顺序排查:① `Path` 必须与 `/odata/Folders` 里的 `FullyQualifiedName` 完全一致(大小写不敏感);
   ② 账号需有该文件夹及子文件夹的读取权限;
   ③ 文件夹树有缓存,改完配置等 `FolderTreeCacheMinutes` 或重启应用。