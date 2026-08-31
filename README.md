# family-gallery-api

가정용 Synology NAS의 이미지·영상을 가족 구성원에게 제공하기 위한 전용 API.

- 로그인 사용자 전체 조회 허용
- `Editor` 권한 사용자만 업로드·삭제 허용
- 원본 수정 미지원, 신규 추가 및 휴지통 기반 삭제만 허용
- Cloudflare Tunnel 단일 경로를 통한 외부 노출
- Flutter 클라이언트 `family-gallery-app` 전용

## 요구 사항

- .NET SDK 10.0
- ffmpeg
  - 썸네일 생성 용도
  - 로컬 실행 시 `PATH` 등록 필요
  - 배포 이미지 기본 포함
- Docker / Docker Compose
  - NAS 배포 시 필요

## 프로젝트 구조

```text
.config/
  dotnet-tools.json   로컬 도구 매니페스트 (dotnet-ef)
global.json           dotnet test 러너 지정
FamilyGallery.slnx

FamilyGallery.Api/
  Program.cs          서비스 등록, HTTP 파이프라인, DB 초기화
  Options/            Jwt, Gallery, Indexing, Thumbnail, Upload 설정
  Data/               AppDbContext, 엔티티, 값 변환기
  Migrations/         EF Core 마이그레이션
  Endpoints/          엔드포인트 매핑
  Services/           인증, 인덱싱, 타입 판별, 업로드 편입, 휴지통, 썸네일
  Cli/                계정 관리 명령

FamilyGallery.Api.Tests/
  ApiFactory.cs       테스트 호스트 및 임시 DB
  MediaFixtures.cs    JPEG·MP4 테스트 데이터
  FfmpegFixtures.cs   디코딩 가능한 테스트 미디어
  TestUploads.cs      tus 업로드 테스트 헬퍼
  *Tests.cs           통합 테스트

Dockerfile
docker-compose.yml    Synology NAS 배포 구성
```

## 인증 / 인가

- JWT Bearer 인증
- issuer, audience, 만료, 서명 키 검증
- fallback policy 기반 전 엔드포인트 인증 적용
- 익명 허용 대상
  - `GET /health`
  - Development 환경 OpenAPI 문서
- 사용자 권한
  - `Viewer`: 조회
  - `Editor`: 조회, 업로드, 삭제
- JWT 클레임
  - `sub`
  - `name`
  - `role`
  - `jti`
- 인바운드 클레임 매핑 비활성화

### 인증 API

| 엔드포인트 | 인증 | 설명 |
| --- | --- | --- |
| `POST /auth/login` | 익명 | 자격 증명 검증 및 토큰 쌍 발급 |
| `POST /auth/refresh` | 익명 | refresh token 회전 발급 |
| `POST /auth/logout` | 필요 | refresh token 폐기 |
| `GET /auth/me` | 필요 | 현재 사용자 정보 조회 |

### 토큰 정책

- refresh token 원문 미저장
- SHA-256 해시 기반 검증
- refresh 시 기존 토큰 폐기 후 신규 토큰 발급
- 폐기된 refresh token 재사용 시 해당 사용자의 전체 유효 세션 폐기
- 로그인 실패 시 계정 존재 여부와 무관한 동일 응답 반환
- 계정 미존재 시에도 해시 검증 수행을 통한 응답 시간 차이 완화

### 권한 변경 반영

access token은 자체 완결형 토큰으로 매 요청마다 DB 미조회.

- 신규 계정 생성 즉시 로그인 가능
- `user set-role` 결과는 기존 access token에 미반영
- `user set-password` 결과도 기존 access token에 미반영
- 최대 `Jwt:AccessTokenMinutes` 경과 또는 refresh 이후 변경 사항 반영
- `GET /auth/me`는 DB 직접 조회로 즉시 반영
- 실제 인가 판정은 JWT 클레임 기준

### 요청 빈도 제한

적용 대상:

- `POST /auth/login`
- `POST /auth/refresh`

정책:

