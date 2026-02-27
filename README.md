# 기출문제 연습프로그램

이 프로젝트는 PDF 기출문제와 정답 파일을 기반으로 문제를 분해해 연습할 수 있는 **오프라인 단일 사용자 데스크톱/모바일 학습 앱**을 만드는 것을 목표로 합니다.

## 목적
- C# MAUI 기반으로 Windows, Android(iOS 예정)에서 동작하는 기출문제 연습 앱을 구현
- PDF와 정답 텍스트를 업로드해 문제를 분해 저장
- 카테고리별로 관리하고 랜덤 출제로 반복 학습
- 오답 문제 중심 복습 흐름을 제공

## 주요 기능(현재 적용 범위)
- 카테고리 관리
  - 카테고리 생성/조회
  - 같은 파일명 업로드 시 기존 항목 수를 기준으로 덮어쓰기 확인
- 업로드/분석(진행중)
  - 정답맵 텍스트(`번호:정답`) 업로드/불러오기
  - 정답맵 파싱 규칙: `번호:정답1|정답2`(객관식 다정답)
- 연습 모드(예정)
  - 카테고리별 랜덤 출제
  - 사용자 입력 정답 즉시 채점
  - 객관식: 숫자 입력만 허용, 정답 중 하나와 일치하면 정답
  - 주관식: 공백 제거 후 대문자화한 값으로 비교
- 오답 노트(예정)
  - 오답 문제만 모아보기
  - 오답을 다시 맞추면 목록에서 제거
  - 사용자 수동 삭제 가능

## 사용 방법
1. 저장소 경로에서 솔루션 열기  
   - `PreviousPractice.sln`
2. MAUI 워크로드가 설치된 환경에서 프로젝트 복원  
   - `dotnet restore PreviousPractice/PreviousPractice.csproj`
3. 실행(예시)  
   - `dotnet build PreviousPractice/PreviousPractice.csproj -t:Run -f net8.0-windows10.0.19041.0`

## 현재 실행 제한
- 현재 작업 환경에는 MAUI 템플릿/워크로드가 없어 프로젝트 골격 생성 위주로 구성되었으며,
실제 앱 실행은 MAUI 워크로드가 구성된 환경에서 수행해야 합니다.

## 프로젝트 구조
- `PreviousPractice/App.xaml` : 앱 루트 설정
- `PreviousPractice/MainPage.xaml` : 시작 화면(XAML)
- `PreviousPractice/ViewModels` : MVVM ViewModel
- `PreviousPractice/Models` : 도메인 모델(Question, Category 등)
- `PreviousPractice/Services` : 비교/채점 로직
- `PreviousPractice/Infrastructure` : 파싱 유틸리티

## 다음 단계(우선순위)
1. 카테고리/문제 영속화(SQLite)
2. PDF OCR 파서 연결(이미지 기반)
3. 연습 세션(문항 랜덤 추출/채점/결과) 구현
4. 오답노트 화면 및 재채점 삭제 흐름 구현
5. 네트워크 없이 오프라인 동작 검증 및 예외 처리
