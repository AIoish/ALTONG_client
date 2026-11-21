# 요약 모델 앱 조건 테스트

요약 모델(GGUF)을 **앱과 같은 조건**으로 돌려 보는 도구입니다. Python 평가는 학습 중에 쓰고, 앱에 넣을지 판단하기 전에 이 테스트를 통과하는지 확인합니다.

앱과 같은 점:

- GGUF 파일을 앱과 같은 LLamaSharp 엔진으로 실행합니다.
- 앱의 시스템 프롬프트(`BriefingPromptBuilder`)와 입력 형식을 그대로 씁니다.
- 앱과 같은 규칙으로 알림을 묶고, 7개를 넘으면 나눕니다.
- 앱과 같은 응답 검사를 거칩니다. 형식이 틀리면 앱에서는 대체 요약이 나갑니다.

## 실행

모델 파일은 `models/briefing/`에 넣고 저장소 루트에서 실행합니다. 경로에 한글이 있으면 모델을 열지 못할 수 있으니 저장소 기준 상대 경로를 쓰세요.

```powershell
dotnet run --project tools/Altong.BriefingBench -- models/briefing/<새 모델>.gguf
# 여러 모델 비교
dotnet run --project tools/Altong.BriefingBench -- models/briefing/<새 모델>.gguf models/briefing/qwen_briefing_v8_q8_0.gguf
```

바탕 모델 후보를 비교할 때는 `--template`으로 대화 형식을 고릅니다. 지시문과 입력 내용은 앱과 같고, 대화 형식만 바꿉니다.

| 옵션 | 뜻 |
| --- | --- |
| `--template app` (기본) | 앱 엔진을 그대로 씁니다 (Qwen 형식). 앱에 넣을 모델은 이걸로 확인합니다. |
| `--template gemma4` | Gemma 4 대화 형식으로 넣습니다. 앱은 아직 Gemma 형식을 지원하지 않습니다. |
| `--gpu` | `gemma4`와 같이 씁니다. 내장·외장 그래픽(Vulkan)으로 실행합니다. 처음 실행할 때는 그래픽 드라이버 준비로 40초쯤 더 걸립니다. |

```powershell
dotnet run --project tools/Altong.BriefingBench -- F:/models/gemma-4-E2B_q4_0-it.gguf --template gemma4 --gpu
```

결과:

- 콘솔에 케이스별 PASS/FAIL, 틀린 이유, 걸린 시간이 나옵니다.
- `tools/Altong.BriefingBench/results/`에 아래 두 파일이 생깁니다. 이 폴더는 Git에서 제외됩니다.
  - `<시각>-report.md`: 기대 결과와 모델 출력을 나란히 놓은 표
  - 모델별 JSON

## 케이스 (`cases.json`)

케이스마다 알림 목록과 자동 판정 기준이 있습니다.

| 필드 | 뜻 |
| --- | --- |
| `expect` | 사람이 읽을 기대 요약 |
| `mustInclude` | 요약에 꼭 있어야 하는 말 (예: 바뀐 시각 "3시") |
| `mustNotInclude` | 있으면 안 되는 말 (예: 취소된 "2시", 요청을 완료로 바꾼 "삭제했") |
| `categories` | 허용하는 분류 |

자동 판정은 최소한의 기준입니다. PASS가 나와도 `report.md`에서 문장을 직접 읽고 확인합니다. 새로 발견한 오류 유형은 케이스로 추가해서 다음 모델에서도 같은 문제를 확인합니다.

## 주의

- 시스템 프롬프트는 이 저장소 코드에 있는 것을 씁니다. 모델마다 학습에 쓴 프롬프트가 다르면, 비교하기 전에 앱 프롬프트를 그 모델에 맞춰야 합니다.
- 기준 시각은 2026-11-17(화) 18:00입니다. "내일", "이번 주 토요일" 같은 말은 이 시각을 기준으로 판정합니다.
- 시간은 PC 부하와 전원 상태(배터리·충전)에 따라 크게 달라집니다. 모델끼리 비교할 때는 같은 PC에서 연달아 실행하세요.
- 최대 메모리는 실행한 프로세스 전체의 최대값입니다. 모델별로 재려면 모델 하나씩 따로 실행하세요. 내장 그래픽은 일반 메모리를 같이 쓰므로 `--gpu`에서 더 크게 나옵니다.
