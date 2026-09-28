using System.Text;
using System.Text.Json;
using Fabric.Deployment.Core.Configuration;
using Fabric.Deployment.Core.Engine;

namespace Fabric.Deployment.Core.Steps;

/// <summary>
/// Posts the outcome to a Teams (Workflows "incoming webhook") or Slack webhook.
/// The URL is read from the env var named in notifications.webhookUrlEnvVar (keep it a secret).
/// Never fails the run — a broken webhook only logs a warning.
/// </summary>
public sealed class NotificationStep : IDeploymentStep
{
    private readonly HttpClient _http;
    public NotificationStep(HttpClient? http = null) => _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public string Name => "Notification";
    public bool AlwaysRun => true;

    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        var cfg = ctx.Settings.Notifications;
        var url = Environment.GetEnvironmentVariable(cfg.WebhookUrlEnvVar);
        if (string.IsNullOrWhiteSpace(url) || cfg.When == NotifyWhen.Never) return;

        var bad = ctx.Outcome is RunOutcome.Failed or RunOutcome.PartiallySucceeded or RunOutcome.RolledBack or RunOutcome.Rejected;
        if (cfg.When == NotifyWhen.OnFailure && !bad) return;

        var (title, lines) = Summarise(ctx, cfg);
        object payload = cfg.Format == NotificationFormat.Slack
            ? new { text = $"*{title}*\n" + string.Join("\n", lines) }
            : new
            {
                type = "message",
                attachments = new[]
                {
                    new
                    {
                        contentType = "application/vnd.microsoft.card.adaptive",
                        content = new Dictionary<string, object>
                        {
                            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                            ["type"] = "AdaptiveCard",
                            ["version"] = "1.4",
                            ["body"] = new object[]
                            {
                                new { type = "TextBlock", text = title, weight = "Bolder", size = "Medium", wrap = true,
                                      color = bad ? "Attention" : "Good" },
                                new { type = "TextBlock", text = string.Join("\n\n", lines), wrap = true }
                            }
                        }
                    }
                }
            };

        try
        {
            using var resp = await _http.PostAsync(url,
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ctx.CancellationToken);
            if (resp.IsSuccessStatusCode) ctx.Log.Info("Notification sent");
            else ctx.Log.Warn($"Notification webhook returned {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log.Warn($"Notification failed: {ex.Message}"); }
    }

    internal static (string Title, List<string> Lines) Summarise(DeploymentContext ctx, NotificationSettings cfg)
    {
        var icon = ctx.Outcome switch
        {
            RunOutcome.Succeeded => "✅", RunOutcome.RolledBack => "↩️", RunOutcome.DryRun or RunOutcome.NothingToDeploy => "ℹ️",
            RunOutcome.PartiallySucceeded => "⚠️", _ => "❌"
        };
        var title = $"{icon} Fabric deployment {ctx.Outcome}: {ctx.SourceStage?.DisplayName ?? ctx.Settings.Deployment.SourceStage} → " +
                    $"{ctx.TargetStage?.DisplayName ?? ctx.Settings.Deployment.TargetStage} ({ctx.Pipeline?.DisplayName ?? ctx.Settings.Pipeline.DisplayName})";
        var lines = new List<string>
        {
            $"Run {ctx.RunId} by {ctx.InitiatedBy}" + (ctx.FinishedAt is { } f ? $", {(f - ctx.StartedAt).TotalSeconds:0}s" : ""),
        };
        if (ctx.Plan is { } p)
            lines.Add($"{p.Items.Count} item(s): {p.Items.Count(i => i.Action == PlannedAction.Create)} new, {p.Items.Count(i => i.Action == PlannedAction.Update)} updated");
        if (ctx.Rollback is { } r)
            lines.Add($"Rollback: {r.Restored.Count} restored, {r.Deleted.Count} deleted, {r.Failed.Count} failed");
        lines.AddRange(ctx.Errors.Take(5).Select(e => "• " + e));
        var runUrl = cfg.RunUrl ?? GitHubRunUrl();
        if (runUrl is not null) lines.Add(runUrl);
        return (title, lines);
    }

    private static string? GitHubRunUrl()
    {
        var server = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL");
        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        return server is null || repo is null || run is null ? null : $"{server}/{repo}/actions/runs/{run}";
    }
}
