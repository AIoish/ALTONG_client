# Slack 개인 DM OAuth 연결

2026-11-18 기준. 기존 Altong Slack 앱의 OAuth와 중앙 수신 서버, Windows 기기별 전달 코드를 구현했다.

`altong-slack` Worker는 `https://altong-slack.commonworks.workers.dev`에 배포되어 있다. Slack Request URL 검증, 실계정 OAuth 연결, **알통 데모** 워크스페이스의 실제 1:1 DM 수신을 확인했다. 운영 구성은 [Cloudflare Worker](#cloudflare-worker), 전환 전 작업 기록은 [SLACK_HANDOFF.md](SLACK_HANDOFF.md)에 있다.

## 사용자가 거치는 흐름

1. 설정 → 앱 연결 → **Slack 연결**을 누른다.
2. 시스템 브라우저에서 기존 Altong Slack 앱의 로그인·워크스페이스 선택·권한 동의를 진행한다.
3. 서버가 승인 코드를 사용자 토큰으로 교환하고 Slack 계정을 확인한다.
4. PC가 자신의 일회용 연결 증명으로 완료 결과를 가져와 자동으로 수신한다.
5. **연결 해제** 후에는 같은 승인 절차로 다시 연결할 수 있다.

이용자는 Slack 토큰이나 Client Secret을 입력하지 않는다. 배포 앱에는 운영자가 정한 공개 서버 URL을 포함한다.

알통은 PC마다 Slack 계정 하나를 한 워크스페이스에 연결한다. 설정의 **앱 연결**에서 현재 계정과 워크스페이스를 확인하고 연결을 해제할 수 있다.

## 구현 구조

- `Altong.Slack.Cloudflare`는 운영 백엔드다. Slack HTTPS Events API와 OAuth 콜백을 받아 SQLite Durable Objects를 통해 승인된 Windows 기기로 전달한다.
- `Altong.Slack.Server`는 ASP.NET Core 호환 서버다. 이 서버를 사용하는 별도 환경에서는 중앙 Socket Mode 수신기 한 곳으로 이벤트를 받는다.
- `Altong.Slack.Contracts`: 비밀이 아닌 연결·수신 프로토콜과 기기 자격 증명 DTO.
- `SlackRelayConnectionController`: Windows의 브라우저 승인·자동 수신·연결 복원·해제.
- `SlackRelaySessionStore`: 서버 URL에 묶인 알통 기기 자격 증명을 Windows DPAPI로 보관.
- 기존 `SlackConnectionController`와 Socket Mode 수신기: 명시적으로 켠 개인 개발 모드에서만 사용.

기존 알통 로그인 시스템이 없으므로, 검증된 Slack `(team_id, user_id)`에 내부 알통 사용자 ID를 발급한다. 같은 Slack 계정을 다시 승인하면 같은 내부 사용자를 복원한다. 여러 Slack 계정을 하나의 사용자로 합치지는 않는다. 같은 계정으로 승인한 여러 PC에는 각각 기기 권한을 발급하고, 연결되어 있던 각 PC로 전달한다.

ASP.NET Core 호환 서버는 수신기 파일 잠금을 사용하고, 하나의 Slack 앱에 대해 인스턴스 하나를 운영한다. Socket Mode의 이벤트는 열린 연결들에 분산될 수 있다. [Slack Socket Mode](https://docs.slack.dev/apis/events-api/using-socket-mode/)

## 승인과 수신의 보호 장치

- 10분 만료·일회용 OAuth state를 검증하고 callback 재사용을 막는다. PC가 생성한 임의 증명의 해시를 연결 요청에 묶는다. 요청 ID나 브라우저 URL만 알아서는 기기 권한을 가져올 수 없다.
- 승인 취소·실패·만료 시 연결됨으로 표시하지 않는다. 앱에서 승인 대기를 취소하면 서버의 요청도 취소하고 늦게 온 callback으로 연결을 되살릴 수 없게 한다.
- `oauth.v2.access`의 **authed_user.access_token**만 개인 DM 접근에 사용한다. app ID·team ID·user ID·token type·필수 scope를 확인한다. 최상위 bot token은 사용하지 않는다.
- Slack의 계정·워크스페이스 이름을 표시하며 이름은 식별 키로 쓰지 않는다. API 오류·응답 본문·서명된 소켓 URL·DM 내용은 로그에 넣지 않는다.
- 서버 토큰·승인 완료 결과·수신 원문·전달 본문은 AES-256-GCM으로 암호화한다. 암호문은 테이블과 레코드 ID에 결합한다. 기기 인증용 서버 DB에는 원래 자격 증명 대신 해시를 보관한다.
- 사용자별 잠금으로 OAuth 재승인·토큰 갱신·API 접근·연결 해제를 직렬화한다. 회전 토큰은 만료 2분 이내에 다음 API 호출 시 갱신하고 새 access/refresh token·만료를 함께 저장한다.
- Slack 확인 응답은 암호화된 SQLite inbox 저장 후 보낸다. 사용자 프로필 조회나 AI 완료를 기다리지 않는다. 서버 재시작 후에도 inbox와 전달 큐를 복원한다.
- `event_context`가 있으면 `apps.event.authorizations.list`의 모든 페이지를 조회한다. 없으면 이벤트에 명시된 사용자 승인만 후보로 쓴다. `authorizations[0]`, 발신자 ID, 워크스페이스 전체 사용자를 수신자로 가정하지 않는다.
- 각 후보의 사용자 토큰으로 `conversations.info`를 호출해 1:1 DM 접근을 확인한다. 자기 발신·그룹 DM·채널·봇·수정/삭제·빈 본문은 제외한다. 접근 확인 실패는 전달하지 않는다.
- 기기 요청에 사용자 ID를 입력받지 않는다. 인증된 기기에서 내부 사용자를 결정하며, 다른 기기의 메시지 ID로 ACK해도 삭제되지 않는다.
- 이벤트·기기별 전달을 중복 제거하고 ACK 영수증을 보관해 처리 재시도로 이미 확인한 전달이 되살아나지 않게 한다. 호출 제한은 해당 이벤트를 지연시키며 다른 이벤트 처리는 계속한다.
- 마지막 PC 연결 해제 시 서버의 사용자 토큰과 모든 대기 전달을 제거하고 해당 사용자 토큰의 `auth.revoke`를 시도한다. 외부 revoke 실패 시에도 서버 접근은 이미 차단된다. 다른 PC가 연결되어 있으면 해당 PC만 해제한다. Slack 앱 전체 제거는 하지 않는다.
- `tokens_revoked` 이벤트는 현재 grant를 다시 확인해 취소된 사용자만 차단한다. 지연된 과거 revoke 이벤트가 새 승인을 지우지 않게 한다. `app_uninstalled`는 해당 워크스페이스 연결을 차단한다.

OAuth·회전 토큰·전체 승인 조회 근거: [OAuth](https://docs.slack.dev/authentication/installing-with-oauth/), [token rotation](https://docs.slack.dev/authentication/using-token-rotation/), [전체 승인 조회](https://docs.slack.dev/reference/methods/apps.event.authorizations.list/).

## 실제 Slack 앱 설정

앱 소유자가 제공한 공개 값:

| 항목 | 값 |
| --- | --- |
| App ID | `A0CK8B7QPSL` |
| Client ID | `12062444213687.12654381839904` |
| 현재 설정 페이지의 워크스페이스 | `T0C1UD269L7` |

다음은 Cloudflare 배포에서 사용하는 Slack 설정이다.

1. **OAuth & Permissions → User Token Scopes**: `im:history`, `im:read`, `users:read`.
2. **OAuth & Permissions → Redirect URLs**에 `https://altong-slack.commonworks.workers.dev/oauth/slack/callback`을 등록한다.
3. **Event Subscriptions**에서 **Request URL**을 `https://altong-slack.commonworks.workers.dev/slack/events`로 지정하고 Slack의 **Verified**를 확인한다. **User Events**에 `message.im`을 등록해 저장한다.
4. **Socket Mode**는 끈 상태로 사용한다. 앱 수준 토큰의 `authorizations:read` 권한으로 이벤트 승인 목록을 조회한다.
5. 현재 데모는 앱을 만든 **알통 데모** 워크스페이스에서 승인할 수 있다. 다른 워크스페이스 설치에는 **Manage Distribution → Activate Public Distribution**이 필요하다. 활성화 요청이 Slack의 `ratelimited` 응답으로 거절되어 현재는 단일 워크스페이스로 운영한다. [Slack 배포 안내](https://docs.slack.dev/app-management/distribution/)
6. 사용자 권한을 바꾸면 영향을 받는 기존 연결을 다시 승인한다.

## ASP.NET Core 호환 서버 실행

현재 저장소의 SDK와 맞춘 `net9.0` 프로젝트다. 실제 호스팅 환경을 가정하거나 자동 배포하지 않았다. 실행·publish 전 배포 환경의 런타임을 확인한다. 단일 인스턴스·영속 데이터 폴더·HTTPS 공개 origin이 필요하다.

호환 서버 전용 Slack 앱에서 **Socket Mode**를 켜고 앱 수준 토큰에 `connections:write`와 `authorizations:read`를 부여한다. 해당 서버의 공개 origin을 기준으로 `/oauth/slack/callback`을 Redirect URL에 등록한다.

`src/Altong.Slack.Server/.env.example`은 이름 참고용이며 자동으로 읽히는 설정 파일이 아니다. 실행 환경 또는 비밀 저장소에서 다음 환경 변수를 설정한다.

| 환경 변수 | 설정 |
| --- | --- |
| `ALTONG_SLACK_APP_ID` | 위 App ID. 실행 스크립트에 기본 공개 값 반영 |
| `ALTONG_SLACK_CLIENT_ID` | 위 Client ID. 실행 스크립트에 기본 공개 값 반영 |
| `ALTONG_SLACK_PUBLIC_URL` | 실제 HTTPS origin. 경로·query·fragment·인증 정보를 넣지 않음 |
| `ALTONG_SLACK_CLIENT_SECRET` | 기존 Altong Slack 앱의 Client Secret. 서버 비밀 저장소에 직접 입력 |
| `ALTONG_SLACK_APP_TOKEN` | 위 두 앱 수준 scope를 가진 토큰. 서버 비밀 저장소에 직접 입력 |
| `ALTONG_SLACK_ENCRYPTION_KEY` | 안전하게 생성한 32바이트 키의 Base64 값. 서버 비밀 저장소에 보존 |
| `ALTONG_SLACK_DATA_DIR` | 서버 DB를 보관할 실제 영속 폴더의 절대 경로 |
| `ALTONG_SLACK_RETENTION_HOURS` | 운영자가 정한 수신·전달 데이터 보관 시간. 1~168시간 |
| `ALTONG_SLACK_ALLOWED_TEAMS` | 선택 사항. 쉼표로 구분한 테스트 workspace ID. 비우면 승인한 모든 workspace 허용 |
| `ASPNETCORE_URLS` | 실제 호스팅·TLS proxy 설정에 맞는 로컬 수신 주소 |

암호화 키를 서버를 시작할 때마다 새로 만들면 기존 연결을 복호화할 수 없다. DB와 키를 별도로 안전하게 보존한다. Secret과 토큰을 채팅·명령줄 인자·소스 파일에 넣지 않는다.

설정 후 프로젝트 루트에서 실행한다.

```powershell
powershell -NoProfile -File .\tools\start-slack-server.ps1 -ValidateOnly
powershell -NoProfile -File .\tools\start-slack-server.ps1
```

스크립트는 누락된 설정 **이름**만 알리고 값은 출력하지 않는다. 실제 값이 없으면 시작하지 않는다. 소유자가 제공한 두 공개 ID만 기본으로 넣는다.

HTTPS proxy는 `/oauth/slack/callback`과 `/v1/slack/*`를 같은 서버로 전달하고, 메시지 long poll의 20초보다 긴 요청 제한을 적용한다. OAuth query나 Authorization 헤더를 proxy 로그에 기록하지 않는다. 공개 URL은 설정에서 가져오며 임의 Host 헤더나 외부 redirect 인자로 구성하지 않는다. 개발 환경(`ASPNETCORE_ENVIRONMENT=Development`)에서만 HTTP loopback origin을 허용한다.

## Windows 배포 설정

실제 서버를 정한 후 공개 URL을 설정해 빌드·배포한다.

```powershell
# 실제 HTTPS origin을 인자로 전달한다. Secret이나 Slack 토큰을 전달하지 않는다.
powershell -NoProfile -File .\tools\configure-slack-client.ps1 -ServerUrl $actualSlackServerUrl
dotnet publish .\src\Altong.Client\Altong.Client.csproj -c Release
```

도구는 `src/Altong.Client/slack-service.json`에 공개 `ServerUrl`만 기록한다. 이 파일은 로컬 배포 설정으로 Git에서 제외하며 빌드·publish 시 실행 파일 옆으로 복사한다. 테스터에게는 설정된 앱을 전달하므로 별도 환경 변수나 토큰 입력이 필요 없다.

개발 시에는 `ALTONG_SLACK_SERVER_URL`로 공개 설정을 덮어쓸 수 있다. Release 클라이언트는 HTTPS origin만 허용하고 Debug는 HTTP loopback도 허용한다. 서버 URL이 없거나 잘못되면 버튼에 연결 서비스 미설정 안내가 나온다. 설정되지 않은 상태를 연결 성공으로 표시하지 않는다.

기기 자격 증명은 `%LOCALAPPDATA%\Altong\slack-relay-session.dat`에 현재 Windows 사용자 DPAPI로 암호화한다. 이전 다중 연결 빌드에서 별도 하위 폴더에 저장했다면 기본 연결을 우선하고, 없으면 현재 서버에 저장된 연결 하나를 복원한다. 앱 재시작은 저장된 기기 권한으로 수신을 재개하며 새 사용자의 브라우저는 자동으로 열지 않는다. 권한이 만료·취소되면 **다시 연결**로 재승인한다. 일반 앱 종료는 서버 연결을 해제하지 않는다.

## 기존 개인 개발 모드

`ALTONG_SLACK_LOCAL_DEVELOPMENT=1`일 때만 기존 PC 직접 Socket Mode를 쓴다. `tools/setup-slack.ps1`과 `slack-tokens.dat`는 이 모드 전용이며 테스터에게 배포하지 않는다. 중앙 서버와 동시에 같은 앱을 연결하지 않는다.

이 모드의 **수신 중지**는 토큰 파일을 보존하고 `slack-tokens.dat.paused`에 중지 여부만 저장한다. 재실행 후에도 중지하고 **다시 연결**로 재개한다. 과거 코드가 삭제한 로컬 토큰을 이번 수정으로 복구하지는 못한다. 기본 OAuth 모드는 이 파일을 필요로 하지 않는다.

`ALTONG_ENABLE_SLACK=0`은 두 모드 모두 시작을 막으며 저장된 연결은 보존한다.

## 범위와 한계

- 현재 한 알통 사용자당 한 Slack workspace/user 조합을 사용한다. 다른 workspace 연결은 별도 내부 사용자로 식별한다. 여러 계정 통합이나 Enterprise 조직 단위 설치는 별도 기능이다.
- 보관 시간은 자동 삭제 기준이며 테스터 배포 전에 운영자가 정해야 한다. 서버는 명시적으로 승인되어 있던 기기의 새 DM을 보관 시간 안에서 재전달한다. 연결 전 과거 대화는 조회하지 않는다.
- PC에 도착한 시점의 집중 상태·세션·작업 맥락을 기존 pending 경로에서 잡는다. 서버는 PC 화면 맥락이나 AI 평가를 수집하지 않는다.
- PC ACK는 기존 파이프라인으로 인계한 시점이다. AI 완료·집중 중 DB 저장까지 원자적으로 보장하는 ACK는 아니다. 이 짧은 구간의 프로세스 강제 종료는 누락될 수 있다. 앱이 실행 중일 때는 재시도 중복을 메모리에서 제거하고, 집중 중 이미 DB에 저장한 메시지는 재평가하지 않는다.
- 마지막 기기의 외부 Slack revoke가 실패하면 서버 연결은 해제되지만 Slack의 앱 설치·승인 표시가 남을 수 있다. 서버의 내부 접근 차단과 외부 앱 제거는 구분한다.
- 시연 범위는 PC당 한 계정·한 워크스페이스다. Slack 공개 배포 활성화와 실제 다중 사용자·워크스페이스 검증은 후속 작업이다.
- 알통 공통 로그인이 추가되면 기존 Slack `(team_id, user_id)`와 내부 계정의 매핑 및 기존 기기 권한을 이전하는 절차가 필요하다.

## 검증

서버 테스트는 state 재사용·증명 소유·사용자별 접근·암호화 저장·기기별 ACK·토큰 회전·연결 해제·재승인·HTTP 프로토콜을 모의 Slack으로 검증한다. Windows 통합 테스트는 실제 로컬 HTTP 서버와 클라이언트 컨트롤러를 연결해 브라우저 승인 콜백 → 기기 권한 수령 → DM의 기존 집중 세션 저장 → 해제 → 재승인을 검증한다. 실제 Slack 토큰은 사용하지 않는다.

```powershell
dotnet test .\tests\Altong.Slack.Server.Tests\Altong.Slack.Server.Tests.csproj
dotnet test .\tests\Altong.Client.Tests\Altong.Client.Tests.csproj --filter "FullyQualifiedName~Slack|FullyQualifiedName~AppConnectionsUiTests"
```

| 확인 항목 | 결과 |
| --- | --- |
| 로컬 검증 | Cloudflare 모의 Slack, ASP.NET Core 서버, Windows 앱과 로컬 Wrangler 연동 통과 |
| Worker 배포 | 운영 Worker의 `/health`에서 `configured: true`와 `RELAY`, `INBOX` 확인 |
| 비밀값·Slack 설정 | 필수 Secret 4개와 Events API Request URL의 **Verified** 확인, Socket Mode 끔 |
| 실제 OAuth·1:1 DM | 알통 데모에서 연결·재실행 복원 확인, 사용자가 실제 수신 확인 |
| 사용자별 분리와 해제·재승인 | 모의 환경에서 확인. 실제 다중 사용자/워크스페이스·해제 후 재승인 검증은 후속 작업 |

## Cloudflare Worker

- Slack은 `POST /slack/events`로 이벤트를 보낸다. Worker가 원본 바이트의 HMAC을 검증하고, 메시지 이벤트를 워크스페이스별 SQLite `SlackInbox`에 암호화해 저장한다. 저장과 재시도 알람 등록을 마치면 200을 반환한다. Slack 대화 조회는 백그라운드에서 처리한다. [Slack Events API](https://docs.slack.dev/apis/events-api/)
- 기존 `SlackRelay` SQLite 객체는 승인 요청과 사용자·기기·전달 데이터를 별도 테이블에 저장한다. `v1` 마이그레이션과 기존 객체 이름을 유지하고, `v2`에서 워크스페이스별 이벤트 수신함을 추가한다. 승인 취소와 기기 발급은 한 SQLite 트랜잭션에서 처리할 수 있다. 워크스페이스별 수신함은 독립적으로 재시도하고, 사용자 데이터는 워크스페이스 ID와 사용자 ID 조합으로 조회한다.
- 전체 승인 목록에서 같은 워크스페이스의 사용자만 후보로 삼는다. 본인이 보낸 메시지, 봇 메시지, 수정·삭제, 그룹 DM과 외부 워크스페이스 공유 대화를 제외한다. 수신자 토큰으로 `conversations.info`를 호출해 채널과 상대를 확인하고, 이벤트 수신 당시 활성 상태인 각 PC에 전달한다. ACK를 받은 기기의 항목은 삭제한다. 미확인 전달과 이벤트 재시도 정보는 `RETENTION_HOURS`(기본 24시간) 동안 보관한다. [전체 승인 조회](https://docs.slack.dev/reference/methods/apps.event.authorizations.list/) · [대화 조회](https://docs.slack.dev/reference/methods/conversations.info/)
- 새 Windows 클라이언트는 Cloudflare에서 WebSocket 수신 모드를 확인하고 기존 기기 자격 증명으로 `/v1/slack/stream`에 연결한다. Durable Object는 `acceptWebSocket`으로 PC 소켓을 등록한다. 대기 메시지가 있거나 새 DM이 들어오면 해당 기기 소켓에 1바이트 신호를 보낸다. PC는 신호를 받을 때만 HTTPS로 대기 메시지를 가져와 ACK한다. 기기 권한 취소 시 해당 소켓을 닫는다. 재연결하면 아직 ACK하지 않은 메시지가 있을 때 신호를 다시 보낸다. [Cloudflare WebSocket Hibernation](https://developers.cloudflare.com/durable-objects/best-practices/websockets/)
- 정상 연결이 유지되는 동안 DM이 없으면 PC의 메시지 HTTP 조회는 0건이다. Windows는 WebSocket 프로토콜 Ping/Pong으로 연결 상태를 확인하고, 이 Ping은 Durable Object 요청 한도에 포함되지 않는다. 초기 연결·재연결, Slack 이벤트, 실제 DM 조회와 ACK 등은 요청량에 합산한다. 기존 Windows 빌드에는 HTTP 조회 호환 경로를 제공한다. .NET 서버를 사용하는 클라이언트는 기존 HTTP 수신을 사용한다. [Cloudflare 요금표](https://developers.cloudflare.com/workers/platform/pricing/#durable-objects)
- `GET /v1/slack/devices`는 인증한 계정의 기기 목록을 반환한다. `DELETE /v1/slack/devices/{id}`는 같은 계정의 기기 하나를 취소한다. 마지막 기기 해제는 즉시 서버 접근을 차단하고 대기 메시지를 정리한다. Slack `auth.revoke`는 알람으로 처리한다. 일시 실패하면 토큰을 암호화한 상태로 보관 기간 안에서 재시도한다.
- 토큰과 메시지는 키 버전이 포함된 AES-GCM 암호문으로 저장한다. 기기 자격 증명은 해시를 저장한다. 승인 결과를 재조회할 때는 PC만 보유한 증명으로 동일한 기기 자격 증명을 다시 만든다. Slack 계정 및 워크스페이스 식별자, 기기 해시, 승인 요청 상태, 미전달 메시지와 전달 상태를 보관한다. PC의 화면과 작업 맥락은 Windows 앱 안에서 처리한다.
- `GET /health`는 Worker 버전과 바인딩 및 Secret 존재 여부를 반환한다. `receiverConnected`는 Worker의 설정 준비 상태를 나타낸다. Slack 대시보드의 `Verified`, 사용자의 OAuth 성공 및 DM 도착은 각각 확인한다.

### 배포 및 운영 확인

저장소 루트에서 Worker 폴더로 이동한다. `wrangler.jsonc`의 Worker 이름, Slack 공개 앱 식별자와 `PUBLIC_URL`을 확인하고 dry run에서 SQLite 바인딩 및 마이그레이션을 검토한다.

```powershell
Set-Location .\src\Altong.Slack.Cloudflare
npm ci
npx wrangler whoami
npx wrangler deployments list --name altong-slack
npx wrangler deploy --dry-run
npx wrangler deploy
```

최초 배포 시 Worker가 만들어진 후 운영자가 필수 Secret을 등록한다. 그 뒤 다음 명령으로 활성 배포와 설정 상태를 확인하고, 위 Slack 앱 설정의 OAuth 및 Events URL을 등록한다. Windows 앱 배포에는 [Windows 배포 설정](#windows-배포-설정)을 사용한다.

```powershell
npx wrangler secret list --name altong-slack
npx wrangler deployments list --name altong-slack
Invoke-RestMethod 'https://altong-slack.commonworks.workers.dev/health'
```

### 무료 플랜 기준

- Workers Free는 계정 전체에 하루 10만 Worker 요청을 제공한다. SQLite Durable Objects의 별도 무료 한도는 하루 10만 요청, 13,000 GB-s 실행 시간, SQLite 읽기 500만 행·쓰기 10만 행, 저장 5 GB다. 무료 한도를 넘긴 항목의 요청은 제한된다. [Workers 한도](https://developers.cloudflare.com/workers/platform/limits/#daily-requests) · [Durable Objects 요금](https://developers.cloudflare.com/durable-objects/platform/pricing/)
- 예를 들어 DM 10건을 두 PC에서 한 건씩 처리하면 Slack 이벤트 10건과 PC의 메시지 조회·수신 확인 40건으로 공개 Worker 요청은 약 50건이다. 초기 연결·재시도·내부 Durable Object 요청과 저장 사용량은 별도로 발생한다.
- 사용량이 무료 한도에 지속적으로 닿으면 계정의 Worker·Durable Object 지표를 보고 유료 전환을 검토한다. Workers Paid는 월 5달러로 시작하며, 포함량을 넘는 요청·실행 시간·저장 비용이 추가된다. [Workers 요금](https://developers.cloudflare.com/workers/platform/pricing/)

### Cloudflare Secret

| 이름 | 용도와 값의 출처 |
| --- | --- |
| `SLACK_CLIENT_SECRET` | OAuth 코드 교환과 토큰 갱신용. Slack 앱의 Basic Information → App Credentials → Client Secret |
| `SLACK_SIGNING_SECRET` | Events API의 서명 검증용. Basic Information → App Credentials → Signing Secret |
| `SLACK_APP_TOKEN` | 승인 목록 조회용. Basic Information → App-Level Tokens에서 `authorizations:read`로 생성 |
| `TOKEN_ENCRYPTION_KEY` | AES-256-GCM 저장 키. 암호학적 난수 생성기로 32바이트를 생성하고 Base64로 변환해 보안 저장소에서 관리 |
| `TOKEN_ENCRYPTION_KEY_PREVIOUS` | 키 교체 때 사용하는 이전 키. 현재 버전보다 한 단계 이전의 암호문을 읽는 데 사용 |

각 값은 담당자가 Wrangler의 `secret put` 프롬프트에 직접 입력한다. 키 교체 때는 이전 키로 저장된 장기 토큰을 재암호화하거나 재승인하고, 임시 데이터 보관 기간이 지난 뒤 이전 키를 제거한다. 일괄 재암호화는 후속 운영 작업이다.

Slack이 회전 토큰을 발급하면 서버는 만료 2분 전에 알람으로 갱신하고, 새 access token과 refresh token을 함께 암호화해 저장한다. 회전은 유출된 access token의 유효 기간을 줄이는 대신 갱신 호출과 장애 대응이 필요하다. Slack 앱에서 활성화하면 되돌릴 수 없으므로, 활성화 여부는 앱 소유자가 실환경 테스트 계획을 확인하고 결정한다. [Slack 토큰 회전](https://docs.slack.dev/authentication/using-token-rotation/)

### 로컬 검증

Cloudflare 프로젝트 폴더에서 실행한다. 로컬 설정과 모의 Slack은 가짜 값만 사용한다.

```powershell
npm test
npx wrangler dev -c test/wrangler.jsonc -c test/wrangler.mock.jsonc
```

두 번째 PowerShell에서 저장소 루트로 이동해 Windows 클라이언트 테스트를 실행한다. Debug 클라이언트 설정에서 로컬 주소를 읽는다.

```powershell
$env:ALTONG_RUN_WRANGLER_TEST = '1'
$env:ALTONG_SLACK_SERVER_URL = 'http://127.0.0.1:8793'
dotnet test .\tests\Altong.Client.Tests\Altong.Client.Tests.csproj --filter 'FullyQualifiedName~DesktopToCloudflare'
```
