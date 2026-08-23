# family-gallery-api

가정용 시놀로지 NAS의 이미지·영상을 가족 구성원에게만 제공하는 API.

- 조회는 로그인한 사용자 전원, 업로드·삭제는 `editor` 권한 계정만
- 원본 수정 경로 없음. 쓰기는 신규 추가와 삭제뿐이며 삭제는 휴지통 이동으로 처리
- 외부 노출은 Cloudflare Tunnel 단일 경로
- 클라이언트는 Flutter 앱(`family-gallery-app`) 단독

## 요구 사항

- .NET SDK 10.0
- ffmpeg (썸네일 생성. 로컬 실행 시 `PATH`에 필요하며 배포 이미지에는 포함)
- (배포) Docker / Docker Compose

## 프로젝트 구조

```
.config/
  dotnet-tools.json   로컬 도구 매니페스트 (dotnet-ef)
global.json           dotnet test 러너 지정
FamilyGallery.slnx
FamilyGallery.Api/
  Program.cs          서비스 등록 / 파이프라인 / DB 초기화
  Options/            Jwt, Gallery, Indexing, Thumbnail 설정 바인딩
  Data/               AppDbContext, Entities, 값 변환기
  Migrations/         EF Core 마이그레이션
  Endpoints/          엔드포인트 매핑 확장 메서드
  Services/           토큰 발급, 미디어 인덱싱, 썸네일 생성
  Cli/                계정 관리 명령
FamilyGallery.Api.Tests/
  ApiFactory.cs       테스트용 호스트 / 임시 DB
  MediaFixtures.cs    JPEG·MP4 바이트 조립
  FfmpegFixtures.cs   디코딩 가능한 미디어 생성
  *Tests.cs           통합 테스트
Dockerfile
docker-compose.yml    NAS 배포용
```

## 인증 / 인가

- JWT Bearer 스킴. issuer / audience / 만료 / 서명 키 전부 검증
- 인가 fallback policy 적용. 전 엔드포인트 인증 필수가 기본값
- 익명 허용은 `/health`, 개발용 OpenAPI 문서뿐
- 권한은 `User.Role`의 `Viewer` / `Editor` 2종. 조회 범위는 권한과 무관하게 동일하고, 업로드·삭제만 `Editor`로 제한
- 클레임은 `sub` / `name` / `role` / `jti`. 인바운드 클레임 매핑 비활성화로 발급·검증 이름 일치

| 엔드포인트 | 인증 | 설명 |
| --- | --- | --- |
| `POST /auth/login` | 익명 | 자격 증명 검증 후 토큰 쌍 + 사용자 정보 반환 |
| `POST /auth/refresh` | 익명 | refresh token 회전 발급 |
| `POST /auth/logout` | 필요 | 제시한 refresh token 폐기 |
| `GET /auth/me` | 필요 | 현재 사용자 정보 (DB 조회) |

- `POST /auth/login`과 `POST /auth/refresh`는 요청 빈도 제한. 초과 시 `429`와 `Retry-After` 헤더 반환
- refresh token은 원문 미저장. SHA-256 해시로 대조하고 사용 시 회전 발급
- 폐기된 refresh token이 다시 제시되면 탈취로 간주해 해당 사용자의 유효한 세션 전부 차단
- 로그인 실패는 계정 존재 여부와 무관하게 동일 응답. 계정 부재 시에도 해시 검증을 수행해 응답 시간 차이 제거

권한 변경 반영 시점에 주의. access token은 자체 완결적이라 매 요청마다 DB를 조회하지 않음.

- `user add`로 만든 계정은 즉시 로그인 가능
- `user set-role` / `user set-password`는 **이미 발급된 access token에 반영되지 않음**. 최대 `Jwt:AccessTokenMinutes`(기본 30분) 경과 또는 refresh 시점에 반영
- `GET /auth/me`는 DB를 조회하므로 즉시 반영. 인가 판정은 클레임 기준이라 지연

### 요청 빈도 제한

- 인증 없이 반복 호출 가능한 `POST /auth/login`, `POST /auth/refresh`만 대상. 나머지는 토큰 자체가 관문
- IP당 1분에 20회. 가족 단위 사용량 기준이며 정상적인 재시도는 허용하고 무차별 대입만 차단
- 집계 기준 IP는 `CF-Connecting-IP` 헤더 우선, 없으면 연결 원격 주소
  - `X-Forwarded-For`는 `KnownProxies`를 비워둔 구성상 클라이언트가 위조할 수 있어 사용하지 않음
  - `CF-Connecting-IP`는 Cloudflare가 항상 덮어쓰므로 Tunnel 단일 경로 전제에서 신뢰 가능

