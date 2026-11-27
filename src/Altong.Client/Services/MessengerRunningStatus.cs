using System.ComponentModel;
using System.Diagnostics;

namespace Altong.Client.Services;

internal readonly record struct MessengerRunningStatus(bool KakaoTalk, bool Slack, bool Discord)
{
    public bool AllStopped => !KakaoTalk && !Slack && !Discord;

    public static MessengerRunningStatus? Read()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            int sessionId = current.SessionId;
            var processes = Process.GetProcesses();
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in processes)
                {
                    if (process.SessionId == sessionId)
                        names.Add(process.ProcessName);
                }

                return new MessengerRunningStatus(
                    names.Contains("KakaoTalk"),
                    names.Contains("Slack"),
                    names.Contains("Discord") || names.Contains("DiscordCanary") || names.Contains("DiscordPTB"));
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
