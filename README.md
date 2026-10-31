# ImCompany

Windows PC에서 수주, 발주, 청구, 입금과 지급을 관리하는 프로그램입니다.

## 다운로드와 실행

1. [최신 Windows 배포본](https://github.com/ImaginetechKorea/ImCompany/releases/latest)에서 `ImCompany-Windows-x64.zip`을 내려받습니다.
2. 압축을 풀고 `ImCompany.exe`를 실행합니다. 압축을 푼 파일들은 함께 보관하세요.
3. 브라우저에 프로그램이 열리면 설정에서 회사 정보를 입력합니다. Windows 64비트가 필요하며, 별도 .NET 설치는 필요하지 않습니다.
4. 종료하려면 설정에서 **프로그램 종료**를 선택합니다.

## 주요 기능

- 거래처, 프로젝트별 수주·발주, 청구·입금·지급과 잔금 관리
- 발주서 인쇄 및 PDF 저장, 납품·입고·매입 기록
- 세금계산서와 문서 이미지·PDF를 이용한 AI 입력안 검토 및 저장
- 첨부 원본과 복원 정보를 포함한 엑셀 백업

## 자료 저장과 AI 연결

업무자료는 각 PC의 `%LOCALAPPDATA%\\ImCompany\\ImOrder` 폴더에 저장되며 처음 실행할 때는 비어 있습니다. 설정에서 엑셀 백업을 만들어 다른 저장장치에 보관하세요. 백업에는 업무자료와 첨부 원본이 포함됩니다.

AI는 ChatGPT 로그인(Codex) 또는 호환 API 연결을 설정한 경우 사용할 수 있습니다. AI에 보내기를 누르면 첨부 자료와 입력한 내용이 선택한 AI 서비스로 전송됩니다. AI가 제안한 내용은 확인 후 저장하세요. 프로그램은 은행 이체나 전자세금계산서 발급을 실행하지 않습니다.

## 소스에서 빌드

.NET SDK 10.0.401이 필요합니다. `dotnet restore` 후 `dotnet build --no-restore`로 빌드하거나 `Publish-ImCompany.ps1`로 Windows 배포본을 만들 수 있습니다.