## 사용자 관리

계정 생성·권한 변경은 CLI로만 수행. 관리용 HTTP 엔드포인트 미제공.

```
user list
user add <username> --display-name <표시 이름> [--role viewer|editor]
user set-role <username> <viewer|editor>
user set-password <username>
```

- `--role` 기본값은 `viewer`
- 비밀번호는 인자로 받지 않고 실행 후 표준 입력으로 수신. 셸 히스토리와 프로세스 목록 노출 방지
- 비밀번호는 8자 이상. `set-password` 실행 시 해당 사용자의 유효한 refresh token 전부 폐기
- 성공은 종료 코드 `0`, 실패는 `1`

로컬:

```powershell
dotnet run --project FamilyGallery.Api -- user add {계정명} --display-name "{표시 이름}" --role editor
```

컨테이너 (비밀번호 입력을 위해 `-it` 필요):

```bash
docker compose exec -it api dotnet FamilyGallery.Api.dll user add {계정명} --display-name "{표시 이름}" --role editor
```

## 미디어

전 엔드포인트 인증 필요. 조회 범위는 권한과 무관하게 동일.

| 엔드포인트 | 설명 |
| --- | --- |
| `GET /media` | 촬영일시 내림차순 목록. 커서 페이징 |
| `GET /media/{id}` | 단건 조회 |
| `GET`·`HEAD /media/{id}/original` | 원본 바이너리. `Range` 요청 지원 |
| `GET /media/{id}/thumbnail` | 썸네일. 긴 변 512px WebP |

- 응답에 NAS 경로와 내용 해시 미노출. 미디어 참조는 `id` 기반이며 원본·썸네일 URL도 `id`에서 도출
- 목록은 `limit` 기본 `50`, 최대 `200`. `nextCursor`를 그대로 다음 요청에 전달하고 `null`이면 마지막 페이지
- 원본은 `Range` 처리로 `206` / `416` 응답. `ETag`는 원본 내용의 SHA-256이며 파일 교체 시 스캔으로 갱신
- 썸네일은 요청 시점에 생성해 디스크에 캐시. 생성 실패는 `404`이고 다음 요청에 다시 시도
- 원본·썸네일 모두 `Cache-Control: private, max-age=604800`. 썸네일 `ETag`에는 규격 토큰이 붙어 크기·품질 변경 시 함께 무효화
- 인덱스에 있어도 파일이 없으면 `404`
- 서버 측 실시간 트랜스코딩 없음

**HEIC·HEIF 썸네일 미지원.** 배포 이미지의 ffmpeg가 6.1.1이고 HEIC 디먹싱은 7.1부터 추가됨.
인덱싱·목록·원본 조회는 정상 동작하며 썸네일 요청만 `404` 응답.

### 인덱싱

목록은 파일시스템 순회가 아닌 DB 인덱스 기반. DSM·SMB로 직접 투입한 파일은 백그라운드 스캐너가 따라감.

- 기동 직후 1회 실행 후 `Indexing:IntervalMinutes` 주기 반복
- `.`으로 시작하는 디렉터리와 심볼릭 링크는 순회에서 제외
- 확장자 화이트리스트: `.jpg` `.jpeg` `.png` `.gif` `.webp` `.heic` `.heif` `.mp4` `.mov` `.m4v`
- 내용이 같은 파일은 한 번만 인덱싱
- 촬영일시는 이미지 EXIF와 영상 QuickTime 메타에서 추출. 없으면 파일 mtime으로 대체
  - 오프셋 정보가 없는 촬영일시에는 `Indexing:TimeZone` 적용
- 해상도는 표시 방향 기준. 회전 정보를 반영해 교환

## 설정

| 키 | 설명 | 기본값 |
| --- | --- | --- |
| `ConnectionStrings:Default` | SQLite 연결 문자열 | `Data Source=/data/app/family-gallery.db` |
| `Jwt:Issuer` | 토큰 발급자 | `family-gallery-api` |
| `Jwt:Audience` | 토큰 대상 | `family-gallery-app` |
| `Jwt:SigningKey` | HMAC 서명 키 (32자 이상) | **없음. 반드시 외부 주입** |
| `Jwt:AccessTokenMinutes` | access token 유효 시간(분) | `30` |
| `Jwt:RefreshTokenDays` | refresh token 유효 기간(일) | `60` |
| `Gallery:RootPath` | NAS 마운트 경로 (읽기·쓰기) | `/data/gallery` |
| `Indexing:IntervalMinutes` | 스캔 주기(분) | `10` |
| `Indexing:TimeZone` | 오프셋 태그가 없는 촬영일시에 적용할 표준시 | `Asia/Seoul` |
| `Thumbnail:CachePath` | 썸네일 디스크 캐시 루트 | `/data/app/thumbnails` |
| `Thumbnail:FfmpegPath` | ffmpeg 실행 파일 경로 | `ffmpeg` |
| `Thumbnail:MaxConcurrency` | 동시 생성 수 상한 | `2` |
| `Thumbnail:TimeoutSeconds` | 개별 생성 제한 시간(초) | `30` |

