# 메신저 동기화 (MessengerSync)

울산교육메신저 쪽지 데이터(`아이디.db`)를 여러 PC(사무실 2곳, 노트북)에서 같은 상태로 유지하는 윈도우 트레이 프로그램입니다.

## 하는 일
- **화면 잠금(Win+L)**: 메신저 종료 → 쪽지 파일 검사 → 압축 → 구글 드라이브에 업로드
- **잠금 해제**: 클라우드에 더 새 버전이 있으면 받아서 덮어쓴 뒤 메신저 다시 실행
- 파일 수정 시각이 아니라 **버전 번호 + 해시값**으로 판단하므로, 예전 파일이 최신 파일을 덮어쓰지 않습니다
- 두 PC 모두에서 바뀌면 **충돌**로 처리해 어느 쪽을 쓸지 묻고, 고르지 않은 쪽은 보관합니다
- 클라우드에 버전을 보관합니다 (최근 20개 + 지난 30일은 하루 1개)

## 받기
[Releases](https://github.com/xleesjx/messenger-sync/releases/latest)에서 `MessengerSync.exe`를 받아
`C:\MessengerSync\` 에 구글 클라우드에서 발급받은 `client_secret.json`과 함께 두고 실행합니다.
자세한 내용은 [사용설명서](docs/사용설명서.txt)를 보세요.

## 개인정보
- 쪽지 데이터는 **사용자 본인의 구글 드라이브**(`messenger-sync-data` 폴더)에만 저장됩니다.
- 구글 권한은 `drive.file`(이 앱이 만든 파일만 접근)입니다.
- `client_secret.json`, 로그인 정보, 쪽지 파일은 이 저장소에 포함되지 않습니다.

## 구조
```
src/MessengerSync/Core       동기화 판단, 파일 처리, 폴더 저장소 (윈도우 외 환경에서도 시험 가능)
src/MessengerSync/Platform   구글 드라이브 API, 메신저 종료, 업데이트
src/MessengerSync/UI         트레이, 설정·상태·진행 창
tests/CoreTests              PC 여러 대를 흉내 낸 동기화 시험
.github/workflows            버전 태그를 올리면 윈도우에서 빌드해 릴리스에 첨부
```
