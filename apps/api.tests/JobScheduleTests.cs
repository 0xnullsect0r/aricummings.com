using Ari.Platform.Jobs;
using Cronos;
using Xunit;

namespace AriApi.Tests;

/// <summary>
/// Regression: the scheduler passed a non-UTC DateTime to Cronos, which threw on the first
/// tick and silently ended the job loop, so no cron job ever ran in production.
/// </summary>
public class JobScheduleTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    [Fact]
    public void Offset_time_does_not_throw_and_is_due_inside_the_window()
    {
        var expression = CronExpression.Parse("*/30 * * * *");
        var now = new DateTimeOffset(2026, 11, 21, 20, 29, 50, TimeSpan.FromHours(-8));
        Assert.True(JobHost.IsDue(expression, TimeZoneInfo.Utc, now, Window));
    }

    [Fact]
    public void Not_due_outside_the_window()
    {
        var expression = CronExpression.Parse("*/30 * * * *");
        var now = new DateTimeOffset(2026, 11, 22, 4, 10, 0, TimeSpan.Zero);
        Assert.False(JobHost.IsDue(expression, TimeZoneInfo.Utc, now, Window));
    }
}
