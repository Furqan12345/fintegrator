namespace SimpleIPaaS.Application.Models;

public class ExecutionOptions
{
    public int MaxConcurrency { get; set; } = 4;

    // A run still going after this long is stopped and Failed (with a dead letter for the card it was on).
    public int MaxFlowDurationSeconds { get; set; } = 600;

    public int PollIntervalSeconds { get; set; } = 2;

    // On a graceful stop the Engine stops claiming and gives in-flight runs this long to finish; whatever is still
    // running afterwards is Failed with a shutdown message and dead-lettered. Keep it below the platform's stop
    // timeout (Docker sends SIGKILL 10 s after SIGTERM by default: set `stop_grace_period`).
    public int ShutdownDrainSeconds { get; set; } = 25;

    // A run InProgress for longer than MaxFlowDurationSeconds plus this grace cannot be alive (the Engine enforces
    // the maximum), so its Engine died: the recovery sweep marks it Failed.
    public int StaleRunGraceSeconds { get; set; } = 300;
}
