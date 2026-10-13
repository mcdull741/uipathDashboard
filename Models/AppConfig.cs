using System.ComponentModel.DataAnnotations;

namespace UiPathJobManager.Models;

public class AppConfig
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string ConfigKey { get; set; } = string.Empty;

    [Required]
    public string ConfigValue { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Description { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class LoginViewModel
{
    [Required(ErrorMessage = "用户名不能为空")]
    [MaxLength(50)]
    [Display(Name = "用户名")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "密码不能为空")]
    [MaxLength(100)]
    [DataType(DataType.Password)]
    [Display(Name = "密码")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "记住我")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class JobQueryViewModel
{
    [Display(Name = "开始时间")]
    public DateTime? StartDate { get; set; }

    [Display(Name = "结束时间")]
    public DateTime? EndDate { get; set; }

    [Display(Name = "状态")]
    public string? State { get; set; }

    [Display(Name = "流程名")]
    public string? ProcessName { get; set; }

    [Display(Name = "关键字")]
    public string? Keyword { get; set; }

    public int PageIndex { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    public List<Job> Results { get; set; } = new();
    public int TotalCount { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

public class JobEditViewModel
{
    public long Id { get; set; }

    [Display(Name = "流程名")]
    public string ProcessName { get; set; } = string.Empty;

    [Display(Name = "状态")]
    public string State { get; set; } = string.Empty;

    [Display(Name = "开始时间")]
    public DateTime StartTime { get; set; }

    [Display(Name = "结束时间")]
    public DateTime? EndTime { get; set; }

    [Display(Name = "机器人")]
    public string? RobotName { get; set; }

    [Display(Name = "机器")]
    public string? MachineName { get; set; }

    [Display(Name = "输入参数")]
    public string? InputArguments { get; set; }

    [Display(Name = "输出参数")]
    public string? OutputArguments { get; set; }

    [Display(Name = "错误信息")]
    public string? FaultReason { get; set; }

    [MaxLength(500)]
    [Display(Name = "备注")]
    public string? Remark { get; set; }

    [MaxLength(50)]
    [Display(Name = "标签")]
    public string? Tag { get; set; }
}

public class SyncStatusViewModel
{
    public DateTime? LastSyncTime { get; set; }
    public int TotalJobs { get; set; }
    public int TodayJobs { get; set; }
    public int SuccessfulToday { get; set; }
    public int FailedToday { get; set; }
    public int RunningNow { get; set; }
    public List<ChartDataPoint> TrendData { get; set; } = new();
    public List<ChartDataPoint> StateDistribution { get; set; } = new();
    public List<ChartDataPoint> TopProcesses { get; set; } = new();
}

public class ChartDataPoint
{
    public string Label { get; set; } = string.Empty;
    public double Value { get; set; }
}