<!-- 용도: 검증된 KLUE 필터를 ALTONG_client에 연결하고 시험하는 전달 안내.
생성일: 2026-10-10 -->
# KLUE base C# 전달 모듈

Python 서버 없이 C# 앱 프로세스 안에서 ONNX Runtime CPU로 실행한다. 학습 코드는 Python에 유지한다.
앱 저장소는 수정하지 않았다. 연결 대상은 `ALTONG_client` develop의
`4fb6b75022309e3019b31c0b5d339142c635173b` 커밋이다.

## 전달 모델과 이번 추가 학습

사용 모델은 `2026-10-10-base-safety-continue-01`의 FP32 ONNX다. 약 440MB이며 토크나이저가 별도로 필요하다.
경계·관련도 새 자료 84행과 안전 복습 375행으로 1에폭 추가 학습했지만 기존 정책 정확도가
68/74 → 67/74로 내려 새 가중치를 채택하지 않았다. 개발 평가 54/56 → 55/56의 개선만으로 교체하지 않는다.
이전 모델의 기존 긴급 누락 2/17도 남아 있다. 전달 패키지에는 이전 안전 보강 모델을 넣는다.
새 합성 진단 14행은 선택에 쓰지 않았고, 이전 모델은 13/14를 맞혔다. 긴급 4건은 모두 통과했고
일반 책 배송 1건을 불필요하게 통과시켰다. 작은 합성 평가이며 실사용 무누락을 보장하지 않는다.

## 폴더 배치

전달 ZIP을 앱 저장소 루트에 풀면 아래 두 디렉터리가 생긴다. 기존 앱 파일은 포함하지 않는다.

```text
integration/csharp/
  Altong.Filtering/       # WPF에 의존하지 않는 net9.0 라이브러리
  ClientAdapter/         # 앱 IFilterEngine 구현
  Verification/          # Python/C# 비교 프로그램
  fixtures.json          # 합성 입력·토큰·기대 점수
  verification-result.json
models/filtering/klue-base-safety-v1/
  model-fp32.onnx
  tokenizer.json
  tokenizer_config.json
  manifest.json
reports/                 # 평가와 추가 학습 채택 여부
```

모델과 토크나이저는 같은 버전으로 배포한다. 모델 옆 manifest.json이 있으면 두 파일의 SHA256을 로딩 전에 확인한다.
소스 라이브러리는 향후 재내보내기를 허용하므로 manifest가 없는 개발 경로도 지원한다.
실제 앱 배포에는 패키지의 manifest를 반드시 포함한다. 모델 바이너리는 일반 소스 커밋에 넣기보다 별도 파일로 전달한다.

## 앱 팀원이 적용할 변경

`src/Altong.Client/Altong.Client.csproj`에 다음 항목을 추가한다. 아래 경로는 ZIP을 앱 저장소 루트에 푼 기준이다.

```xml
<ItemGroup>
  <ProjectReference Include="..\..\integration\csharp\Altong.Filtering\Altong.Filtering.csproj" />
  <Compile Include="..\..\integration\csharp\ClientAdapter\KlueFilterEngine.cs"
           Link="Services\AI\Filtering\KlueFilterEngine.cs" />
  <Content Include="..\..\models\filtering\klue-base-safety-v1\**\*">
    <Link>models\filtering\klue-base-safety-v1\%(RecursiveDir)%(Filename)%(Extension)</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </Content>
</ItemGroup>
```

ONNX Runtime 1.31.0은 라이브러리의 NuGet 참조로 전달된다. 이번 필터링 모듈에는 LLamaSharp가 필요 없다.
Windows x64 앱으로 빌드·배포하고, NuGet이 가져오는 네이티브 ONNX Runtime DLL도 함께 배포한다.
개발자는 .NET 9 SDK가 필요하다. 사용자의 Python·PyTorch·CUDA 설치는 필요하지 않다.
.NET 런타임 필요 여부는 앱의 framework-dependent/self-contained 배포 방식에 따른다.

`App.xaml.cs`에 엔진을 소유할 필드를 추가한다.

```csharp
private KlueFilterEngine? _klueFilterEngine;
```

현재 `var filterEngine = new RuleBasedFilterEngine();` 부분을 아래처럼 연결한다.
기존 `NotificationPipelineCoordinator`에 `filterEngine`을 전달하는 코드는 유지한다.

```csharp
var modelDir = Path.Combine(AppContext.BaseDirectory,
    "models", "filtering", "klue-base-safety-v1");
_klueFilterEngine = new KlueFilterEngine(
    Path.Combine(modelDir, "model-fp32.onnx"),
    Path.Combine(modelDir, "tokenizer.json"),
    shadow: false, // 모델 판단대로 실제 차단·통과
    observe: d => AppLogger.Info(
        $"[KLUE] {d.Status}: U={d.UrgencyScore}, R={d.RelevanceScore}, modelPass={d.ModelPassed}"));
IFilterEngine filterEngine = _klueFilterEngine;
```

생성자는 백그라운드에서 모델을 한 번 로딩하므로 WPF UI에서 모델 초기화를 기다리지 않는다.
초기화가 아직 끝나지 않은 첫 요청은 2초까지 기다린 뒤 미평가 상태로 통과한다.
종료 시에는 기존 파이프라인을 중지·해제한 다음 `_klueFilterEngine?.Dispose()`를 호출한다.
네이티브 추론 중인 작업은 해제 전에 끝날 때까지 기다린다. 브리핑 엔진은 변경하지 않는다.

## 처리 규칙