- IP당 1분 20회
- 초과 시 `429 Too Many Requests`
- `Retry-After` 헤더 반환
- 집계 IP는 `CF-Connecting-IP` 우선
- 헤더 부재 시 연결 원격 주소 사용
- `X-Forwarded-For` 미사용
  - `KnownProxies` 미지정 구성에서 클라이언트 위조 가능성 존재
- `CF-Connecting-IP` 신뢰
  - Cloudflare Tunnel 단일 진입 경로 전제
  - Cloudflare의 헤더 덮어쓰기 보장 전제

## 사용자 관리

계정 생성 및 변경은 CLI 전용. 관리용 HTTP API 미제공.

```text
user list
user add <username> --display-name <표시 이름> [--role viewer|editor]
user set-role <username> <viewer|editor>
user set-password <username>
```

정책:

- `--role` 기본값 `viewer`
- 비밀번호 최소 8자
- 비밀번호 인자 전달 미지원
- 실행 후 표준 입력을 통한 비밀번호 입력
- 셸 히스토리 및 프로세스 목록 노출 방지
- `set-password` 실행 시 해당 사용자의 전체 refresh token 폐기
- 성공 종료 코드 `0`
- 실패 종료 코드 `1`

로컬 실행:

```powershell
dotnet run --project FamilyGallery.Api -- user add {계정명} --display-name "{표시 이름}" --role editor
```

컨테이너 실행:

```bash
docker compose exec -it api dotnet FamilyGallery.Api.dll user add {계정명} --display-name "{표시 이름}" --role editor
```

비밀번호 입력을 위한 `-it` 옵션 필요.

## 미디어

### API

| 엔드포인트 | 권한 | 설명 |
| --- | --- | --- |
| `GET /media` | 인증 | 촬영일시 내림차순 목록 조회, 커서 페이징 |
| `GET /media/{id}` | 인증 | 단건 조회 |
| `GET /media/{id}/original` | 인증 | 원본 바이너리 조회 |
| `HEAD /media/{id}/original` | 인증 | 원본 메타데이터 조회 |
| `GET /media/{id}/thumbnail` | 인증 | 512px WebP 썸네일 조회 |
| `DELETE /media/{id}` | `Editor` | 휴지통 기반 삭제 |

### 조회 정책

- NAS 내부 경로 미노출
- 내용 해시 미노출
- 모든 미디어 참조는 `id` 기준
- 목록 기본 `limit` 50
- 목록 최대 `limit` 200
- `nextCursor` 기반 다음 페이지 조회
- `nextCursor = null`이면 마지막 페이지
- 원본 `Range` 요청 지원
- 정상 부분 응답 `206`
- 유효하지 않은 범위 요청 `416`
- 원본 `ETag`는 SHA-256 기반
- 원본 변경 감지 시 스캔을 통한 `ETag` 갱신
- 인덱스 존재 여부와 무관하게 실제 파일 부재 시 `404`
- 서버 측 실시간 트랜스코딩 미지원

### 썸네일

- 요청 시 생성 후 디스크 캐시
- 긴 변 512px WebP
- 생성 실패 시 `404`
- 실패 결과 미캐시
- 다음 요청 시 재생성 시도
- 원본 및 썸네일 공통 `Cache-Control: private, max-age=604800`
- 썸네일 `ETag`에 규격 토큰 포함
- 크기·품질 정책 변경 시 캐시 자동 무효화

> HEIC·HEIF 썸네일 미지원  
> 배포 이미지의 ffmpeg 6.1.1 기준 HEIC demuxing 미지원. 인덱싱, 목록 조회, 원본 조회는 정상 지원하며 썸네일 요청만 `404` 반환.

## 인덱싱

미디어 목록은 파일시스템 직접 순회가 아닌 DB 인덱스 기반 제공. DSM·SMB를 통한 직접 파일 추가는 백그라운드 스캔으로 반영.

정책:

