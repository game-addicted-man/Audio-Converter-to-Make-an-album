# Audio Converter

Windows용 음악 일괄 변환기. MP3와 FLAC을 함께 선택해 한 번에 저장할 수 있습니다.

| 출력 | 설정 |
| --- | --- |
| MP3 | 320 kbps CBR, 44.1 kHz, LAME 최고 알고리즘 품질 |
| FLAC | 44.1 kHz, 16/24-bit 자동 선택, 압축 수준 8 |

## 실행

`release/AudioConverter-portable.zip`을 **모두 압축 해제**한 뒤 안의 `AudioConverter.exe`를 실행합니다. `tools` 폴더는 exe 옆에 있어야 합니다. Windows 10/11 64비트 및 .NET Framework 4.8 이상 환경용입니다. Python이나 FFmpeg를 별도로 설치할 필요가 없습니다.

1. 음악 파일이나 폴더를 창에 끌어 놓거나 **파일 추가 / 폴더 추가**를 누릅니다. 폴더는 하위 폴더까지 검색합니다.
2. MP3 / FLAC 출력과 저장 위치를 선택합니다. 기본 위치는 사용자의 음악 폴더 안 `AudioConverter`입니다.
3. **변환 시작**을 누릅니다. 결과는 저장 위치의 `MP3`, `FLAC` 폴더에 생깁니다.
4. **저장 폴더 열기**로 결과를 확인합니다. 실패한 항목의 이유는 **오류 보기**에서 복사할 수 있습니다.

원본은 수정하지 않습니다. 같은 이름의 출력이 있으면 `(2)`, `(3)`을 붙입니다. 취소 시 이미 완료한 결과는 유지하고 변환 중이던 임시 파일은 삭제합니다. 목록을 유지한 채 다시 시작하면 전체 목록을 새로 변환하므로 재시도할 파일만 남기는 것이 좋습니다. 한 번에 최대 10,000개 파일을 추가할 수 있습니다.

## 음질과 지원 범위

- MP3는 손실 압축입니다. 저음질 MP3를 320 kbps 또는 FLAC으로 바꿔도 손실된 음질이 복원되지는 않습니다.
- 44.1 kHz와 다른 원본은 리샘플링합니다. FLAC 저장 자체는 무손실이지만 샘플레이트 변환 전 원본과 완전히 같은 PCM이라는 뜻은 아닙니다.
- FLAC은 16비트 이하 정수 원본을 16비트로, 24비트·32비트·부동소수점 원본을 24비트로 저장합니다. 32비트 원본은 24비트로 양자화됩니다.
- MP3는 모노/스테레오를 유지하며 다채널 입력은 스테레오로 변환합니다. FLAC은 입력 채널 수를 유지합니다. 볼륨 정규화는 하지 않습니다.
- 첫 번째 오디오 트랙과 제목·아티스트·앨범 등 텍스트 태그를 사용합니다. 커버 이미지, 가사 전용 스트림, 챕터, 다른 오디오 트랙은 복사하지 않습니다. 형식마다 지원하는 태그가 다를 수 있습니다.
- WAV, MP3, FLAC, M4A, AAC, OGG, OPUS, AIFF, WMA, APE 등 FFmpeg가 해독할 수 있는 로컬 음악 파일을 지원합니다. DRM 파일과 재생목록·온라인 스트림은 지원하지 않습니다.

## 소스 빌드

별도 NuGet 패키지가 없는 C# 5 / WinForms 앱입니다. Windows에 포함된 .NET Framework 컴파일러를 사용합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
```

`vendor/ffmpeg-release-essentials.zip`이 없으면 `build.ps1 -DownloadEngine`으로 Gyan 배포 파일과 SHA-256을 다운로드할 수 있습니다. 기존 다운로드가 있으면 해당 ZIP의 SHA-256을 검증한 뒤 사용합니다. 빌드 결과, 배포 ZIP, 실행 파일 SHA-256은 `release`에 생성됩니다.

변환 코드는 `src/ConversionEngine.cs`, 화면은 `src/Program.cs`입니다. 테스트는 실제 FFmpeg로 fixture를 만들고 출력 사양, PCM 동일성, 메타데이터, 원본 보존, 중복 이름, 손상 입력, 취소 및 임시 파일 정리를 검사합니다.

## 변환 엔진

FFmpeg 공식 다운로드 페이지에서 안내하는 Gyan Windows Essentials 빌드를 사용합니다. 배포된 엔진의 정확한 버전, 체크섬과 소스 링크는 패키지의 `THIRD-PARTY-NOTICES.txt`와 `tools/BUILD-INFO.txt`를 확인하세요.

- FFmpeg: https://ffmpeg.org/download.html
- Gyan builds: https://www.gyan.dev/ffmpeg/builds/
- 인코더 옵션: https://ffmpeg.org/ffmpeg-codecs.html
- FLAC: https://xiph.org/flac/faq.html

앱 소스는 `LICENSE`를, FFmpeg 엔진은 동봉한 별도 라이선스를 따릅니다.
