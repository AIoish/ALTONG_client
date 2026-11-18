# Slack 연동 초기 작업 기록

운영·시연 기준은 [Slack 개인 DM OAuth 연결](SLACK_INTEGRATION.md)에 있다. 2026-11-18에 Cloudflare 배포, 실제 OAuth 및 알통 데모의 1:1 DM 수신을 확인했다.

## Cloudflare 배포 이전 인계 메모

사용자 요청으로 서버 연결·배포 작업을 여기서 중단했다. **실제 Slack OAuth 연결이나 Cloudflare 배포를 완료한 상태가 아니다.** 다음 요청 때 현재 미커밋 변경부터 확인하고 이어간다.

## 작업 상태

- 작업 폴더: `C:\Altong`, 브랜치: `feature/slack-personal-dm`.
- 기존 미커밋 변경을 보존했다. 이번 작업도 커밋·푸시하지 않았다.
- 목표: 이용자는 알통에서 Slack 연결을 눌러 로그인·워크스페이스 선택·권한 동의만 하고, 본인에게 오는 새 1:1 DM을 본인의 PC에서 받는다. 이용자에게 Slack 토큰을 복사·입력하도록 요구하지 않는다.
- 사용자는 상시 운영할 서버로 **Cloudflare**를 선택했다. 현재 PC나 Tunnel에만 의존하는 배포는 원하지 않는다. PC가 꺼져도 중앙 서비스가 동작해야 한다.

## 완료한 코드와 검증

### 기존 개인 개발 연결 복구

- `SlackConnectionController`: 연결 해제 때 암호화 토큰을 삭제하던 동작을 수신 중지·재개로 수정했다. 중지 여부를 별도 파일에 저장한다.
- 이 PC의 기존 `slack-tokens.dat`는 이미 없었다. 토큰을 복구하거나 새 값을 등록하지 않았다.
- PC 직접 Socket Mode는 `ALTONG_SLACK_LOCAL_DEVELOPMENT=1`에서만 사용하는 개발 모드로 분리했다. 테스터 PC에 복제하지 않는다.

### ASP.NET Core 중앙 서버와 Windows 클라이언트

- `src/Altong.Slack.Contracts`: 연결·수신 프로토콜.
- `src/Altong.Slack.Server`: OAuth 코드 교환, 사용자 인증 확인, 계정·기기 연결, 암호화 SQLite 저장, 사용자 토큰 갱신, 중앙 Socket Mode, 사용자별 DM 접근 검증·전달, 연결 해제.
- `SlackRelayConnectionController`, `SlackRelayApiClient`, `SlackRelaySessionStore`: 시스템 브라우저 승인, 승인 결과 자동 확인, 기기 권한의 DPAPI 보관, 기존 알림 파이프라인으로 수신, 재실행·해제·재승인.
- 설정 UI에 승인 대기·취소·연결 해제·오류 안내를 연결했다. 실제 서버 URL이 없으면 서비스 미설정 안내를 표시한다.
- 실행·배포 준비 도구: `tools/start-slack-server.ps1`, `tools/configure-slack-client.ps1`. 실제 Secret이나 서버 주소를 만들어 넣지 않았다.
- 서버 테스트 **22개 통과**. Windows·인접 알림 경로 테스트 **91개 통과** 기록이 있다. 이후 추가한 `SavedSession_RetriesTemporaryServerOutageWithoutReopeningApproval` 테스트는 아직 실행하지 않았다.
- Windows 컨트롤러와 실제 로컬 HTTP 서버를 연결한 모의 Slack 통합 테스트에서 승인 → 기기 연결 → DM의 기존 집중 세션 DB 저장 → 연결 해제 → 재승인이 통과했다.
- 서버 Release 빌드는 경고·오류 없이 통과했다. Windows Release 빌드는 실행 파일 복사 단계에서 기존 `Altong.Client.exe` 파일 잠금으로 실패했다. 사용자 앱을 강제 종료하지 않았다. 다음에 별도 출력 폴더로 빌드하거나 사용자 앱의 정상 종료 후 확인한다.
- 실제 Slack 계정의 OAuth·DM 수신은 검증하지 않았다. 실제 자격 증명도 읽거나 출력하지 않았다.

## Cloudflare 전환 — 진행 중이며 미검증

`src/Altong.Slack.Cloudflare`를 새로 만들었다.

