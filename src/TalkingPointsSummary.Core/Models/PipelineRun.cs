namespace TalkingPointsSummary.Models;

public enum PipelineRunRecordStatus
{
    Started,
    Completed,
    Failed
}

public class PipelineRun
{
    public int Id { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public DateTime? ScheduledDate { get; set; }
    public int? ParentId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public PipelineRunRecordStatus Status { get; set; } = PipelineRunRecordStatus.Started;
    public string? Error { get; set; }

    public Parent? Parent { get; set; }
}