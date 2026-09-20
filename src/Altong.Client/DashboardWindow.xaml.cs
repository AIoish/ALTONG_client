using System.Windows;

namespace Altong.Client;

/// <summary>
/// 집중 세션 상태와 세션 종료 리포트를 표시할 대시보드 화면 뼈대.
/// 실제 데이터는 공통 계약과 저장 계층이 확정된 뒤 연결한다.
/// </summary>
public partial class DashboardWindow : Window
{
    public DashboardWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
