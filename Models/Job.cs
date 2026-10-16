using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UiPathJobManager.Models;

[Table("Jobs")]
public class Job
{
    [Key]
    public long Id { get; set; } // UiPath JobId，作为主键保证增量同步

    [Required, MaxLength(100)]
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>来源文件夹的 Orchestrator FolderId</summary>
    public long? FolderId { get; set; }

    /// <summary>来源文件夹全路径，例如 Finance/AP</summary>
    [MaxLength(200)]
    public string? FolderName { get; set; }

    [MaxLength(100)]
    public string? RobotName { get; set; }

    [MaxLength(100)]
    public string? MachineName { get; set; }

    [Required, MaxLength(20)]
    public string State { get; set; } = string.Empty; // Successful / Failed / Pending / Running / Stopped

    public string? InputArguments { get; set; }

    public string? OutputArguments { get; set; }

    public string? FaultReason { get; set; }

    public DateTime StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    [MaxLength(100)]
    public string? Key { get; set; }

    [MaxLength(100)]
    public string? ReleaseName { get; set; }

    [MaxLength(100)]
    public string? ReleaseVersion { get; set; }

    // 本地扩展字段
    [MaxLength(500)]
    public string? Remark { get; set; }

    [MaxLength(50)]
    public string? Tag { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;

    [NotMapped]
    public string StateBadgeClass => State switch
    {
        "Successful" => "success",
        "Failed" => "danger",
        "Running" => "primary",
        "Pending" => "warning",
        "Stopped" => "secondary",
        _ => "info"
    };
}