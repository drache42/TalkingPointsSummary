namespace TalkingPointsSummary.Configuration;

public class AppSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string AnthropicApiKey { get; set; } = string.Empty;
    public string BrowserlessUrl { get; set; } = "http://browserless:3000";
    public SmtpSettings Smtp { get; set; } = new();

    /// <summary>
    /// Cron-style schedule: day of week (0=Sun, 1=Mon) and hour (24h).
    /// Defaults to Monday at 8 AM.
    /// </summary>
    public int ScheduleDayOfWeek { get; set; } = 1; // Monday
    public int ScheduleHour { get; set; } = 8;
}

public class SmtpSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
}
