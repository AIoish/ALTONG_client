namespace Altong.Client.Models;

/// <summary>탭 머리의 ⋯ 메뉴 한 줄. 도안에 없는 보조 기능(새로고침, 여러 개 선택, 목록 보기 등)을 여기에 모은다.</summary>
public sealed record PageMenuItem(string Header, Action Run, bool IsEnabled = true, bool IsDestructive = false);