- 애플리케이션 기동 직후 1회 실행
- 이후 `Indexing:IntervalMinutes` 주기 실행
- `.` 시작 디렉터리 제외
- 심볼릭 링크 제외
- 동일 내용 파일 중복 인덱싱 방지
- 이미지 EXIF 및 영상 QuickTime 메타데이터에서 촬영일시 추출
- 촬영일시 부재 시 파일 mtime 사용
- 오프셋 없는 촬영일시에 `Indexing:TimeZone` 적용
- 회전 정보를 반영한 표시 방향 기준 해상도 저장

지원 확장자:

```text
.jpg
.jpeg
.png
.gif
.webp
.heic
.heif
.mp4
.mov
.m4v
```

각 스캔 주기에서 다음 작업을 직렬 실행:

1. 미디어 인덱싱
2. 휴지통 보존 기간 정리
3. 만료 업로드 세션 정리

개별 작업 실패가 후속 작업 및 다음 스캔 주기에 영향을 주지 않는 구조.

## 업로드

tus 1.0 기반 resumable upload. 전체 경로 `Editor` 권한 필요.

### API

| 엔드포인트 | 설명 |
| --- | --- |
| `POST /media/uploads/lookup` | 해시 배치 조회 및 중복 판정 |
| `POST /media/uploads` | 업로드 세션 생성 |
| `PATCH /media/uploads/{fileId}` | 청크 전송 |
| `HEAD /media/uploads/{fileId}` | 현재 업로드 오프셋 조회 |
| `DELETE /media/uploads/{fileId}` | 업로드 세션 취소 |
| `POST /media/uploads/{fileId}/commit` | 검증 후 원본 트리 편입 |

### 업로드 흐름

```text
lookup
  → 세션 생성
  → 청크 전송
  → commit
```

- `lookup` 최대 200건
- 기존 미디어 발견 시 `mediaId` 반환
- 중복 미디어는 업로드 세션 생성 생략

### 세션 정책

- `Upload-Metadata` 필수 값
  - `filename`
  - `contentHash`
- `contentHash`는 원본 SHA-256
- 청크 크기는 클라이언트 결정
- Cloudflare 요청 본문 상한 고려 시 5MB 청크 권장
- 파일 전체 크기는 `Upload:MaxUploadSizeBytes` 이하로 제한
- 크기 초과 시 `413`
- 제한 초과 데이터 미기록
- 스테이징 디렉터리는 갤러리 마운트 내부 구성
- 다른 볼륨 사용 시 원자적 이동 실패 가능성 존재
- 미완료 세션은 `Upload:SessionExpirationHours` 경과 후 정리

### commit

`commit` 단계에서 수행되는 검증 및 편입:

1. 업로드 완료 여부 확인
2. 선언된 크기와 실제 크기 비교
3. 선언된 SHA-256과 실제 해시 비교
4. 매직바이트 기반 파일 형식 판별
5. 저장 확장자 결정
6. 원본 트리 원자적 편입
7. 미디어 인덱스 반영

확장자는 클라이언트 파일명이 아닌 실제 파일 형식 기준 결정. 원본 파일명은 표시용 메타데이터로만 보관.

저장 경로:

```text
Uploads/{YYYY}/{MM}/{yyyyMMdd_HHmmss}_{해시 앞 8자}.{ext}
```

경로의 연·월 및 시각은 촬영일시를 `Indexing:TimeZone` 기준 현지시각으로 변환한 값 사용.

검증 실패 시 원본 트리 미편입.

### 응답

| 응답 | 조건 |
| --- | --- |
| `200 { mediaId, duplicate }` | 정상 편입 또는 중복 판정 |
| `422` | 크기 또는 해시 불일치 |
| `415` | 지원하지 않는 파일 형식 |
| `409` | 전송 미완료 |
| `404` | 업로드 세션 없음 |

### 구현 참고

`commit` 분리 이유:

- tus 완료 `PATCH` 내부에서 애플리케이션 검증 실패 응답 전달 곤란
- 이벤트에서 지정한 상태코드를 라이브러리가 `204`로 대체
- 응답 본문 작성 시 마무리 단계 예외 발생

