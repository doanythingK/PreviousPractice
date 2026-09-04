# PreviousPractice 아키텍처 수렴 기준

이 문서는 `refactor/architecture-convergence` 브랜치에서 구조를 정리할 때 지켜야 할 책임 경계와 검증 순서를 기록합니다.

## 목표

기능 동작과 OCR 판정 규칙을 바꾸지 않은 채, 거대한 UI/ViewModel 파일에 집중된 책임을 단계적으로 분리합니다.

현재 우선순위는 다음과 같습니다.

1. 기존 회귀 동작을 자동 검증으로 고정
2. `MainViewModel` 책임을 기능 단위 파일로 분리
3. OCR 구조 판정 로직을 Presentation 계층 밖으로 이동
4. PDF 분석 플랫폼 구현을 어댑터 단위로 분리
5. 저장소 구현을 유지한 채 경계를 고정한 후 필요 시 SQLite 전환 검토

## 현재 계층

### Presentation

- `MainPage.xaml` / 페이지 코드비하인드
- `ViewModels/MainViewModel*.cs`
- 화면 상태, command, 사용자 피드백, 화면 전환을 소유합니다.

`MainViewModel`은 현재 partial class로 나누어 기능별 책임을 물리적으로 분리했습니다.

- `MainViewModel.cs`: 공통 상태, 속성, command 구성, 공통 orchestration
- `MainViewModel.Import.cs`: 카테고리/소스 파일/정답맵 가져오기 흐름
- `MainViewModel.Practice.cs`: 연습 세션, 채점 진행, 오답 흐름
- `MainViewModel.Diagnostics.cs`: OCR 분석 진단과 구조 오류 요약
- `MainViewModel.Images.cs`: 문항 이미지 조각과 공통 지문 이미지 처리

이 partial 분리는 최종 구조가 아니라 **행동 보존형 중간 단계**입니다. 이후 순수 계산과 구조 판정은 별도 서비스로 옮깁니다.

### Domain / Models

- `Models/*`
- 문항, 카테고리, OCR 결과, 문항 범위 등 데이터 계약을 소유합니다.
- UI 프레임워크 의존성을 추가하지 않습니다.

### Application / Services

- `Services/AnswerComparer.cs`
- `Services/PdfAnalysisService.cs`
- `Services/PdfStructuralValidator.cs`
- 분석/채점/구조 검증 등의 유스케이스와 플랫폼 OCR 진입점을 소유합니다.

향후 `PdfAnalysisService` 내부의 Windows/Android/Mac 구현은 플랫폼 어댑터로 분리하되, 외부 `IPdfAnalysisService` 계약은 가능한 한 유지합니다.

### Infrastructure

- `Infrastructure/OcrQuestionSegmenter.cs`
- `Infrastructure/QuestionSetParser.cs`
- `Data/PracticeRepository.cs`
- 파일/JSON/OCR 분할처럼 외부 표현이나 영속화에 가까운 구현을 소유합니다.

## 다음 분리 대상

### 1. OCR 구조 검증 — 1차 완료

순수 구조 검증 규칙은 `Services/PdfStructuralValidator.cs`로 이동했습니다. `MainViewModel`은 검증을 호출하고 결과를 진단 DTO/화면 메시지로 변환하는 역할만 유지합니다.

현재 validator가 소유하는 책임은 다음과 같습니다.

- 중복 문항 번호
- 비연속 문항 번호
- 잘못된 문항 span
- 겹치는 문항 이미지 영역
- 공통 지문과 문항 이미지 겹침
- 신뢰할 수 없는 geometry 차단

기존 구조 검증 회귀 테스트도 `MainViewModel` 대신 `PdfStructuralValidator` 경계를 직접 호출하도록 전환했습니다.

### 2. OCR 문항 분할

`OcrQuestionSegmenter`는 외부 facade를 유지하면서 내부를 다음 책임으로 나눕니다.

- document normalization
- header hypothesis 수집
- header path/sequence 선택
- page/column layout 판정
- shared context 해석
- image region 계산

규칙 분리 중에는 기존 `OcrQuestionSegmenterTests`와 `OcrSegmentationBoundaryTests` 결과를 변경하지 않습니다.

### 3. PDF 플랫폼 분석

`IPdfAnalysisService`를 진입점으로 유지하고 구현을 다음처럼 분리할 예정입니다.

- Windows PDF renderer + Windows OCR
- Android PdfRenderer + ML Kit
- Mac command-line renderer/OCR
- 공통 cache/PNG validation

현재 iOS OCR 미지원 정책은 별도 구현이 생기기 전까지 유지합니다.

## 데이터 안정성 계약

아래 동작은 구조 정리 중 변경하지 않습니다.

- 기존 문항 ID 보존
- 오답 이력 보존
- 부분 정답 갱신 시 지정하지 않은 정답/유형 보존
- 손상 JSON 백업 실패 시 원본 덮어쓰기 금지
- 임시 파일 작성 후 최종 파일 교체
- PDF/PNG 캐시의 부분 파일 노출 방지
- 애매한 OCR 구조를 임의 추정해 저장하지 않는 fail-closed 정책

## 검증 계약

`refactor/architecture-convergence` 브랜치는 `.github/workflows/validate.yml`에서 다음을 검증합니다.

1. Windows MAUI workload 설치
2. Windows 앱 target restore
3. Windows Release build
4. 테스트 프로젝트 restore
5. `PreviousPractice.Tests` 전체 실행

구조 변경은 이 검증이 성공한 상태에서만 다음 절편으로 진행합니다.

## 리팩터링 원칙

- 기능 추가와 구조 변경을 같은 절편에서 섞지 않습니다.
- OCR threshold/regex/점수 조정은 구조 이동과 별도 커밋으로 분리합니다.
- 대규모 재작성보다 기존 메서드를 이동하고 테스트가 통과한 뒤 책임을 축소합니다.
- `main` 또는 `fix/ocr-structure-validation`에는 검증 전 변경을 직접 반영하지 않습니다.
