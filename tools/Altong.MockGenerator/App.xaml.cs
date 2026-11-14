using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Altong.MockGenerator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.SequenceEqual(["--notification-control-test"]))
        {
            try
            {
                new ToastContentBuilder().AddText("알통 알림 제어 테스트")
                    .AddText("테스트용 앱의 배너·알림음 설정을 확인합니다.").Show();
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        base.OnStartup(e);
    }
}
