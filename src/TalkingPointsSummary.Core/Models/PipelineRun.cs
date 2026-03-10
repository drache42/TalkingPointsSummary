namespace TalkingPointsSummary.Models;

/// <summary>
/// Outcome state recorded for a pipeline run.
/// </summary>
public enum PipelineRunRecordStatus
{
    /// <summary>
    /// The run has started but not yet finished.
    /// </summary>
    Started,

    /// <summary>
    /// The run completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The run failed.
    /// </summary>
    Failed
}

/// <summary>
/// Stored record describing a pipeline execution attempt.
/// </summary>
public class PipelineRun
{
    /// <summary>
    /// Database identifier for the run record.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Trigger source for the run, such as manual or schedule.
    /// </summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// Scheduled date represented by the run when triggered on a schedule.
    /// </summary>
    public DateTime? ScheduledDate { get; set; }

    /// <summary>
    /// Optional parent identifier when the run targets a single parent.
    /// </summary>
    public int? ParentId { get; set; }

    /// <summary>
    /// UTC time when the run started.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// UTC time when the run finished, if it has completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Recorded status for the run.
    /// </summary>
    public PipelineRunRecordStatus Status { get; set; } = PipelineRunRecordStatus.Started;

    /// <summary>
    /// Error message captured for a failed run.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Parent associated with the run when scoped to a single parent.
    /// </summary>
    public Parent? Parent { get; set; }
}