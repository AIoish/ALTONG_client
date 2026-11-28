# Windows 알림 제어 호환성 — 빌드·DLL·해시·호출 순서

기준일: 2026-11-14. 작업 브랜치: `feature/windows11-notification-compatibility`.

## 목차

1. [실기기 검사 순서](#1-실기기-검사-순서)
2. [검증 상태와 지원 범위](#2-검증-상태와-지원-범위)
3. [클래스·인터페이스 ID와 호출 순서](#3-클래스인터페이스-id와-호출-순서)
4. [빌드별 DLL 버전·SHA256 전체 목록](#4-빌드별-dll-버전sha256-전체-목록)
5. [새 DLL 추가 순서](#5-새-dll-추가-순서)
6. [근거](#6-근거)

## 1. 실기기 검사 순서

실기기에서 작업 브랜치로 전환한 뒤 평소처럼 빌드·실행한다. 기존 알통은 먼저 집중 모드를 끄고 트레이에서 종료한다. 아래는 .NET 9 SDK가 설치된 개발 환경에서 실행하는 예시다.

```powershell
git fetch origin
git switch feature/windows11-notification-compatibility
git pull --ff-only
dotnet run --project .\src\Altong.Client\Altong.Client.csproj --configuration Release
```

Visual Studio에서 기존 방식으로 실행해도 된다. 검증 스크립트나 별도 패키지는 사용하지 않는다.

1. 설정의 알통 자체 제어 카드에서 앱 조회·선택이 되는지 확인한다.
2. 테스트할 앱만 선택하고 집중을 켠다. 배너·알림음이 억제되는지 실제로 관찰한다.
3. 알림센터 보관과 알림 수신 허용은 유지되는지 확인한다.
4. 집중을 끄고 원래 배너·알림음·레지스트리 상태가 복원되는지 확인한다.
5. 테스트 대상만 선택한 별도 집중에서 알통 강제 종료 후 복구 감시가 원상복구하는지 확인한다.
6. 실패했다면 실제 Windows 세부 빌드와 증상을 기록한다. 원래 복구 파일은 삭제하지 않는다.

배너·소리는 실제로 관찰하고, 자체 팝업을 쓰는 앱의 연동은 별도 확인한다. WinRT 수집 권한·AI 품질은 이 DLL 호환성 분석과 구분한다.

## 2. 검증 상태와 지원 범위

변경하는 곳은 알통의 호환성 자료·COM 어댑터다. `System32`의 Windows DLL 파일을 교체하지 않는다.

- 판단 기준은 OS 빌드 범위가 아니라 실제 `NotificationController.dll` SHA256이다. 같은 DLL을 배포하는 여러 세부 빌드는 같은 구현을 공유한다.
- Windows 11 DLL 33개(x64 16개·ARM64 17개)의 정확한 파일 해시, 일치하는 PDB GUID/age, GUID 3개와 실제 호출 9개의 슬롯·인자 형식을 대조했다. 이 파일들은 알통이 쓰는 호출 구조가 같아 기존 Windows 11 구현을 공유한다.
- 최신 정적 분석 대상: 26H1 `28000.3501`, 26H2 `26300.9974`. OS 빌드와 개별 DLL 버전은 다를 수 있다.
- Windows 10의 다른 Sender IID·후반 슬롯은 기존 구현을 유지한다.
- 알 수 없는 DLL과 x86·ARM32 프로세스는 자체 제어를 허용하지 않고 기존 Windows 방해 금지 방식으로 전환한다. 사용자 앱 선택은 유지한다.

| 환경·검사 | 현재 결과 |
|---|---|
| Windows 11 DLL 33개 정적 ABI 대조 | 통과 |
| 전체 솔루션 테스트 | 566개 통과·12개 건너뜀(네이티브 opt-in 4개, Paddle/OCR 8개) |
| 25H2 `26200.9711` x64 실기기 | 읽기·모의 앱 차단·복구 2개 통과 |
| 26H2 `26300.9457` x64 VM | 읽기·모의 앱 차단·복구 2개 통과, 강제 종료 복구 확인 1개 통과 |
| 26H2 실기기·최신 누적 업데이트 실행 | **검증 대기** |
| 26H1·ARM64 실행 | **검증 대기** |

모든 세부 빌드를 각각 실행한 상태는 아니다. VM 작업은 중단했고 현재 후속 단계는 실기기 검사다.

**제외한 파일:** 26H1 x64 `28000.1896`·`28000.2113`에서 쓰는 DLL `10.0.28000.1896`, SHA256 `E5B62EDDD4CAB734280F5738B5AE1BDC34E39C1C9394813E3AF3D997AFEF2286`은 일치하는 공개 PDB를 확보하지 못해 허용 목록에 추가하지 않았다. 해당 ARM64 파일은 별도 대조 후 수록했다. 표에 없는 최초 출하 이미지·미래 DLL까지 지원한다고 간주하지 않는다.

## 3. 클래스·인터페이스 ID와 호출 순서

슬롯 번호는 0부터 세는 COM vtable 인덱스다. 사용하지 않는 슬롯은 호출하지 않는다.

| 역할 | GUID |
|---|---|
| NotificationSettings 클래스 CLSID | `D18705BE-FC2F-44C8-AEFF-1CD49AEA8FC1` |
| Factory IID | `443B1739-3779-4ABA-953B-9E4FF84DD4B6` |
| Windows 11 Sender IID | `9DCA8F00-CB98-434B-9473-5A0691200DC4` |
| Windows 10 Sender IID | `266DE187-1DE7-4021-A421-806ABA49A99E` |

| 호출 | Windows 11 슬롯 | Windows 10 슬롯 | 인자·반환 |
|---|---:|---:|---|
| Factory `GetSettingsForAumid` | 3 | 3 | `LPCWSTR`, `IAumidNotificationSettings**` → HRESULT |
| `get_Enabled` | 5 | 5 | `int*` → HRESULT |
| `get_ShowBanner` | 7 | 7 | `int*` → HRESULT |
| `put_ShowBanner` | 8 | 8 | `int` → HRESULT |
| `get_ShowInActionCenter` | 9 | 9 | `int*` → HRESULT |
| `get_SoundFile` | 33 | 21 | `LPWSTR*` → HRESULT |
| `put_SoundFile` | 34 | 22 | `LPCWSTR` → HRESULT |
| `get_Title` | 37 | 25 | `LPWSTR*` → HRESULT |
| `get_HasValidAppInfo` | 43 | 30 | `int*` → HRESULT |

## 4. 빌드별 DLL 버전·SHA256 전체 목록

아래는 [제품에 포함한 호환성 자료](../src/Altong.Client/Services/NotificationControl/NotificationControllerModules.json)의 33개 Windows 11 모듈이다. 자료에는 KB, Microsoft DLL/PDB URL, PDB GUID/age도 포함한다. 같은 행의 세부 빌드들은 같은 파일 해시를 공유한다. **표 수록은 정적 분석 결과이며 각 빌드의 실기기 통과 표시가 아니다.**


### 26H1 x64

| DLL 버전 | 해당 OS 세부 빌드 | SHA256 |
|---|---|---|
| `10.0.28000.1516` | 28000.1575 | `3B3F227FDF3A9214F06AA5A959FA82F1A966A9A2CDC89398F63CD22C8273AB36` |
| `10.0.28000.1643` | 28000.1643, 28000.1719 | `DA15D7C2D4399E9577336C17A9AB42429F170DA3E8A39FB2AAD111A5D9A4FB7E` |
| `10.0.28000.1761` | 28000.1764 | `F6E1DCA17C18036AB00C93279C2F6F0BE0B6F1038E97FD48FC79A6816D36EF2E` |
| `10.0.28000.1830` | 28000.1836 | `6AD4A955AF8D0EA5CD0096AA5C544FFF4123E82F23B226FECA3EA555B3E8E594` |
| `10.0.28000.2179` | 28000.2179, 28000.2269 | `6373AFE5ECF305028BBFE49D127ACC03B203FEFEFFA3B8AC16F8B21607A68DA3` |
| `10.0.28000.2336` | 28000.2340, 28000.2525 | `2BBFC0CF1E8316BAF97AC2592BA1C79A630A7CF11C014DE789D363F935C9981B` |
| `10.0.28000.2605` | 28000.2608, 28000.2704 | `E9C1AB859B132DB48DC2CDDE5CBA7FDBC3075E0F315896B82257433AE30F98AE` |
| `10.0.28000.2804` | 28000.2804 | `78B2DFF58FFE8084AFA9B44339E5FECADA7E356C91CC0495CB951B6B878049C0` |
| `10.0.28000.2952` | 28000.2954, 28000.2956 | `812956BFB0E512404B8E794D9EC0D309907844E580359E031FAB58E2DF6B2EC9` |
| `10.0.28000.3086` | 28000.3086 | `0C42625F551398671E600CA6F55132DF63D8F8DB772B95E8BCA595B1BAC32213` |
| `10.0.28000.3231` | 28000.3231 | `DAD130AFC0F53CFF24FB26C689E67BC0701FF14D42312DC3B968256C775461CC` |
| `10.0.28000.3365` | 28000.3365, 28000.3501 | `800DA001B2D438DD692A5AF825E3212EC9A47B0C3C2BAD746061B04E338F653C` |

### 26H1 arm64

| DLL 버전 | 해당 OS 세부 빌드 | SHA256 |
|---|---|---|
| `10.0.28000.1516` | 28000.1575 | `21B2AFF5C65DAE4C24A5E551CBD09489694A0CCD47085A67CCCD1C9757499E20` |
| `10.0.28000.1643` | 28000.1643, 28000.1719 | `1B0DB8C3F3BC60579314EB4E2DD4AA7653D80AE690F5BDBBA6051BE4A599704E` |
| `10.0.28000.1761` | 28000.1764 | `B731A02E3EF1CD3D44FD466A810CB7086ACB542099D4530E77207F933C6B7D4D` |
| `10.0.28000.1830` | 28000.1836 | `E79D60A50F0BBD67E7D446288A0ACCD67E7D1C004174368DA9F3FAD01C8B2639` |
| `10.0.28000.1896` | 28000.1896, 28000.2113 | `F0E20FAAEF674132D8906277B66773F3106FFEC52FD71D9E6C1F21CC14A29B54` |
| `10.0.28000.2179` | 28000.2179, 28000.2269 | `D386A84071C5860D451B01B4EA9A23197D9526679CD7B5A982A5AFBCBDA5069B` |
| `10.0.28000.2336` | 28000.2340, 28000.2525 | `AE4B7201D219C4EBF378DAFEF2E45DB0279E3A5857518D665CB03F9F95391BDF` |
| `10.0.28000.2605` | 28000.2608, 28000.2704 | `3A9FF08FBE7FF801F90C29BB609E1DEF1CDB0565B42F3581648BA587344AD6C0` |
| `10.0.28000.2804` | 28000.2804 | `2DC07BFCC21E7EB5A1D396340D5F9E848323EFB0D0FB59F28DC2379A89C5282D` |
| `10.0.28000.2952` | 28000.2954, 28000.2956 | `A9F11F7016B3B3A0BC81AC04CA245506AAD3A15019A33F2915CBED341A6CEBA7` |
| `10.0.28000.3086` | 28000.3086 | `527DE2BB74E85F336588A48207749E3FE8D58E347C43A93687BA3A77EF6B8664` |
| `10.0.28000.3231` | 28000.3231, 28000.3237 | `9AA51FCD949F8E25D4E950B992B05245B3B3C9163C4F141AE92B1B744BE99AEB` |
| `10.0.28000.3365` | 28000.3365, 28000.3501 | `7231FAE99137A870FAB9DC9D8EE5866D5ED5BFBA76D87986D524BFCF9169508D` |

### 26H2 x64

| DLL 버전 | 해당 OS 세부 빌드 | SHA256 |
|---|---|---|
| `10.0.26100.9444` | 26300.9457 | `6E4670B545B39398E5A806415883B0912EC04F435BB03D1560ED28D1C9009101` |
| `10.0.26100.9549` | 26300.9550 | `21DFC6F1E63E3615ACA0486B90C02D759A535E0ED3795E2751551B10247057D2` |
| `10.0.26100.9711` | 26200.9711, 26300.9711, 26300.9725 | `3242604B327A90314F88F044FFA4E3B8B2DD28F7D9E7009AB38793ABFE6FFC2D` |
| `10.0.26100.9818` | 26200.9818, 26200.9974, 26300.9818, 26300.9974 | `4BC537B3C70F87C1BDD1EC8919F38FBBE2077EB283A8CCE7C98280867CE345C6` |

### 26H2 arm64

| DLL 버전 | 해당 OS 세부 빌드 | SHA256 |
|---|---|---|
| `10.0.26100.9444` | 26300.9457 | `4F0EC58023C469A9BA199FC371C70F811EFE363E6D80F8E50EB001FF7D5561B0` |
| `10.0.26100.9549` | 26300.9550 | `2E426E31806F622DF3BEE59E5034110672C95A2EF9AD6DEA6B0456DE681F9E6A` |
| `10.0.26100.9711` | 26300.9711, 26300.9725 | `5E25D962F68CC1E229E77FC5C79D1713D714D5B9CB0CFDBC045AD780B0864CAB` |
| `10.0.26100.9818` | 26200.9818, 26200.9974, 26300.9818, 26300.9974 | `07B5BBBCC1A0399A4CFC488D17D5C7D3A7782D4EAFDE853A383FAE2CA43A2E27` |

### Windows 10 기존 모듈

| DLL 대응 OS | SHA256 |
|---|---|
| Windows 10 22H2 `19045.6466` | `C2C4693C3204BA7FB1D2BA1D78D8E4705CCA76BBA5C7D368D43C7A05CB599F97` |

## 5. 새 DLL 추가 순서

1. 실기기의 실제 Windows 세부 빌드와 `NotificationController.dll` SHA256을 확인한다.
2. 새 해시면 Microsoft DLL과 정확히 대응하는 PDB를 확보한다. 빌드 번호만 보고 기존 프로필을 허용하지 않는다.
3. PDB GUID/age, CLSID·IID와 위 9개 함수의 슬롯·인자 형식을 확인한다.
4. 구조가 같으면 호환성 JSON에 해시·DLL 버전·해당 빌드·출처를 추가해 기존 어댑터를 공유한다.
5. IID·슬롯·시그니처가 다르면 어댑터를 수정하고 별도 레이아웃으로 분리한다. 해시만 바꾸어 허용하지 않는다.
6. 관련 테스트와 대상 실기기의 위 검사를 통과한 뒤 실행 검증 상태를 갱신한다. 대응 자료가 부족하면 허용 목록 밖에 둔다.

## 6. 근거

- [Microsoft 26H2 KB5132061](https://support.microsoft.com/en-us/servicing/os/windows-11/2026/11/kb5132061-windows-11-24h2-25h2-26h2-security-update), [공식 파일 목록](https://download.microsoft.com/download/d2b8ce92-7b1b-413d-83e3-f6c5cef48d05/5132061.csv)
- [Microsoft 26H1 KB5132059](https://support.microsoft.com/en-us/servicing/os/windows-11/2026/11/kb5132059-windows-11-26h1-security-update), [공식 파일 목록](https://download.microsoft.com/download/d2b8ce92-7b1b-413d-83e3-f6c5cef48d05/5132059.csv)
- [Microsoft 공개 심볼 서버](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/microsoft-public-symbols). Winbindex는 Microsoft 다운로드 주소 구성용 보조 인덱스로 사용했다.

원시 DLL·PDB·검사 결과·이전 VM 작업 기록은 Git 제외 경로 `verify-build`에 보존한다. 제품과 Git에는 Windows DLL·PDB를 포함하지 않는다. 패키지 생성·VM 준비는 현재 실기기 검사 절차에 포함하지 않는다.