- `Jwt:SigningKey`는 설정 파일에 미포함. 운영은 환경변수 `Jwt__SigningKey`, 로컬은 user-secrets 사용
- `ValidateOnStart` 적용. 필수 설정 누락 시 기동 단계에서 실패

## 데이터베이스

- SQLite. 스키마는 EF Core 마이그레이션으로 관리
- 기동 시 마이그레이션 자동 적용. 단일 인스턴스 배포이므로 별도 적용 절차 없음
- DB 파일의 상위 디렉터리는 기동 시 자동 생성. (SQLite가 직접 만들지 않아 최초 실행이 실패하는 것을 막음)
- `journal_mode`는 명시 설정하지 않음. EF Core가 생성하는 SQLite DB는 WAL이 기본값

### 시각 값

- 시각 값은 모두 UTC 기준 `DateTime`으로 저장·조회
- SQLite의 `DateTimeOffset` 쿼리 제약을 피하기 위해 `DateTimeOffset`은 사용하지 않음
- SQLite 조회 시 사라지는 UTC `Kind`는 `UtcDateTimeConverter`에서 복원
- 모든 `DateTime` 속성에 공통 적용되어 JSON 응답의 UTC 표기(`Z`)를 일관되게 유지

마이그레이션 추가:

```powershell
dotnet tool restore
dotnet ef migrations add <이름> --project FamilyGallery.Api
```

`dotnet-ef`는 로컬 도구로 버전 고정. 전역 설치 불필요.

## 로컬 실행

서명 키를 user-secrets에 등록 (최초 1회).

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

- `http://localhost:5088/health` → `{"status":"ok","version":"..."}`
- `http://localhost:5088/openapi/v1.json` (Development 전용)

Development 환경 기본값은 `Gallery:RootPath` = `./.local/gallery`, `Thumbnail:CachePath` = `./.local/thumbnails`, SQLite = `./.local/family-gallery.db`. `.local/`은 git 제외 대상이며 기동 시 자동 생성. 소스 폴더 `Data/`와 대소문자만 다른 `data/`는 Windows git이 함께 무시하므로 미사용.

## 테스트

```powershell
dotnet test --solution FamilyGallery.slnx
```

- xUnit v3 기반 통합 테스트. `WebApplicationFactory`로 테스트 호스트를 띄워 실제 HTTP 파이프라인 검증
- 테스트 클래스마다 임시 파일 SQLite를 생성하고 마이그레이션까지 적용. DB와 요청 빈도 제한 상태가 클래스 간에 섞이지 않음
- xUnit v3의 Microsoft.Testing.Platform 지원을 사용하며, `dotnet test`도 같은 러너를 사용하도록 `global.json`에서 지정
- 테스트 프로젝트는 `Dockerfile`의 게시 대상이 아니므로 배포 이미지에 포함되지 않음
- 썸네일 테스트는 `PATH`의 ffmpeg 필요. 그 외 테스트는 바이트를 직접 조립해 ffmpeg 없이 실행

## 배포 (Synology NAS)

`docker-compose.yml`과 같은 위치에 `.env` 배치 후 서명 키 지정.

```
JWT_SIGNING_KEY=<32자 이상 랜덤 문자열>
```

```bash
docker compose up -d --build
```

마운트:

- `/volume2/family-gallery` → `/data/gallery` (원본, 읽기·쓰기)
- `/volume2/docker/family-gallery-api/data` → `/data/app` (SQLite DB 및 썸네일 캐시)

- 컨테이너는 비root 계정(uid 1654)으로 실행되며, 두 마운트 경로 모두 해당 uid에 대한 쓰기 권한이 필요
  - 이미지에 `/data/app`, `/data/gallery` 디렉터리를 미리 생성하므로 named volume은 해당 uid 소유로 초기화됨
  - bind mount는 호스트 디렉터리의 권한이 우선하므로 DSM에서 별도 권한 설정 필요
  - 쓰기 권한이 없으면 기동 단계에서 `SQLite 데이터베이스에 쓸 수 없습니다` 오류로 중단
- 외부 노출은 Cloudflare Tunnel 단일 경로로 구성하며, 컨테이너 포트는 호스트 loopback에만 바인딩

## 라이선스

[MIT](./LICENSE)