파일 타입 판별 직접 구현 이유:

- `ftyp` 박스가 없는 실제 QuickTime 파일 존재
- 검토한 매직바이트 라이브러리의 해당 파일 판별 실패
- 실제 파일 헤더 기반 회귀 테스트 유지

## 삭제

`DELETE /media/{id}` 기반 논리 삭제. `Editor` 권한 필요.

삭제 처리:

- 실제 삭제 대신 휴지통 이동
- 이동 경로: `Upload:TrashDirectoryName/{yyyyMMdd}/{원래 상대 경로}`
- 기존 상대 경로 구조 유지
- 미디어 인덱스 즉시 제거
- 이후 조회 `404`
- 동일 내용 재업로드 허용
- 썸네일 캐시 동시 제거
- 대상 경로 정규화 후 갤러리 루트 하위 여부 검증
- 동일 날짜·동일 경로 충돌 시 접미사 추가
- 기존 휴지통 파일 보존

감사 로그:

- `MediaDeletions` 테이블 저장
- 원본 경로
- 휴지통 경로
- 내용 해시
- 파일 크기
- 수행 계정
- 삭제 시각
- 실삭제 시각
- HTTP API 미노출

보존 기간 정리:

- 스캔 주기 내 실행
- `Upload:TrashRetentionDays` 경과 항목 대상
- 감사 로그 기준 정리
- 휴지통 전체 디렉터리 순회 미사용
- 파일 삭제 후 빈 날짜 디렉터리 정리
- 실삭제 완료 시각 기록

복원 API 미제공. DSM·SMB를 통해 원래 위치로 복원 시 다음 스캔에서 재인덱싱. 복원 경로 확인은 감사 로그 기준.

## 설정

| 키 | 설명 | 기본값 |
| --- | --- | --- |
| `ConnectionStrings:Default` | SQLite 연결 문자열 | `Data Source=/data/app/family-gallery.db` |
| `Jwt:Issuer` | 토큰 발급자 | `family-gallery-api` |
| `Jwt:Audience` | 토큰 대상 | `family-gallery-app` |
| `Jwt:SigningKey` | HMAC 서명 키, 32자 이상 | 없음 |
| `Jwt:AccessTokenMinutes` | access token 유효 시간 | `30` |
| `Jwt:RefreshTokenDays` | refresh token 유효 기간 | `60` |
| `Gallery:RootPath` | NAS 갤러리 마운트 경로 | `/data/gallery` |
| `Indexing:IntervalMinutes` | 스캔 주기 | `10` |
| `Indexing:TimeZone` | 오프셋 없는 촬영일시 기준 표준시 | `Asia/Seoul` |
| `Upload:StagingDirectoryName` | 업로드 스테이징 디렉터리 | `.uploads` |
| `Upload:TrashDirectoryName` | 휴지통 디렉터리 | `.trash` |
| `Upload:MaxUploadSizeBytes` | 파일당 최대 업로드 크기 | `2147483648` |
| `Upload:SessionExpirationHours` | 미완료 세션 보존 시간 | `24` |
| `Upload:TrashRetentionDays` | 휴지통 보존 기간 | `30` |
| `Thumbnail:CachePath` | 썸네일 캐시 경로 | `/data/app/thumbnails` |
| `Thumbnail:FfmpegPath` | ffmpeg 실행 경로 | `ffmpeg` |
| `Thumbnail:MaxConcurrency` | 최대 동시 썸네일 생성 수 | `2` |
| `Thumbnail:TimeoutSeconds` | 썸네일 생성 제한 시간 | `30` |

설정 정책:

- `Jwt:SigningKey` 설정 파일 미포함
- 운영 환경은 `Jwt__SigningKey` 환경변수 사용
- 로컬 환경은 user-secrets 사용
- `ValidateOnStart` 적용
- 필수 설정 누락 시 애플리케이션 기동 실패

## 데이터베이스