- Wrangler `4.164.0`을 프로젝트 내부에 설치하고 `package-lock.json`을 생성했다. `node_modules`와 `.wrangler`, `.dev.vars`는 Git에서 제외했다.
- `wrangler whoami`로 기존 Cloudflare OAuth 로그인과 배포 가능한 단일 계정을 확인했다. 로그인이나 계정 설정을 바꾸지는 않았다.
- Cloudflare Workers + SQLite Durable Object에 서버를 올리는 방향이다. 기존 .NET/SQLite 서버를 Cloudflare의 임시 컨테이너 디스크에 그대로 배포하지 않는다.
- Workers용 `src/crypto.js`, `store.js`, `relay.js`, `index.js`와 `wrangler.jsonc` 초안을 작성했다.
- 이 초안은 Slack HTTP Events API를 사용한다. 공개 Worker가 Slack 서명·타임스탬프를 확인하고, Durable Object가 암호화된 승인·토큰·기기·전달 큐를 관리한다. Windows의 기존 HTTP 프로토콜을 유지하도록 작성했다.
- **Cloudflare용 코드는 테스트·dry run·배포를 아직 하지 않았다.** `npm test`에 필요한 테스트 파일도 아직 없다. 완성되었다고 간주하지 않는다.
- `wrangler.jsonc`의 `PUBLIC_URL`은 비어 있다. Worker URL을 추측해서 넣지 않았다. `RETENTION_HOURS=24`는 현재 초안 값으로, 실제 운영 정책이 확정된 것이 아니다.
- Cloudflare Worker, Durable Object namespace, 공개 URL, 유료 플랜을 생성·배포·변경하지 않았다.

## 확인된 Slack 공개 정보

- App ID: `A0CK8B7QPSL`
- Client ID: `12062444213687.12654381839904`
- 기존 설정 페이지: `https://app.slack.com/app-settings/T0C1UD269L7/A0CK8B7QPSL/general`
- 해당 페이지를 브라우저에서 열었으나 로그인 화면으로 이동해 상세 scope·구독·배포 상태는 확인하지 못했다.
- Secret이나 토큰을 채팅으로 요청하지 않는다. 필요한 서버 비밀은 담당자가 Cloudflare Secrets에 직접 입력한다.

## 다음에 이어갈 순서

1. 현재 미커밋 변경을 확인한다. 다른 작업 파일을 되돌리거나 덮어쓰지 않는다.
2. Cloudflare 초안을 검토·수정하고 Node/SQLite 모의 테스트와 Wrangler 로컬 검증을 먼저 추가한다. 기존 Windows 계약과 실제 HTTP 연동도 검증한다.
3. 특히 async 저장·승인 취소 경합, 토큰 갱신 잠금, 사용자별 전달·ACK, 재시도·보관 기간, Slack 서명 검증을 확인한다. Cloudflare 초안의 메시지 timestamp는 현재 밀리초 ISO로 만들어 원래 Slack 마이크로초 정밀도를 보존하도록 보완할 필요가 있다.
4. 계정의 기존 Worker를 조회해 이름 충돌을 확인한 뒤 새 Worker를 배포한다. 기존 다른 앱을 덮어쓰지 않는다. 유료 플랜이나 비용이 필요한 변경은 적용 전에 설명한다.
5. 실제 Worker HTTPS origin을 확인해 `PUBLIC_URL`과 Windows 배포 설정을 연결한다.
6. Slack Client Secret·Signing Secret·암호화 키·전체 승인 조회용 앱 수준 토큰을 Cloudflare Secrets에 설정한다. 원문을 출력하거나 저장소에 넣지 않는다. HTTP 방식의 앱 수준 토큰에는 `authorizations:read`가 필요하다.
7. 기존 Slack 앱에서 정확한 OAuth Redirect URL과 HTTP Events Request URL을 등록하고, User Token Scopes `im:history`, `im:read`, `users:read`, User Event `message.im`, 취소·제거 이벤트 수신을 확인한다. HTTP 수신으로 전환할 때 Socket Mode를 끈다. 현재는 이 설정을 변경하지 않았다.
8. 다른 workspace 배포 설정은 실제 앱 화면에서 확인한다. Marketplace 등록은 별개다.
9. 실제 승인·DM 수신과 두 사용자·다른 workspace의 수신 분리를 검증한 뒤에만 실제 연결 완료로 보고한다.

상세 .NET 구현 설명은 [SLACK_INTEGRATION.md](SLACK_INTEGRATION.md)에 있다. Cloudflare 전환이 완료되면 그 문서도 최종 배포 구조에 맞게 갱신한다.
