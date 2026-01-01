using Agent.Core;
using Agent.Core.Configuration;
using Agent.Core.Models;
using Microsoft.Extensions.Options;

namespace Agent.Worker;

/// <summary>
/// Background service that runs the Diagnostics Agent on a schedule.
/// </summary>
public sealed class DiagnosticsWorker(
    DiagnosticsAgent agent,
    IOptions<AgentConfiguration> config,
    ILogger<DiagnosticsWorker> logger) : BackgroundService
{
    private ScheduleConfiguration Schedule => config.Value.Schedule;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Diagnostics Worker started. Running every {Hours} hours.", Schedule.IntervalHours);

        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (ShouldRunNow())
            {
                try
                {
                    await RunAnalysisAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during analysis run. Will retry next cycle.");
                }
            }
            else
            {
                logger.LogInformation("Skipping run due to quiet hours or weekend restrictions");
            }

            var nextRun = TimeSpan.FromHours(Schedule.IntervalHours);
            logger.LogInformation("Next analysis scheduled at {NextRun}",
                DateTime.UtcNow.Add(nextRun).ToString("yyyy-MM-dd HH:mm:ss UTC"));

            await Task.Delay(nextRun, stoppingToken);
        }

        logger.LogInformation("Diagnostics Worker shutting down.");
    }

    private bool ShouldRunNow()
    {
        var now = DateTime.UtcNow;

        if (!Schedule.EnableWeekendRuns && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        if (Schedule.QuietHoursStart is { } start && Schedule.QuietHoursEnd is { } end)
        {
            var currentTime = TimeOnly.FromDateTime(now);
            if (start < end)
            {
                if (currentTime >= start && currentTime <= end)
                    return false;
            }
            else
            {
                if (currentTime >= start || currentTime <= end)
                    return false;
            }
        }

        return true;
    }

    private async Task RunAnalysisAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting scheduled analysis at {Time}", DateTime.UtcNow);

        var request = new AnalysisRequest
        {
            HoursToAnalyze = Schedule.HoursToAnalyze,
            CreatePullRequest = Schedule.AutoCreatePullRequests,
            CreateIssues = Schedule.AutoCreateIssues,
            MinOccurrences = Schedule.MinExceptionOccurrences,
            MaxExceptionsToAnalyze = Schedule.MaxExceptionsPerRun
        };

        var startTime = DateTime.UtcNow;
        var report = await agent.AnalyzeAndFixAsync(request, cancellationToken);
        var duration = DateTime.UtcNow - startTime;

        LogReport(report, duration);
    }

    private void LogReport(DiagnosticReport report, TimeSpan duration)
    {
        logger.LogInformation("Duration: {Duration:mm\\:ss}", duration);
        logger.LogInformation("Period analyzed: {Period} hours", report.AnalysisPeriod.TotalHours);

        logger.LogInformation("Summary: {Summary}", report.Summary);

        logger.LogInformation("Exceptions Found: {Count} ({Critical} critical)",
            report.TotalExceptions, report.CriticalExceptions);

        foreach (var ex in report.Exceptions.Take(5))
        {
            var icon = ex.Severity switch
            {
                "Critical" => "[CRIT]",
                "High" => "[HIGH]",
                "Medium" => "[MED]",
                _ => "[LOW]"
            };
            logger.LogInformation("   {Icon} {Type} - {Count} occurrences",
                icon, ex.ExceptionType, ex.OccurrenceCount);
        }

        if (report.Exceptions.Count > 5)
        {
            logger.LogInformation("   ... and {More} more", report.Exceptions.Count - 5);
        }

        logger.LogInformation("");
        logger.LogInformation("Fixes Proposed: {Count}", report.TotalFixes);

        foreach (var fix in report.ProposedFixes)
        {
            logger.LogInformation("   [{Confidence}] {File}",
                fix.Confidence, fix.FilePath);
            var explanation = fix.Explanation.Length > 80
                ? $"{fix.Explanation[..80]}..."
                : fix.Explanation;
            logger.LogInformation("      {Explanation}", explanation);
        }

        logger.LogInformation("");

        if (!string.IsNullOrEmpty(report.PullRequestUrl))
        {
            logger.LogInformation("Pull Request: {Url}", report.PullRequestUrl);
        }

        if (!string.IsNullOrEmpty(report.IssueUrl))
        {
            logger.LogInformation("Issue: {Url}", report.IssueUrl);
        }

        logger.LogInformation("Analysis complete");
    }
}
