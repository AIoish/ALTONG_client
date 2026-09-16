# Altong.MockGenerator

알통 클라이언트의 알림 수신 및 AI 필터링 기능을 테스트하기 위한 모의 알림 발생 도구입니다.

## 실행 방법

```powershell
dotnet run --project tools/Altong.MockGenerator
```

## 연동 가이드 (수신 모듈 개발 참고)

* **발신 앱 감지**: Windows 보안 정책상 알림 헤더에는 실행 파일 이름(`Altong.MockGenerator`)이 고정 표시됩니다.
* **출처 텍스트 포맷**: 모의 앱 정보는 본문 하단 **AttributionText**에 `"{앱이름} · {발신자}"` 형태로 전달됩니다. (예: `Slack · 김철수 팀장`, `Chrome · 쿠팡`)
* **수신 처리**: 발신자가 MockGenerator일 경우, `AttributionText`를 `·` 기준으로 분리하여 `AppName`과 `Sender`로 사용하시면 됩니다.

> 💡 새로운 테스트 알림 템플릿이 필요하면 [`NotificationPresets.cs`](NotificationPresets.cs)에 추가하시면 됩니다.