- SQLite 사용
- EF Core 마이그레이션 기반 스키마 관리
- 애플리케이션 기동 시 마이그레이션 자동 적용
- 단일 인스턴스 배포 전제의 별도 마이그레이션 단계 미운영
- DB 상위 디렉터리 기동 시 자동 생성
- `journal_mode` 별도 지정 없음
- EF Core 생성 SQLite DB의 WAL 기본 사용

### 시각 값

- 전체 시각 값 UTC 기준 `DateTime` 저장
- `DateTimeOffset` 미사용
- SQLite `DateTimeOffset` 쿼리 제약 회피 목적
- SQLite 조회 과정에서 유실되는 UTC `Kind`를 `UtcDateTimeConverter`로 복원
- 전체 `DateTime` 속성 공통 적용
- JSON UTC 표기 `Z` 유지

### 마이그레이션 추가

```powershell
dotnet tool restore
dotnet ef migrations add <이름> --project FamilyGallery.Api
```

`dotnet-ef`는 로컬 도구 매니페스트로 버전 고정. 전역 설치 불필요.

## 로컬 실행

최초 1회 JWT 서명 키 등록:

```powershell
cd FamilyGallery.Api
$bytes = New-Object byte[] 48
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
dotnet user-secrets set "Jwt:SigningKey" ([Convert]::ToBase64String($bytes))
```

실행:

```powershell
dotnet run --project FamilyGallery.Api
```

확인:

- `http://localhost:5088/health`
- `http://localhost:5088/openapi/v1.json`
  - Development 환경 전용

Development 기본 경로:

| 항목 | 경로 |
| --- | --- |
| Gallery | `./.local/gallery` |
| Thumbnail | `./.local/thumbnails` |
| SQLite | `./.local/family-gallery.db` |

`.local/`은 Git 제외 대상이며 기동 시 자동 생성.

Windows Git의 대소문자 처리로 소스 디렉터리 `Data/`와 충돌할 수 있는 `data/` 경로 미사용.

## 테스트

```powershell
dotnet test --solution FamilyGallery.slnx
```

테스트 구성:

- xUnit v3 기반 통합 테스트
- `WebApplicationFactory` 기반 실제 HTTP 파이프라인 검증
- 테스트 클래스별 임시 SQLite DB 생성
- 테스트 DB별 EF Core 마이그레이션 적용
- 테스트 클래스 간 DB 상태 및 rate limit 상태 격리
- Microsoft.Testing.Platform 사용
- `global.json`을 통한 `dotnet test` 러너 고정
- 테스트 프로젝트의 배포 이미지 제외
- 썸네일 테스트만 로컬 ffmpeg 필요
- 기타 미디어 테스트는 직접 조립한 바이트 기반 실행

## 배포

Synology NAS의 Docker Compose 기반 단일 인스턴스 배포.

### 환경변수

`docker-compose.yml`과 동일 경로에 `.env` 생성:

```env
JWT_SIGNING_KEY=<32자 이상 랜덤 문자열>
```

### 실행

```bash
docker compose up -d --build
```

### 마운트

| Synology 경로 | 컨테이너 경로 | 용도 |
| --- | --- | --- |
| `/volume2/family-gallery` | `/data/gallery` | 원본 미디어 |
| `/volume2/docker/family-gallery-api/data` | `/data/app` | SQLite DB, 썸네일 캐시 |

### 권한

- 컨테이너 비root 실행
- 실행 uid `1654`
- `/data/gallery`, `/data/app` 쓰기 권한 필요
- 이미지 내부 디렉터리 사전 생성
- named volume 사용 시 uid `1654` 기준 초기화
- bind mount 사용 시 호스트 디렉터리 권한 우선
- DSM에서 별도 쓰기 권한 설정 필요
- DB 쓰기 권한 부재 시 기동 단계 실패
- 오류 메시지: `SQLite 데이터베이스에 쓸 수 없습니다`

### 외부 노출

- Cloudflare Tunnel 단일 진입 경로
- 컨테이너 포트는 호스트 loopback에만 바인딩
- NAS 포트 직접 외부 노출 미사용

## 라이선스

[MIT](./LICENSE)