- 긴급도 입력은 알림만 포함한다. 관련도 입력은 알림·현재 창·실제 최근 창/프로세스 목록을 포함한다.
- 입력 JSON은 Python `encoder_score_experiment.input_text`와 같은 순서와 escaping을 사용한다.
- 이 KLUE export의 토크나이저는 대소문자를 유지하는 BertNormalizer, BertPreTokenizer, WordPiece,
  `[CLS]`·`[SEP]` 삽입이다. C# 구현은 이 구성만 지원하고 다른 주요 설정은 거절한다.
- `input_ids`, `attention_mask`는 Int64 `[1, sequence]`다. `token_type_ids`는 전달하지 않는다.
- 같은 ONNX 세션으로 긴급도/관련도 입력을 각각 실행한다. `logits[0,0,:]`, `logits[0,1,:]`에서
  `argmax + 1`을 얻는다. 두 점수는 각각 1~5이며 `U >= 4 || R >= 4`가 모델 통과 조건이다.
- 기본 `shadow: false`는 모델 판단대로 실제 차단·통과한다. `FilterResult.IsPassed`와 DB 통과 플래그에
  이 판단이 전달된다. 별도 진단이 필요할 때만 명시적으로 `shadow: true`를 사용하면 모든 알림이 통과한다.
- 오류·취소·시간 초과·대기열 초과·512토큰 초과는 통과한다. 내용을 조용히 잘라 차단 판단하지 않는다.
  라이브러리는 실패 점수를 null로 반환한다. 앱의 FilterResult는 정수 필수이므로 어댑터는 **0=미평가**로 변환한다.
  0은 모델의 1~5점 판단이 아니다. UI에서는 0을 `미평가`로 표시하는 것을 권한다.
- 네이티브 추론은 한 번에 하나, 대기 요청을 포함해 최대 8개다. 정상 평가 제한은 대기·토큰화·추론을 합쳐
  기본 2초다. 로딩 대기 최대 2초는 별도다. ONNX 취소 요청도 전달하지만 커널 종료까지의 지연을 완전히 없애지는 않는다.
- Category는 학습 출력이 아니므로 `미분류`다. 이유 문자열은 점수·정책 설명 템플릿이며 생성형 설명이 아니다.

현재 앱의 CurrentContext에는 최근 창 제목 목록이 없다. 어댑터는 최근 프로세스는 전달하고 recent_windows는
빈 배열로 둔다. 라이브러리는 실제 recent_windows를 받을 수 있으므로 추후 관측 이력을 수집하면 확장할 수 있다.
프로세스 이름에서 창 제목을 추정해 만들지 않는다. 현재 창 제목이 없는 경우도 그대로 빈 문자열을 사용한다.
Timestamp는 Kind가 지정된 UTC/local DateTime을 받아 UTC 초 단위 `...Z`로 맞춘다.

## 팀원 검증 명령

앱 저장소 루트에서 실행한다. 기존 앱의 네 계약 파일을 읽어 어댑터도 함께 빌드한다.
빌드에는 NuGet 복원이 필요하고, 추론 실행은 네트워크를 사용하지 않는다.

```powershell
$clientRoot = (Get-Location).Path
dotnet build integration/csharp/Verification/Verification.csproj -c Release -p:ClientRoot=$clientRoot
dotnet integration/csharp/Verification/bin/Release/net9.0/Verification.dll `
  models/filtering/klue-base-safety-v1/model-fp32.onnx `
  models/filtering/klue-base-safety-v1/tokenizer.json `
  integration/csharp/fixtures.json
```

이 저장소에서는 실제 develop 계약에 대한 빌드, 토크나이저 260건, 모델 입력·토큰·점수 188건 일치,
시험 모드·긴 입력·취소·대기열 초과·모델 파일 누락 시 통과를 확인했다. 성능 수치는 함께 전달한
verification-result.json의 timing_scope 기준이며 알림 캡처부터 UI 표시까지의 전체 시간이 아니다.
토큰 비교 사례는 한글·영문 대소문자·중국어·이모지·유니코드 공백·제어 문자·특수 토큰을 포함한다.
모든 가능한 문자를 검증했다는 의미는 아니다.

## 실제 알림 시험

1. 위 비교 프로그램이 통과하는지 먼저 확인한다.
2. 앱에서 실제 알림이 RawNotification으로 들어오는지 확인한다. unpackaged 환경에서는
   WinRT 리스너가 MockGenerator 파이프로 전환될 수 있다. 실제 Slack/Chrome 알림 시험은 앱의
   sparse package 등록 및 알림 접근 권한을 확인한다. 카카오톡은 별도 인터셉터 경로다.
3. 집중 모드를 끈 상태에서 모델 판단을 관찰한다. 현재 파이프라인은 이 상태에서도 평가하지만 DB에는 저장하지 않는다.
   callback은 점수·상태만 제공하고 기본 원문 로그는 추가하지 않았다.
4. 집중 모드에서는 기본 shadow=false로 모델 판단을 적용한다. 기존 앱은 원래 카카오톡 팝업을 미리 숨긴 뒤 닫고,
   통과한 알림을 독에 표시한다. 소리·독 표시·세션 종료 직전 알림·대기열 초과·초기 로딩도 실제 앱에서 확인한다.

전체 WPF 앱의 실행·배포·실제 OS 알림 수신은 여기서 수행하지 않았다. 독 표시 및 DB 실패는 모델의
오류 시 통과 처리만으로 해결되지 않으므로 앱의 전체 흐름 검증이 필요하다.
