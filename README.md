# ai-reasoning-hotkeys

StrokesPlus.net의 `Ctrl + Alt + ↑ / ↓`로 ChatGPT Windows 앱의 Codex와 Claude Desktop의 추론 강도를 한 단계씩 바꿉니다.

| 앱과 모델 | 단계 순서 |
|---|---|
| ChatGPT Windows · Codex · GPT-6.1 Sol | Light → Medium → High → Extra High → Ultra |
| Claude Desktop · Opus 5.5 | 낮음 → 중간(보통) → 높음 → 엑스트라 → 최대 → Ultracode |

위쪽 방향키는 한 단계 올리고 아래쪽 방향키는 한 단계 내립니다. 양 끝에서는 현재 단계를 유지합니다. 다른 모델에서는 변경을 중단하고, 다른 앱에서는 원래 키 조합을 전달합니다.

## 설치

Windows, x64 .NET Framework 및 StrokesPlus.net이 필요합니다. StrokesPlus.net 0.5.7.8의 기본 설치 경로(`C:\Program Files\StrokesPlus.net`)에서 확인했습니다.

1. 저장소를 내려받습니다. 권장 폴더는 Windows 문서 폴더의 `_ETC\ai-reasoning-hotkeys`입니다.
2. 실행 중인 StrokesPlus.net을 트레이 메뉴의 **Exit**로 종료합니다.
3. 내려받은 폴더에서 다음 명령을 실행합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

설치 프로그램은 StrokesPlus 설정을 백업하고 `AI Reasoning` 분류에 두 단축키를 등록한 뒤 StrokesPlus를 실행합니다. 다른 설정과 단축키는 보존합니다. 실행 경로는 설치한 폴더에 맞춰 생성하므로 다른 폴더에 설치해도 됩니다. 폴더를 이동하거나 이름을 바꾸면 새 위치에서 설치를 다시 실행하세요.

설정 변경 없이 확인하거나 두 단축키만 제거하려면 다음 명령을 사용합니다.

```powershell
.\Install.ps1 -DryRun
.\Install.ps1 -Undo
```

`Up.js`와 `Down.js`는 수동 등록용 스크립트입니다. 기본 경로는 Windows 문서 폴더의 `_ETC\ai-reasoning-hotkeys`이며, 다른 위치에서는 `reasoningHelper`를 변경하세요. `Hotkeys.json`은 설치 프로그램이 사용하는 템플릿입니다.

## 동작

팝업이 닫혀 있으면 접근성 정보에서 현재 모델과 강도, 버튼 위치를 읽고 실제 마우스 클릭으로 엽니다. 이미 열려 있으면 그대로 인식합니다. 고정된 안내 문구를 StrokesPlus의 이미지 검색으로 찾아 슬라이더의 현재 흰 손잡이를 읽고, 목표 칸을 클릭한 뒤 변경을 확인합니다.

변경 후 채팅 입력란을 클릭합니다. GPT에서는 팝업이 보이는 동안 입력란에 포커스가 있다는 접근성 정보만으로 클릭을 생략하지 않습니다. **팝업 닫힘과 입력란 포커스**를 함께 확인하고, 확인되지 않으면 활성 창과 위치를 다시 검사한 뒤 한 번만 재시도합니다. Claude는 입력란을 한 번 클릭합니다.

각 클릭 전에 Ctrl·Alt·방향키가 놓였는지 확인합니다. Esc, SendKeys, 강제 SetFocus는 사용하지 않습니다. 활성 창이 바뀌거나 모델·이미지·입력란 위치를 확인할 수 없으면 추가 클릭을 중단합니다.

참조 이미지는 한국어 UI와 밝은 테마에서 캡처했습니다. 모델 이름, 화면 배율 또는 앱 UI가 달라져 이미지와 일치하지 않으면 전환을 중단합니다. 테마와 배율이 다른 환경은 별도 조정이 필요합니다.

## 빌드와 검사

```powershell
.\Build.ps1
.\ReasoningSwitch.exe --selftest
```

빌드는 네 C# 소스를 x64 실행 파일로 컴파일하고 자체 검사에 통과한 파일만 적용합니다. 이미지 검사에는 설치된 StrokesPlus.net이 필요합니다. `--selftest`는 첨부 이미지와 생성한 검사 캔버스만 사용하며 앱 화면을 캡처하거나 입력하지 않습니다.

현재 버전은 `17-gpt-confirmed-dismiss`입니다. 단계 이름·양방향 이동·경계, 실제 참조 이미지의 손잡이, 팝업이 남은 상태의 포커스 정보, 첫 클릭이 무시된 경우의 재시도, 입력란 클릭 위치를 검사합니다. 사용자 실행 로그에서도 GPT의 팝업 닫힘·입력란 포커스와 Claude의 입력란 포커스가 확인됐습니다.

## 진단 파일

- `last-result*.txt`: 최근 실행 결과
- `last-action-chatgpt.txt`, `last-action-claude.txt`: 앱별 단계·소요 시간·포커스 결과
- `last-diagnostic*.txt`: 오류 발생 시 모델·추론 컨트롤 정보
- `chat-input-*.txt`: 같은 창에서 재사용하는 창·프로세스 번호와 입력란 위치
- `StrokesPlus.before-*.json`: 설치 전 개인 설정 백업

실행 파일은 네트워크나 클립보드에 접근하지 않으며 대화를 전송하지 않습니다. 입력란의 본문을 읽거나 로그로 저장하지 않습니다. 화면 캡처는 이미지 검색을 위해 메모리에서만 사용합니다. 개인 설정 백업과 진단·위치 파일은 Git에서 제외됩니다.
