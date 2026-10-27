# Windows 포트 검증 기록

## 2026-10-28 · 0.2.29 재실행 시 듣기 상태

- 이전 시작 경로는 `--listen` 없이는 마이크를 시작하지 않았습니다. `StartupListening=restore/always/off`를 설정 UI·검증·가져오기 미리 보기에 연결했습니다. 기본 복원 모드는 과거 버전에서 처음 업데이트할 때 꺼짐을 유지하며 명시적으로 듣기를 켠 이후부터 복원합니다.
- `listening-state.json`에 사용자 의도를 원자적으로 저장합니다. 설정 재적용·일시 입력 중지·인식 프로세스 복구가 이를 지우지 않으며, 직접 듣기를 끄면 다음 실행에도 꺼집니다. 저장 실패는 기존 파일/메모리 상태를 보존하고 안내합니다. 업데이트 백업과 복원 검사에도 포함합니다.
- 개발본 일반 검사 **373/373**: 상태 파일 없음·시작/중지 후 재로드·3개 모드·초기 설정 준비 조건·손상/초과 크기/지원하지 않는 버전·파일 잠금 저장 실패·이전 설정 호환·가져오기 선택을 포함합니다. 별도 격리 App 검사 **4/4**: 실제 WPF 저장 처리로 3개 모드 저장/재로드, 임시 중지와 설정 저장 시 의도 유지, 직접 중지 저장, 초기 설정 미완료 시 `--listen` 차단. `artifacts/checks.json`, `artifacts/listening-app-checks.json`.
- 이번 검사는 실제 마이크 캡처나 유튜브 창 열기를 실행하지 않았습니다. 현재 사용자의 발화 인식률·오인식·마이크 입력 신호 및 사용자 PC에서 듣기 켠 상태의 재실행 실측은 별도입니다.
- 0.2.29 MSI **99,676,850 bytes**, SHA-256 `74A8D6296BC7483533584D68AF408CF0E0DA1A322CD1ED821D43D9F4304E16BC`. 0.2.28 백업 `install-backups/Sasarisa-0.2.28-20261027-172155-31d78e.zip`의 1,764개 파일과 새 설치본의 1,764개 파일이 각각 명세와 일치합니다. 설치 후 Windows 기본 PATH만 사용한 격리 검사 **373/373**, 실제 App/WPF 검사 **4/4**. 설정 JSON 5개와 모델 파일 5개의 해시를 보존했습니다.
- 일반 앱 PID 49228 재실행 후 응답·창 제목을 확인했습니다. 시작 로그는 `mode=restore; requested=False; ready=True; start=False`, 음성 프로세스 0개이며 원래 없던 상태 파일을 임의로 생성하지 않았습니다. 소스 ZIP 180개 항목 중 Windows 파일 179개가 빌드 시 소스와 일치하고 개인 파일/모델/빌드 산출물은 없었습니다. 이 설치 결과 기록만 패키지 생성 뒤 추가됐습니다. `artifacts/upgrade-result-0229.json`, `artifacts/installed-self-test-0229`, `artifacts/source-check-0229.json`, `artifacts/final-state-0229.json`.

## 2026-10-28 · 0.2.28 휴대폰 명령 결과·PC 음성 안내

- 인터넷 미리 보기 코드가 시계 명령만 허용하고 `Speak=false`를 사용함을 확인했습니다. 일반 로컬·인터넷 연결은 같은 `RunRemoteCommandAsync → SubmitAsync → SayAsync` 경로를 사용합니다. 사용자의 당시 실제 연결 토큰은 확인하지 않았으므로 시험 연결이 당시 원인이라고 단정하지 않습니다. 새 PC는 연결 종류와 앱 버전을 인증된 폰에 제공하며, 시험/일반/구버전 미확인을 화면에서 구분합니다.
- `list_timers`가 실제 내용을 내부 system 메시지로만 표시하고 폰에는 일반 완료 문구만 보내던 문제를 실제 App 검사에서 재현하고 수정했습니다. 타이머 생성·목록·취소를 모두 실제 저장 상태와 답변으로 확인했습니다.
- PC 무료 연결 창의 답변 읽기 체크박스가 기존 전역 설정을 저장하며, 다른 출력 장치·음량은 보존합니다. 읽기 꺼짐/음량 0/출력 실패를 원격 답변에 표시하고 긴 답변 때문에 이유가 잘리지 않게 앞에 배치했습니다. 실제 로컬 App 검사 10개, 중계/웹 상태 검사 22개 통과. Windows SAPI 생성→WASAPI 재생 완료는 시험 음량 0으로 확인했으며 사람에게 들린 음성을 검증한 것은 아닙니다.
- 설치본·공개 HTTPS·배포 검증 결과는 아래에 추가합니다. 유튜브를 여는 검사는 반복하지 않습니다.
- 공개 HTTPS를 통해 실제 Windows App의 시각, 타이머 생성·목록·취소, 중복 방지·취소·해제, 음량 0의 Windows 음성 재생과 실제 Codex 질문 `2 더하기 3` → `5` 답변까지 **10개 통과**. Codex 요청은 현재 PC CLI 계정으로 실행했으며 시험 앱은 종료했습니다. `artifacts/internet-phone-ai-checks-0228.json`, `internet-phone-ai-home-0228/assistant.log`.
- **0.2.28 설치 완료**: MSI 종료 0, 설치본 0.2.28.0. Windows 기본 PATH·격리 프로필에서 기본 367개, Pro 원격 5개, 공유 5개, 로컬 폰 10개, 인터넷 폰 9개, 콤보 3개, 합계 **399개 통과**. 설치 파일과 이전 0.2.27 복원 백업의 각 1,764개 해시가 일치하며 기존 사용자 JSON 5개·한국어 모델 5개를 보존했습니다. 일반 앱 PID 20720, 응답 정상·제목 `자비스 · Windows`를 확인했습니다. Codex·읽기 켜짐·음량 85·Chrome 유튜브 선택을 유지합니다. `artifacts/upgrade-result-0228.json`, `installed-and-backup-0228.json`, `installed-self-test-0228/`, `final-state-0228.json`.
- MSI **99,664,562 bytes**, SHA-256 `B13108D7FC1DEAF2C14FEAEA956600B3F721635E1F7EBD453E7DFB516CEA994B`. 복원 백업 `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.27-20261027-170611-0416fc.zip`. 소스 압축 176개 항목 중 Windows 파일 175개가 작업 트리와 바이트 단위로 일치하며 비공개/빌드/모델/가상환경 파일이 없습니다. `artifacts/source-check-0228.json`. 이 최종 기록은 패키지 생성 뒤 추가했으며 포함 문서는 생성 시점 상태입니다.
- Mini PC 중계의 기존 5개 배포 파일을 `backups/release-before-0228-20261028-020511.zip`에 백업하고 압축 내용을 검증한 뒤 교체·재시작했습니다. 실패 시 기존 파일 복구 경로를 적용했으며 Caddy 구성은 전후 동일합니다. 공개 HTTPS의 HTML/JS/CSS/소스 압축이 로컬 배포 파일과 모두 일치합니다. `artifacts/relay-deployment-0228.json`, `live-relay-0228.json`.
- Chrome 390×844에서 실제 페어링 후 시험 연결 제목·버전·시계 전용/PC 음성 꺼짐 안내의 줄바꿈과 가독성을 확인했습니다. 루프백 화면 시험이며 PC 동작을 실행하지 않는 전용 서버였습니다. 탭·서버를 종료하고 브라우저 크기를 복원했습니다. `artifacts/phone-preview-banner-0228.png`. 실제 휴대폰·회사망·사용자 발화/음질과 보고된 마이크 무반응 해결, 원본 전체 이식 완료는 별도입니다.

## 2026-10-28 · 0.2.27 유튜브 브라우저 계정·콤보

- Chrome/Edge의 일반 프로필에서 영상을 여는 저장 설정, 전용 플레이어에서 현재 영상과 이후 재생 방식 전환, 수동 YouTube 로그인 열기를 구현했습니다. 쿠키 복사, 원격 디버깅, 보안 우회는 사용하지 않습니다. 새 창·단일 탭·영상 주소·프로세스 수명을 확인하며 탭이나 주소가 바뀌면 조작하지 않습니다.
- 브라우저 전용 모의 검사 5개 통과: URL/설정 교환, 이전 영상 정지·종료 순서, 로그인/다른 탭 보호, 찾지 못한 창의 미제어, 취소/실패 시 중복 재생 방지. 실제 Chrome에서 정상 프로필로 열기와 일시정지 후 위치 3초·길이 634초를 관측했습니다. 최종 탐색 상태 검사는 통과하지 못해 브라우저 시간 이동 기능을 비활성화하고 직접 이동 안내를 반환하도록 했습니다. 사용자가 반복되는 시험 창을 지적하여 추가 브라우저 검사를 중단했습니다. 프리미엄 계정 로그인·광고 제거, Edge와 다음 영상 동작은 검증하지 않았습니다. `artifacts/video-browser-native-0227.json`, `video-browser-home-0227/assistant.log`.
- 콤보 분류/설정 모의 검사 6개 통과. 실제 App 네이티브 검사 6개 중 5개 통과: WPF 제공자 선택/모델 보존, 로컬 시계, 원격 Aside 제한, 실제 Codex/Claude MCP 시각 호출. Aside 경로는 브라우저 연결 실패를 정확히 반환했으며 성공으로 처리하지 않았습니다. `artifacts/combo-native-app-0227.json`.
- 네이티브 UI 도구는 pipe 연결 실패 상태입니다. 계정 로그인은 자동화하지 않았습니다. 사용자 설정의 AI 제공자 변경은 하지 않습니다. 설치본 회귀·백업 결과는 아래에 추가합니다.
- **0.2.27 설치 완료**: MSI 종료 0, 설치본 0.2.27.0. Windows 기본 PATH·격리 프로필에서 기본 검사 **367개**, 실제 App의 원격/공유/로컬 휴대폰 각 5개, 인터넷 휴대폰 6개, 콤보 WPF/명령 3개, 합계 **391개 통과**. 브라우저를 여는 네이티브 검사는 다시 실행하지 않았습니다. 새 설치 파일 1,764개와 이전 0.2.26 복원 백업 1,764개가 매니페스트와 일치하며 기존 사용자 JSON 5개·한국어 모델 5개를 보존했습니다. 소스 압축 174개 항목 중 Windows 파일 173개가 작업 트리와 바이트 단위로 일치하고 artifacts/bin/obj/models/가상환경/백업은 없습니다.
- 검증 후 사용자 설정을 별도로 백업하고 `YouTubePlayer=chrome`만 적용했습니다. 다른 설정 값은 그대로이며 현재 선택은 Codex·답변 읽기 켜짐·음량 85입니다. 일반 앱을 PID 30168로 재실행했습니다. 유튜브 프리미엄 로그인·광고 없는 재생은 사용자의 브라우저에서 직접 확인해야 합니다. 브라우저의 시간 이동은 실패를 숨기지 않고 현재 미지원 안내를 반환합니다.
- MSI **99,639,986 bytes**, SHA-256 `9767BF994D3F3167CBD03A843A470BD5CF40E6E692F5A1A572815A08239A0DE1`. 복원 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.26-20261027-165140-886fdd.zip`. 근거: `artifacts/upgrade-result-0227.json`, `installed-and-backup-0227.json`, `installed-self-test-0227/`, `source-check-0227.json`, `release-apps-0227.log`, `final-state-0227.json`. 이 설치 기록은 패키지 생성 후 추가했으며 포함 문서는 생성 시점 상태입니다. 기존 인터넷 중계 서버와 그 소스 배포는 이번 변경 대상이 아닙니다.

## 2026-10-28 · 0.2.26 인터넷 휴대폰 연결·음성 입력

- `휴대폰 연결 · 무료`에 **인터넷**과 **같은 와이파이 · 로컬 연결** 선택을 추가했습니다. 연결을 끈 뒤 방식을 바꿉니다. 기존 LAN HTTP 17845/8자리 코드는 유지하며 인터넷은 `https://sasarisa.xsw.kr`/12자리 코드를 사용합니다. PC와 폰이 각각 HTTPS로 접속하고 원본 Pro 계정 없이 기존 App 명령 처리 및 PC 음성 답변 설정을 사용합니다.
- Node 내장 모듈 중계는 코드 10분·세션 8시간·최대 4대, 개별 토큰, Host/Origin 검사, 속도·본문·동시 실행 제한, 취소·로그아웃·PC 연결 해제와 요청 ID 중복 방지를 적용합니다. 명령·응답은 서버 메모리에서만 처리하며 종단 간 암호화는 아닙니다. 폰 웹 음성 입력은 명시적 시작·권한 허용 후 한국어 Web Speech를 사용하고, 인식문 확인 뒤 보내기를 눌러야 실행합니다. 30초/화면 숨김 중단과 권한 오류 안내, 지원하지 않는 환경의 키보드 입력을 유지합니다.
- 실제 HTTP 중계 검사 **12개**, 브라우저 음성 상태 검사 **6개**, 합계 **18개** 통과. 실제 폰의 음성 인식 시험이 아닌 HTTP 및 모의 브라우저 상태 검사입니다. `artifacts/relay-checks-0226.log`.
- 설치본 기본 검사 **356개**, Pro 원격·공유·LAN App 각 **5개**, 실제 HTTPS 인터넷 App **6개**, 합계 **377개** 통과. 인터넷 App 검사는 Pro/LAN 없이 실제 시각 응답, 중복 요청 미실행, PC 확인 제한, 작업 중 거절, 대기 중 취소, 연결 해제 후 세션 폐기를 확인했습니다. 최초 묶음 실행이 세 항목 뒤 결과 없이 종료되어 해당 인터넷 검사를 단독 실행하고 전체 묶음을 재실행했으며 모두 통과했습니다. `artifacts/installed-self-test-0226/`, `final-state-0226.json`.
- **PC 음성 출력 확인**: 설치된 DLL의 실제 HTTPS 중계 → RemotePump → Windows SpeechOutput 경로에서 시험 요청 한 번으로 재생 시작·정상 종료, 출력 PCM peak **0.210869**, 기존 음량 **85**를 확인했습니다. 사용자 설정의 `Speak=true`와 Windows 제공자는 변경하지 않았습니다. 이 검사는 음성 출력 경로 시험이며 실물 폰 마이크나 일반 App 전체 음성 왕복 시험은 아닙니다. `artifacts/internet-audio-probe-0226.log`.
- Chrome **390×844** 화면에서 실제 공개 HTTPS 주소·코드 연결 → 시각 명령 → App 응답 표시·로그아웃을 확인했고 브라우저 오류가 없었습니다. 음성 입력 버튼은 표시/활성 상태만 확인했으며 실제 마이크 권한·발화는 시험하지 않았습니다. `artifacts/phone-internet-0226.png`. 시계만 허용하고 음성을 끈 별도 미리보기를 사용자가 실사용 연결로 혼동하여 안내했습니다. 해당 미리보기와 임시 코드는 종료·폐기했고 최종 일반 앱은 미리보기 인자 없이 실행했습니다. **일반 앱에는 시각 명령 전용 제한이 없습니다.**
- Mini PC에 `sasarisa-relay.service`와 해당 Caddy 경로 복구 타이머를 배포했습니다. 기존 디렉터리와 Caddy JSON을 복원 가능한 형태로 백업한 뒤 호스트 전용 경로 하나만 추가했으며 나머지 Caddy 설정은 동일함을 확인했습니다. 기존 `jarvis.xsw.kr` 서비스는 변경하지 않았습니다. 유효한 HTTPS 인증서·공개 페이지·실제 App 왕복을 확인했습니다. 배포 파일 해시 5개와 공개 소스 압축이 최종 패키지에 일치합니다. `artifacts/relay-deployment-0226.json`, `source-check-0226.json`.
- **0.2.26 설치 완료**: MSI 종료 0, 설치본 0.2.26.0. 설치 파일 **1,764개** 및 이전 0.2.25 백업 파일 **1,764개**의 매니페스트 해시 일치. 사용자 JSON 5개·한국어 모델 5개를 보존했고 일반 앱 `자비스 · Windows`를 재실행했습니다. 소스 압축 169개 항목 중 관련 22개 파일을 작업 트리와 바이트 단위로 확인했고 artifacts/bin/obj/models/가상환경/비공개 백업은 없습니다. `artifacts/upgrade-result-0226.json`, `installed-and-backup-0226.json`, `source-check-0226.json`.
- MSI **99,611,314 bytes**, SHA-256 `33C3E25654DB76A98690BE8B210666E0A07B6CD5A676D74F319481808EB77851`. 복원 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.25-20261027-161417-1ce85f.zip`. 이 검증 기록은 패키지 생성 후 작성했으며 포함 소스의 문서는 생성 당시 상태입니다.
- 실제 휴대폰·직장망 접속, 휴대폰 마이크 권한·인식 정확도, 보고된 PC 마이크 무반응 회복 및 원본 전체 기능 완료는 별도입니다. 네이티브 UI 도구가 pipe 연결 오류를 반환하여 최종 설치본 연결 방식 메뉴를 클릭하는 화면 검증은 수행하지 못했습니다. 설치본 App 검사와 일반 프로필 실행 상태는 위와 같이 확인했습니다.

## 2026-10-28 · 0.2.25 무료 휴대폰 로컬 연결

- 원본 Pro 서버와 독립적인 Kestrel HTTP 서버와 모바일 웹 화면, PC의 별도 `휴대폰 연결 · 무료` 메뉴를 구현했습니다. 기본 포트는 **17845**이며 선택한 사설 IPv4 인터페이스와 같은 서브넷만 받습니다. 자동 시작, 원본 계정 연결, 방화벽 변경, 포트포워딩은 하지 않습니다.
- 연결 코드 8자리/10분, 메모리의 256비트 토큰/8시간, 최대 4대, 페어링·명령 속도 제한, Host/Origin/인증/본문 크기 검사, 외부 리소스 없는 CSP, 세션별 결과 접근을 적용했습니다. 요청 ID를 디스크에 먼저 기록한 뒤 실행하고, 같은 ID 재전송은 기존 결과만 반환합니다. 동시에 한 요청만 실행하며 중지·로그아웃·연결 초기화·앱 종료 시 취소합니다. PC의 확인이 필요한 동작에 대한 기존 제한을 유지합니다.
- 개발본 무료 연결 HTTP 검사 **10개**, 실제 App/명령 실행 경로 **5개**, 기존 Pro 원격 App **5개**, 공유 학습 App **5개**가 통과했습니다. Pro 꺼짐/계정 없음 상태에서 실제 시각 명령 응답, PC 작업 중 거부, 확인 동작 차단, 실행 대기 중 취소 및 서버 종료를 검사했습니다. `artifacts/phone-checks.json`, `phone-app-checks.json`, `remote-app-checks.json`, `grow-app-checks.json`.
- Chrome의 **390×844** 화면에서 실제 코드 입력 → 연결 → `지금 몇 시야` → 실제 App 답변 표시를 확인했습니다. 별도 프로필의 루프백 시험으로 실물 휴대폰 검증은 아닙니다. 당시 `127.0.0.1` 시험 창을 사용자가 실제 접속 주소로 혼동했으므로 시험 창 제목·안내에 PC 내부 시험/휴대폰 접속 불가를 명시하고 닫았습니다. `artifacts/phone-browser-0225.png`.
- 실제 Wi-Fi IPv4 **`http://192.168.68.101:17845`**에 서버를 열고 같은 PC의 HTTP 클라이언트로 페이지·코드 인증·종료 후 포트 반환을 확인했습니다. 이 별도 검사 서버는 PC 동작을 실행하지 않습니다. **실제 휴대폰에서 공유기/방화벽을 통과한 접속은 미검증**입니다. `artifacts/phone-lan-probe-0225.json`.
- 패키지 소스 압축에 HTML/JS/CSS를 포함하고 모바일 화면에서 해당 버전 소스를 내려받도록 했습니다. ASP.NET Core 런타임과 라이선스 고지를 포함했습니다. 소스 159개 항목 중 이번 관련 17개 파일이 작업 트리와 바이트 단위로 일치하고 artifacts/bin/obj/models/가상환경이 없습니다. `artifacts/source-check-0225.json`.
- 최초 패키지 검사에서 OpenBLAS 메모리 부족, 최초 설치 전 기록 저장에서 메모리 부족이 발생했습니다. UI 검사 도구의 private memory가 약 88,611MB인 것을 확인하여 해당 도구만 종료·재시작했습니다. 패키지는 이미 완성된 동일 stage에서 BLAS 스레드 1개로 네이티브 가져오기·장치 열거를 통과한 후 MSI 생성을 마쳤습니다. `artifacts/ui-helper-memory-0225.txt`, `build-0225.log`, `build-0225-resume.log`. 사용자 앱이나 방화벽 설정은 이 조치로 변경하지 않았습니다.
- 이번 기능은 같은 네트워크에서 명령 입력/결과 확인을 제공합니다. 휴대폰 자체의 웹 녹음·음성 인식, 외부 인터넷 접속, 보고된 PC 마이크 무반응 문제, 원본 전체 기능과 실제 계정별 연동의 완료를 의미하지 않습니다. 폰 키보드 음성 입력을 사용할 수 있고 PC AI 제공자의 요금은 그대로 적용됩니다.
- **0.2.25 설치 완료**: MSI 종료 0, 설치본 0.2.25.0. Windows 기본 PATH/별도 프로필에서 기본 검사 **356개**, 실제 Pro/공유/무료 휴대폰 App 검사 각 **5개**, 합계 **371개** 통과. 설치 매니페스트의 1,764개 파일과 이전 0.2.24 백업의 1,625개 파일 해시가 모두 일치하며 사용자 JSON 4개·한국어 모델 5개를 보존했습니다. 일반 앱은 `자비스 · Windows`로 재실행했습니다. 무료 연결은 기본 꺼짐이며 사용자에게 켜기/실제 폰 접속 단계를 안내했습니다. 설치 후 네이티브 UI 검사 도구가 재연결되지 않아 최종 설치본 메뉴의 클릭/화면 재검증은 완료하지 못했습니다. 개발본 UI와 설치본 실제 App 경로 검증은 위와 같이 완료했습니다.
- MSI **99,558,066 bytes**, SHA-256 `A13627579E0D528390981B4A10AA69060CF2447D7D4339FB0907AE9EFB37C72F`. 복원 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.24-20261027-154135-418cb1.zip`. 근거: `artifacts/upgrade-result-0225.json`, `installed-and-backup-0225.json`, `installed-self-test-0225/`, `final-state-0225.json`. 이 검증 기록은 패키지 생성 후 작성했으며 포함 소스의 문서는 생성 당시 상태입니다.

## 2026-10-27 · 0.2.24 다 같이 키우기

- 원본 `Sources/MacButler/GrowTogether.swift`의 `/pack?lang=ko`, `/learn` 규약을 연결했습니다. 기본 꺼짐, 받기 전용, 제한된 기여/받기 선택과 설정 저장을 구현했습니다. 계정 키·쿠키·리디렉션을 사용하지 않으며 사용자 프로필에서는 공유를 켜지 않았습니다.
- 전용 모의 검사 11개와 실제 공개 목록 GET 1개 통과. 개인정보를 포함한 예제/임의 단어/등록 앱/검색/타이머 내용 차단, 허용 단일 명령만 기여, 원본 JSON 규약, 하루 주기·실패 재시도, 받기 전용 미전송, 잘못된/큰/중복 충돌 목록 제외, 제외 목록 재시작 보존, 취소 후 늦은 응답 미적용, 중복 동기화 직렬 처리, 손상·저장 실패 시 기록 보존을 검사했습니다. 기여 POST는 실제 서비스에 보내지 않았으며 서버 집계·채택은 미검증입니다. 근거: `artifacts/grow-checks.json`.
- 실제 App 처리 경로 5개 통과: 공유 시각 명령 실행과 ‘틀렸어’ 제외, 로컬 학습/기본 명령 우선, 끄기와 제외 시간 제한, 휴대폰 요청 기여 제외, 앱 설정 끄기 시 통신 취소·대기·늦은 응답 폐기. 별도 프로필과 모의 HTTP만 사용했습니다. 근거: `artifacts/grow-app-checks.json`.
- Release 빌드 경고·오류 0개, 개발본 기본 회귀 340개 통과. 별도 WPF 시험 창에서 ‘다 같이 키우기’ 항목, 기본 ‘끄기’, 설명 전체 줄바꿈과 꺼짐 상태를 실제 화면으로 확인했습니다. 시험 창은 종료했습니다. 설치본의 검사와 파일/백업 확인은 별도 설치 기록에 추가합니다.
- 원본과 다른 점: 보수적인 허용 어휘만 기여하며 기존 개인 학습 기록을 한꺼번에 올리지 않습니다. 사용자 마이크 무반응 해결, 자유 모드, Aside 원격 업무, 실제 회원/휴대폰 검증은 여전히 남아 있습니다.
- **0.2.24 설치 완료**: MSI 종료 0, 설치본 버전 0.2.24.0. Windows 기본 PATH와 별도 프로필에서 기본 검사 346개, 실제 원격 앱 처리 5개, 실제 공유 앱 처리 5개, 합계 **356개** 통과. 설치 파일 1,625개와 이전 0.2.23 백업 파일 1,625개의 해시가 모두 일치했고 사용자 JSON 4개·한국어 모델 5개를 보존했습니다. 일반 앱을 다시 실행하여 자비스 이름·Claude 준비·마이크 꺼짐을 화면으로 확인했습니다. 공유와 휴대폰 원격은 기존 꺼짐 상태를 유지합니다.
- MSI 90,136,832 bytes, SHA-256 `507FBABAA95B05D76338D0C224BA0EEE5D23A252650A88C38557D04E1E6B74DA`. 복원 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.23-20261027-140314-def21d.zip`. 패키지 소스 152개 항목에 artifacts/bin/obj/models/가상환경이 없고, 이번 기능 관련 소스 10개를 작업 트리와 바이트 단위로 대조했습니다. 근거: `artifacts/upgrade-result-0224.json`, `installed-and-backup-0224.json`, `installed-self-test-0224/`, `source-check-0224.json`, `grow-service-checks-0224.json`, `final-state-0224.json`. 설치 후 기록은 작업 트리에 추가했으며 패키지 내부 문서는 빌드 시점 기록입니다.

## 2026-10-27 · 0.2.23 휴대폰 원격 연결

- 기본 꺼짐인 원격 설정, 계정 창/설정 저장 이후 재연결, 앱 종료 시 취소·대기를 구현했습니다. 실제 서버와 같은 HTTPS 경로·Bearer 인증·명령 300자/답변 400자 규약을 모의 응답으로 검사했습니다. 실제 Pro 계정과 휴대폰으로 왕복한 검증은 아닙니다.
- 원격 전용 12개 검사 통과: 204/401/402 처리, 유니코드 경계·잘못된 명령, 연결 해제 중 늦은 응답, 재시작·중복 전달, 다른 계정의 동일 ID, 저장 실패/손상, 실행 중 busy/중지, 회신 재시도, 계정 취소·시간 제한, PC와 원격 답변 분리, 설정 가져오기 시 원격 상태 보존. 근거: `artifacts/remote-checks.json`.
- 실제 App → SubmitAsync → 명령/응답 경로 5개 검사 통과: 시각 답변, 확인 필요 동작 미실행, 원격 꺼짐·PC 작업 중 거절, PC 새 명령의 원격 취소, 휴대폰 취소의 실행 대기 중단. 별도 시험 프로필에서 고정된 무해한 명령만 사용했으며 계정이나 메시지 전송을 하지 않았습니다. 근거: `artifacts/remote-app-checks.json`.
- 원본 자유 모드와 Aside 원격 웹 작업은 미구현이며, 실제 회원 계정·휴대폰 왕복 및 마이크 무반응 해결 확인도 남아 있습니다.
- 개발본 Release 빌드 경고·오류 0개, 기본 회귀 329개 통과. 별도 WPF 시험 프로필에서 원격 설정의 기본 꺼짐과 안내 전체 줄바꿈을 화면으로 확인했습니다. 일반 프로필의 원격 연결은 켜지 않았습니다.
- **0.2.23 설치 완료**: MSI 종료 0, 설치본 파일 버전 0.2.23.0. Windows 기본 PATH·별도 프로필의 설치본 검사 335개와 실제 앱 원격 처리 경로 검사 5개가 통과했습니다. 새 설치 파일 1,625개 및 이전 0.2.22 백업 파일 1,625개가 모두 기록된 해시와 일치했습니다. 사용자 설정 JSON 4개와 한국어 모델 5개는 재실행 후에도 해시가 동일합니다. 일반 앱을 열어 Claude 준비·자비스 이름·마이크 꺼짐을 화면으로 확인했습니다.
- MSI 90,112,256 bytes, SHA-256 `8127C568DB414D97C8E8DBA08D19726C4625FD789CD1DBE91E0808390DACCEEE`. 검증한 복원 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.22-20261027-123201-e96ca4.zip`. 근거: `artifacts/upgrade-result-0223.json`, `installed-and-backup-0223.json`, `installed-self-test-0223/`, `final-state-0223.json`. 설치 후 검증 기록과 최신 MSI 링크는 작업 트리에 추가했으며 패키지 내부 문서는 빌드 시점 기록입니다.

## 2026-10-26 · Windows 브랜치 작업 저장

- `windows` 브랜치에 Windows 앱·음성 런타임·설치/검증 스크립트와 현재 문서를 저장합니다. 빌드 산출물, 로컬 시험 기록, 모델, 가상환경은 Git에서 제외합니다.
- 현재 소스 Release 빌드 경고·오류 0개, 별도 프로필의 기본 자체 검사 317개 통과. 장치·실계정·사용자 발화 검사는 이번 저장 과정에서 반복하지 않았습니다.
- Python 어휘 7개·음향 3개·worker 제어 1개·호출 이름 4개, 총 15개 검사 통과.
- 작성 중인 `ProRemote.cs`는 컴파일만 확인했습니다. 앱·설정 연결과 원격 전용 검증이 없으므로 휴대폰 원격 지원 완료를 뜻하지 않습니다. 마이크 무반응 해결 확인도 남아 있습니다.

## 2026-10-26 · 0.2.22 Pro 계정·음성

- 원본 `Sources/MacButler/ProAccount.swift`의 연결 시작/확인, 상태/사용량, 연결 해제, 음성 요청을 고정된 HTTPS 서버에 연결했습니다. 승인 페이지의 HTTPS·호스트·경로를 검증하며 HTTP 리디렉션을 따르지 않습니다. 키는 프로필별 Windows 자격 증명에 보관하며 설정 내보내기에 포함하지 않습니다.
- Pro 집중 검사 13개 통과: 승인 전 미저장, 취소 후 늦은 승인 차단, 만료/시간 제한, 상태 오류 후 오래된 회원 상태 제거, 401 키 폐기, 서버 해제 실패와 로컬 삭제 구분, 해제 후 도착한 상태/음성 무효화, 목소리/공용 문장 규약, 402/503 한 시간 대기, 기본 목소리 전환, 설정 교환, 실제 Windows 자격 증명의 시험 키 저장·삭제입니다.
- 실제 서버의 승인 코드 발급과 승인 전 취소를 별도 검증했습니다. 이 검사는 계정 로그인이나 회원 승인, 유료 음성 생성 검증이 아닙니다. 코드와 poll 값을 검증 보고서·로그에 보관하지 않았습니다.
- 별도 WPF 프로필에서 계정 창의 코드 표시, 취소 뒤 코드 제거 및 승인 버튼 비활성화, Pro 목소리 5종 목록, 하나 선택과 설정 저장을 확인했습니다. 미연결 목소리 시험은 SAPI/Windows 출력으로 전환되어 5.13초 재생을 완료했습니다(시험 음량 0). 사용자의 일반 프로필과 Pro 계정에는 변경을 가하지 않았습니다. 시험 앱은 종료했습니다.
- 추가 동시성 검사 전 기본 회귀 316개가 통과했습니다. 최종 설치본 회귀·파일 검증 결과는 아래 설치 기록에 별도로 추가합니다. 현재 사용자 마이크의 거의 무음 입력 문제는 이 기능의 구현과 구분하며 해결했다고 판단하지 않습니다.
- 근거: `artifacts/pro-checks.json`, `pro-ui-results.json`, `pro-ui-home/config.json`, `pro-ui-home/assistant.log`, `build-0222.log`. 실제 회원 계정 승인·유료 음성 생성, 휴대폰 원격과 공유 기능은 남아 있습니다.
- 설치 검증: MSI 종료 코드 0, 설치본 323개 검사 통과(별도 프로필·Windows 기본 PATH). 설치본 1,625개 파일과 이전 0.2.21 백업 1,625개 파일이 모두 기록된 해시와 일치했습니다. 기존 설정 4개·모델 5개 파일도 설치·검사·일반 앱 재실행 후 동일합니다. 일반 앱은 Claude·자비스·G435·Windows 음성 설정을 유지하고 자동 듣기로 복귀했습니다. 설치 및 최종 실행 근거는 `upgrade-result-0222.json`, `installed-and-backup-0222.json`, `installed-self-test-0222/checks.json`, `final-state-0222.json`입니다.
- 배포 파일: `dist/Sasarisa-Windows-0.2.22-x64.msi`, 90,075,392 bytes, SHA-256 `667C36026AD8CC4574A6CB5BD954E8FD3AE387B09F10FC13E02829C580A46951`. 복원 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.21-20261026-131150-d5a380.zip`. 설치 후 검증 기록과 안내 링크는 패키지 제작 후 작업 트리에 갱신했습니다.

## 0.2.21 · Aside Windows 연결 (2026-10-26)

- 공식 `https://releases.aside.com/install.ps1`와 x64 대상 설치 스크립트의 Authenticode가 Valid이고 발급 대상이 `AT YOUR SIDE INC`이며 타임스탬프가 있는지 확인했습니다. 대상 스크립트 SHA-256도 상위 서명 스크립트와 일치했습니다. CLI 1.26.1023.1115를 `%LOCALAPPDATA%\Aside\CLI\current\aside.exe`에 설치했고, 기존 계정의 연결을 확인했습니다. PATH는 변경하지 않고 앱의 기본 검색 경로로 연결했습니다. Sasarisa는 계정 자격 파일을 직접 읽거나 앱 설정에 복사하지 않습니다.
- 개발본 전체 310개 검사 통과. Aside 집중 검사 12개 통과: 설정·프로세스 인수·권한 모드·세션 격리·오류·취소·시간 초과·출력 구분, 실제 Windows 프로세스의 한국어 stdout/stderr·종료 코드·취소, 실제 Aside 계정의 두 차례 대화와 세션 중지 접수를 포함합니다. 네트워크 없는 검사는 10개, 실제 Aside 검사는 2개입니다.
- 별도 WPF 시험 프로필에서 설정의 연결 확인(`Aside CLI 1.26.1023.1115 · 계정 연결됨`), 설정 저장 후 `BrainProvider=aside`와 제공자별 설정 보존, 재실행 시 선택 유지, 실제 `윈도우 연동 확인` 응답, 기본 시각 명령의 로컬 실행, 중지 버튼 후 세션 중지 안내를 확인했습니다. 실행 중 앱 종료 시 세션 중지 요청을 보낸 뒤 종료했고 CLI 프로세스가 남지 않았습니다. 일반 Aside 브라우저는 종료하지 않습니다.
- 실제 공개 웹 읽기 시험에서 `https://example.com`을 열고 `Example Domain` 제목을 확인했습니다. 첫 시험으로 CLI의 작업 기록이 최종 답변에 섞이는 오류를 찾았고, ANSI 진행 상태 구간을 먼저 구분하도록 수정했습니다. 재시험의 WPF 화면에는 `새 탭에서 example.com을 열어 확인했고, 최상단 제목은 “Example Domain”입니다.`만 표시됐습니다. 구분할 수 없는 작업 기록·진행 상태만 있는 출력은 완료 답변으로 사용하지 않습니다.
- MSI 업데이트 종료 코드 0. 설치본 310개 검사 통과(Windows 시스템 디렉터리만 있는 PATH·별도 프로필). 설치 파일 1,625개 해시 일치, 이전 0.2.20 백업의 1,625개 해시도 일치했습니다. 설정 4개·모델 5개 파일은 업데이트·설치본 검사·일반 앱 재시작 후 해시가 그대로입니다. 정상 프로필은 Claude·자비스·G435 입력을 유지하며 듣는 중 화면으로 복귀했습니다. 시험 앱과 Aside CLI 프로세스는 남지 않았습니다.
- MSI: `dist/Sasarisa-Windows-0.2.21-x64.msi`, 90,009,856 bytes, SHA-256 `D9EF49386B04D74EF6483D6B303E99209685217ACEE2EA50A13200096B1B528C`. 업데이트 전 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.20-20261026-124816-6ca72a.zip`.
- 근거: `artifacts/aside-checks.json`, `checks-aside-full.json`, `aside-ui-home/assistant.log`, `aside-ui-results.json`, `aside-public-web-stdout.txt`, `build-0221-release.log`, `upgrade-result-0221.json`, `installed-and-backup-0221.json`, `installed-self-test-0221/checks.json`, `final-state-0221.json`. 설치 후 검증 기록은 배포 파일 제작 후 작업 트리에 추가했습니다.
- 실제 계정이 필요한 웹 업무·승인 화면·추가 Aside 버전과 다른 PC는 별도 검증이 남아 있습니다. G435에서 음성 신호가 거의 없는 문제와 원본 Pro·원격·공유 기능은 이 변경으로 해결됐다고 판단하지 않습니다.

검증 환경: Windows 11 Education 10.0.26300 x64, .NET SDK 10.0.201, Python 3.11 가상환경 및 패키지의 독립 Python 3.13.17 x64, sherpa-onnx 2.0.0. 2026-10-26 KST에 로컬 작업 트리에서 수행했습니다. 원본 macOS 앱은 실행하지 않았습니다.

## 통과

- **0.2.20 설치 완료**: 파일 버전 0.2.20.0, MSI 종료 0, 설치본 독립 환경 검사 **300/300**. 설치본과 이전 버전 백업 각각 1,625개 파일 해시 일치. 사용자 JSON 4개·모델 5개 해시를 앱 재실행·화면 확인 후까지 보존했습니다. 실제 설치된 설정 화면에서 ‘내 목소리로 이름 맞추기’ 버튼을 확인하고 대화 대기로 복귀했습니다. `artifacts/upgrade-result-0220.json`, `artifacts/installed-self-test-0220/`, `artifacts/final-state-0220.json`.
- 0.2.20 MSI 90,009,856 bytes, SHA-256 `57016224E31051BC071EEBAFCD41442236C2CC5C5D059A00E7067B43B906AC33`. 설치 전에 검증한 복원 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.19-20261026-120457-c647a1.zip`. 설치 후 기록은 작업 트리에 추가했으며 패키지 내부 문서는 빌드 시점 기록입니다. 현재 G435의 거의 무음인 입력 및 실제 사용자 발화 검증은 여전히 미해결입니다.

- 0.2.19 설치 단계: MSI 종료 0, 설치본 **300/300**, 설치본·이전 백업 각각 1,625개 파일 해시 일치, 사용자 JSON 4개·모델 5개 보존. 이후 0.2.20에서 기호가 있는 이름의 일치 범위를 보완했습니다. `artifacts/upgrade-result-0219.json`.
- 0.2.20 호출 이름 경계 검사: `C++`로 설정했을 때 `C 드라이브 열어`를 이름으로 간주하거나 앞글자를 제거하지 않고, `C++ 메모장 열어`에서만 이름을 제거합니다. C# 이름 보정 검사 6개, Python 이름 패턴 검사 4개 통과. 최종 설치본에서 전체 검사를 다시 실행합니다.

- 0.2.19 개발본 검사 **300/300**, Python 이름 패턴·음향·제어 루프 검사 **8/8**. 8회 이름 + 4회 명령 포함 발음, 무음 제외, 명령 꼬리 제거, 두 글자 발음의 반복·자모 유사도 조건, 흔한 말·명령 제외, 이름별 설정 보존·교환·명령 앞부분 제거를 확인했습니다. `artifacts/checks-name-full.json`, `runtime/test_names.py`.
- 별도 프로필의 실제 WPF 창에 **합성 인식 결과**를 넣어 12회 완료 → ‘서서 있어’ 보정만 저장 → 마이크 복귀 → 일반 설정 저장 후 보존 → 앱 재시작 → 기존 보정 삭제까지 확인했습니다. 시험에 포함된 유튜브·음량·시각·타이머 명령은 실행되지 않았으며 시험 글 12개만 기록했습니다. 화면에 표시한 0/12 → 12/12는 시험 글 재평가 수치로, 사용자 실제 음성 정확도 검증이 아닙니다. `artifacts/name-ui-home/`, `artifacts/name-completed-training.json`, `artifacts/name-saved-config.json`.
- 실제 G435·실제 sherpa 모델을 사용한 별도 프로필에서 입력 회차 시작, 15초 자동 종료, 읽을 수 있는 말 없음 → 회차 0 유지·적용 비활성화·재시도 안내를 확인했습니다. 닫기 후 원래 마이크 꺼짐 유지, 시험 입력 프로세스 종료, 설정 해시 불변, 결과 파일 미생성을 확인했습니다. 이 때도 신호가 거의 무음이었으며 사용자 발화 성공을 의미하지 않습니다. `artifacts/name-live-home/`, `artifacts/name-ui-results.json`.

- **0.2.18 설치 완료**: 파일 버전 0.2.18.0, MSI 종료 0, 설치본 독립 환경 검사 **294/294**. 설치본 1,625개와 이전 버전 백업 1,624개 파일 해시 일치. 사용자 JSON 4개·모델 5개 해시를 재실행 후까지 보존했습니다. 패키지 독립 Python 3.13.17에서도 음향·제어 루프 검사 4개 통과. 정상 프로필 앱을 다시 열고 마이크 켜짐·이름 대기 화면과 실행 프로세스를 확인했습니다. `artifacts/upgrade-result-0218.json`, `artifacts/installed-self-test-0218/`, `artifacts/final-state-0218.json`.
- 0.2.18 MSI 90,005,760 bytes, SHA-256 `A1FBA5A34C8F33BDDB58B8C00929DF0F465C1A4A5203A574074E32CE469F2444`. 설치 전에 검증한 복원 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.17-20261026-113747-10128e.zip`. 설치 후 검증 기록은 작업 트리에 추가했으며 패키지 내부 문서는 빌드 시점 기록입니다. 실제 사용자 발화 및 현재 무반응 문제 해결은 확인하지 못했습니다.

- 0.2.18 개발본 검사 **294/294**, Python 음향·실제 제어 루프 대체 장치 검사 **4/4**. 기본 한국어 SAPI 파일 생성·정리, 실제 WASAPI 출력의 이벤트 순서·취소·음량 측정, 자기 반향·주변음·단발 소음 제외, 장치별 보정, 요청 번호에 따른 캡처 응답과 최근 0.6초 입력 복원을 검사했습니다. 음향 측정의 0.25 PCM × 음량 0.4 = RMS 0.1도 확인했습니다. `artifacts/checks-barge-full.json`, `runtime/test_acoustics.py`, `runtime/test_worker_control.py`.
- 별도 프로필의 실제 WPF 앱과 실제 Windows 음성 출력(시험 음량 0)에 **합성 ASR 이벤트**를 연결해 자기 답변 무시 → ‘그만’ 중단 → 동일 발화의 최종 결과 처리 → 듣기 복귀를 확인했습니다. 끼어들기 끄기에서는 재생 완료 뒤 인식 재개, 마이크를 끈 상태에서는 재생 뒤에도 마이크 꺼짐·입력 프로세스 없음 유지. 사람의 실제 발화 또는 스피커 음향 검증으로 간주하지 않습니다. `artifacts/barge-ui-home/assistant.log`, `artifacts/barge-fixture-worker.py`.
- 20:34 현재 일반 설치본의 G435 입력은 raw RMS 약 0.0000153, 인식 처리 켜짐, 부분·최종 인식 모두 0입니다. 화면에서도 거의 무음 경고를 확인했습니다. 헤드셋 실제 음소거 상태 및 사용자가 선택된 마이크로 말했는지는 확인되지 않았습니다.

- **0.2.17 설치 완료**: 파일 버전 0.2.17.0, MSI 종료 0, 설치본 독립 환경 검사 **284/284**. 설치본 및 이전 버전 백업 각각 1,624개 파일 해시 일치, 사용자 JSON 4개·모델 5개 해시를 재실행 뒤까지 보존했습니다. 일반 앱을 마이크 켜짐·이름 대기 상태로 다시 열고 화면을 확인했습니다. 이번 수정은 유튜브 명령 분류이며 실제 사용자 음성 회복이나 브라우저 재생 품질 검증을 의미하지 않습니다. `artifacts/upgrade-result-0217.json`, `artifacts/installed-self-test-0217/`, `artifacts/final-state-0217.json`.
- 0.2.17 MSI 89,985,260 bytes, SHA-256 `A24D75784FB350693D98678BD925518FFE1E127EBE1D909BA8E37155931242D2`. 설치 전 검증한 복원 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.16-20261026-110537-e0f248.zip`. 설치 후 검증 기록은 작업 트리에 추가했으며 패키지 내부 문서는 빌드 시점 기록입니다.

- 0.2.17 개발본: 실제 로그의 `유튜브열어줘.` 및 10개 사이트 열기 변형이 모두 YouTube 홈 `open_url`로 분류됨을 확인했습니다. ‘열어줘’ 곡 검색·재생, 존댓말, 붙여 인식된 에스파/로파이, 검색어 없는 요청 경계까지 포함한 검사 **281/281**. 마이크를 켜지 않은 별도 시험 앱에서 `유튜브에서 검색해줘` → `유튜브에서 어떤 영상을 찾을까요?` 표시와 무동작을 확인했습니다. 이번 회차에서 실제 브라우저 홈 열기와 영상 재생을 다시 실행하지 않았습니다. `artifacts/checks-youtube-command-full.json`, `artifacts/youtube-command-ui-home/assistant.log`.

- **0.2.16 설치 완료**: MSI 종료 코드 0, 파일 버전 0.2.16.0, 설치본 독립 환경 검사 **281/281**. 설치 파일과 이전 0.2.15 백업의 파일 1,624개씩 해시 일치. 사용자 JSON 4개와 모델 5개의 해시를 설치·검사·일반 앱 재실행 후 재확인했습니다. 설치본 직접 호출에서 실제 거의 무음 안내와 입력 확인 표시, 버튼·본문이 잘리지 않는 화면을 확인했습니다. 정상 프로필 앱 PID 53660, worker 51616, 마이크 켜짐. 실제 사용자 발화 회복은 미확인입니다. `artifacts/upgrade-result-0216.json`, `artifacts/installed-self-test-0216/`, `artifacts/final-state-0216.json`.
- 0.2.16 MSI 89,968,876 bytes, SHA-256 `69B862D9CD8EC7544A0A826C9F5F618964FD7C22FF1A71E0A3CD917E52DCC050`. 검증한 복원 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.15-20261026-105318-6dcdab.zip`. 설치 이후 검증 기록은 작업 트리에 추가했으며 패키지 내부 문서는 빌드 시점 기록입니다.

- 0.2.16 개발본: 무음 안내·입력 중단 감지·일시 대기 해제 유예·재시도 제한 정책 5개를 포함한 검사 278/278. 실제 G435 입력의 거의 무음 안내, 시험 인식 프로세스 종료 후 자동 재연결, 다시 연결 중 듣기 중지 후 1분 이상 중지 유지 확인. 실제 측정은 0.0000153 이하 RMS와 인식 0개이며 검사 중 사용자 발화 여부는 미확인입니다. 음소거 또는 하드웨어 고장으로 단정하지 않습니다. `artifacts/checks-health-full.json`, `artifacts/health-ui-home/assistant.log`, `artifacts/live-recognition-probe-1944.json`.

- **0.2.15 설치 완료**: MSI 종료 코드 0, 실행 파일 `0.2.15.0`, 설치본 독립 환경 검사 **276/276**, 실제 마이크 세션 검사 **7/7**. 설치·0.2.14 복원 백업 각각 1,624개 파일 해시 일치, 설정·기억·앱·타이머 JSON 4개와 모델 5개 보존. 설치된 앱에서 실제 외부 캡처 시작→자동 대기→캡처 종료→이름 대기를 화면과 worker의 False→True 확인 신호로 검증했습니다. 최종 상태는 마이크 켜짐·이름 대기·Claude 준비이며 사용자 발화 성공은 아직 확인되지 않았습니다. `artifacts/upgrade-result-0215.json`, `artifacts/installed-self-test-0215/`, `artifacts/final-state-0215.json`.
- 0.2.15 MSI **89,989,356 bytes**, SHA-256 `7B40EE90F602B9E2ED6E755752420CEC85B7927D4D969E571C12666F39F1A2FF`. 검증한 업데이트 전 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.14-20261026-102958-ffffaf.zip`. 설치 후 검증 내용은 작업 트리 문서에 기록했으며 패키지 내부 문서는 빌드 시점 기록입니다.

- 0.2.15 개발본: 통합 **276/276**, 마이크 대기 검사 **7/7**(실제 WASAPI 세션 신규 생성·종료 포함), 한국어 실제 모델 검사 **7/7**. 화면에서 외부 캡처 중 자동 대기→수동 호출→중지/시간 만료 후 대기→외부 캡처 종료 후 복귀, 직접 듣기 중지 후 자동으로 켜지지 않음, 옵션 해제·저장·재실행을 확인했습니다. `artifacts/checks-microphone-full.json`, `artifacts/microphone-checks.json`, `artifacts/microphone-progress-20261026.md`.
- Windows 가상환경의 Python 실행기가 별도 자식 프로세스에서 마이크를 여는 경우를 수정했습니다. 음성 worker가 준비 신호에 실제 PID를 전달하고 앱은 현재 세대의 worker PID만 제외합니다. 별도 프로필의 중복 실행/종료가 기본 프로필을 건드리지 않도록 IPC 이름을 분리했으며 실제 두 프로필 실행·중복 방지·시험 프로필만 종료를 검증했습니다. 기본 설치의 IPC 이름은 유지합니다.
- 사용자 보고 음성 무반응: 설치본 0.2.14의 마이크를 재시작하고 같은 G435 입력을 인식기에 직접 연결했습니다. 12초 검사에서 인식기는 오류 없이 실행됐으나 거의 무음과 인식 결과 0개가 관측됐습니다. 그 시간에 사용자가 발화했는지는 확인되지 않았으며 하드웨어 결함·음소거 또는 소프트웨어 오류 중 어느 원인인지 확정하지 않았습니다. 이 문제의 해결과 사용자 발화 정확도는 **미검증**입니다.

- 이전 **0.2.14 설치 완료**, MSI 종료 코드 0, 실행 파일 `0.2.14.0`. 설치 파일과 0.2.13 복원 백업 각각 **1,624개** 해시 일치. 설정 3개·한국어 모델 5개 보존, 개발 도구 없이 설치본 검사 **268/268**. 바로가기 실행 후 카카오톡 도움말 전체 줄바꿈을 화면에서 확인하고 대화 대기·마이크 꺼짐·Claude 준비 상태로 복귀했습니다. `artifacts/upgrade-result-0214.json`, `artifacts/installed-and-backup-0214.json`, `artifacts/installed-self-test-0214/checks.json`, `artifacts/final-state-0214.json`.
- 0.2.14 MSI **89,960,684 bytes**, SHA-256 `06C113CC33ACC613ED65ED2172C8A26BBA03E5ED96D8EC4FC003A7EC7B782450`. 백업 `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.13-20261026-094707-1f96e5.zip`. 패키지 문서는 빌드 시점 기록이며 설치 후 검증은 작업 트리 문서에 추가했습니다. 카카오톡 실제 전송은 실행하지 않았고 개발본 초안/취소 검사를 설치 후 반복하지 않았습니다.

- 0.2.12→0.2.13 MSI 설치 완료, 종료 코드 0. 설치·백업 파일 각각 1,624개 해시 일치, 설정 3개·모델 5개 보존. 개발 도구를 PATH에서 제외한 설치본 검사 **268/268**. 바로가기 실행 및 사용 방법 화면 반영 확인. 도움말의 긴 설명이 잘리는 것을 발견하여 0.2.14에서 줄바꿈을 추가했습니다. `artifacts/upgrade-result-0213.json`, `artifacts/installed-and-backup-0213.json`.

- 0.2.13 개발본: 카카오톡 전용 흐름 포함 통합 검사 **268/268**, 빌드 경고·오류 0. 실제 한국어 KakaoTalk 26.9.0.5337에서 열린 본인 대화방 조회, 미전송 초안·이모지·줄바꿈, 기존 초안 보존과 확인창 취소를 검증했습니다. 실제 Claude MCP의 `kakao_chats` 조회 후 `kakao_draft` 확인창도 취소하고 무입력을 확인했습니다. 전송은 실제로 수행하지 않았습니다. `artifacts/checks-kakao-full.json`, `artifacts/kakao-chat-ui-20261026.md`.

- 0.2.11→**0.2.12 MSI 업데이트 완료**, 설치 종료 코드 0, 실행 파일 `0.2.12.0`. 업데이트 전 0.2.11 백업과 설치 후 0.2.12 파일 각각 **1,624개** 해시 일치. 설정 3개·한국어 모델 5개 해시 보존. 개발 도구를 PATH에서 뺀 설치본 검사 **258/258**. `artifacts/upgrade-result-0212.json`, `artifacts/installed-and-backup-0212.json`, `artifacts/installed-self-test-0212/checks.json`.
- 0.2.12 MSI: **89,899,244 bytes**, SHA-256 `C1995CC414EAC47013F0DF672B2B736D2127E179D60488970A6484940DAFADA6`. 이전 버전 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.11-20261026-091032-0c5448.zip`. 패키지 문서는 빌드 시점 기록이며 설치 후 검증은 작업 트리 기록에 추가했습니다.
- 0.2.12 바로가기 실행과 실제 사용 방법 화면의 화면 끄기 안내를 확인했습니다. 설치본이 Windows 화면 켜짐 알림을 받았고, 기본 사용자 설정·모델 해시를 재확인한 뒤 마이크 꺼짐·Claude 준비·대화 대기 상태로 두었습니다. `artifacts/final-state-0212.json`. 실제 화면 끄기·복귀 시험은 개발본에서 수행했으며 설치 후 반복하지 않았습니다.
- 0.2.12 개발본: 통합 검사 **258/258**(화면 전원 8개 포함), 빌드 경고·오류 0. `artifacts/checks-display-full.json`, `artifacts/display-power-checks.json`. 실제 로컬 명령으로 Windows 화면 꺼짐 알림 수신→키 입력→켜짐 알림 수신을 확인했습니다. 실행 직전 중지 및 실제 Claude MCP 확인창 취소 시 추가 꺼짐이 발생하지 않았습니다. `artifacts/display-power-ui-20261026.md`.
- 화면 전원은 [Windows SC_MONITORPOWER](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand)와 [사용자 세션 전원 알림](https://learn.microsoft.com/en-us/windows/win32/power/power-setting-guids)을 사용합니다. 현재 PC의 Windows 알림을 검증했으며, 모든 물리 모니터의 전기적 상태나 원격 세션·추가 하드웨어 구성까지 검증한 것은 아닙니다.
- 0.2.10→**0.2.11 MSI 업데이트 완료**, 설치 종료 코드 0, 실행 파일 `0.2.11.0`. 업데이트 전 0.2.10 백업을 만든 뒤 **1,624개** 파일 해시를 검증했습니다. 설치 후 새 파일도 **1,624개** 해시 일치, 설정 3개·한국어 모델 5개 해시 보존. 개발 도구를 PATH에서 뺀 설치본 검사 **250/250**. `artifacts/upgrade-result-0211.json`, `artifacts/installed-and-backup-0211.json`, `artifacts/installed-self-test-0211/checks.json`.
- 0.2.11 MSI: **89,895,148 bytes**, SHA-256 `091DD255A6ED3D7934DE62D83A092591FF5DB45D910097A17B67719A35BD672D`. 검증한 이전 버전 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.10-20261026-085044-579b00.zip`. 설치 후의 기록은 작업 트리 문서에 추가하며, 패키지 문서는 빌드 시점 기록입니다.
- 0.2.11 설치본에서도 새 세션 시작 화면→기존 세션 검색→전환→완료 응답을 재확인했습니다. 대상 Claude 화면의 제목과 빈 입력칸을 확인한 뒤 시험 앱을 종료했고, 남은 시험 프로세스가 없는 상태에서 바탕화면 바로가기를 실행했습니다. `artifacts/claude-home-ui-20261026.md`.
- 0.2.11 개발본: 세션 집중 검사 **9/9**, 빌드 경고·오류 0. Code의 새 세션 화면(`/epitaxy`)에서 사이드바를 맨 위로 올린 상태로 아래쪽의 `Git Mac 소스 확인` 세션을 검색·전환하고, 본문 미전송 및 완료 응답을 확인했습니다. 시작 화면 URL은 목록 조회에만 허용하며 전송 대상 검사에는 허용하지 않습니다. `artifacts/claude-session-checks.json`, `artifacts/claude-home-ui-20261026.md`.
- 0.2.9→**0.2.10 MSI 업데이트 완료**, 실행 파일 `0.2.10.0`. 개발 도구를 PATH에서 뺀 설치본 검사 **249/249**. 설치 파일과 이전 앱 백업 각각 **1,624개** 해시 일치. MSI **89,878,764 bytes**, SHA-256 `436F11C19225FF11A404387FD21A4D7E2822467C255360666E8CD2B4A13FC520`. `artifacts/installed-and-backup-0210.json`, `artifacts/installed-self-test-0210/checks.json`. 업그레이드 래퍼는 파일 검증 후 종료 코드 1이어서 설치본 검사를 별도로 실행해 통과를 확인했습니다.
- 0.2.10 설치본 실제 UI: 공백 호출 이름으로 마이크를 켜고 끄기, 기존 `Windows 설치 구성` 로컬 세션 조회·전환을 확인했습니다. 메시지는 보내지 않았으며 검증 뒤 `Git Mac 소스 확인`으로 돌아왔습니다. 시험 앱은 정상 종료했습니다.
- 0.2.10 개발본: Claude/Codex 포함 통합 **264/264**, 당시 세션 집중 검사 **8/8**, 실제 한국어 모델 Python **7/7**, 빌드 경고·오류 0. `artifacts/checks-claude-sessions-full.json`. 최신 집중 검사 파일은 0.2.11의 9개 검사로 갱신했습니다. 후속 네이티브 UI 호환 수정은 실제 앱에서 재검증했습니다.
- Claude Windows 3.6556.2.0: 로컬→클라우드 세션 전환, 기존 로컬 세션에 승인한 연결 시험 한 번 전달 및 실제 `연결 확인` 응답. 확인창 취소 시 빈 초안 보존, 승인 중 시험 초안 변경 시 미전송·초안 보존. 초기 전환 실패, 로컬 주소 미지원, 화면 교체 중 UIA 오류를 수정한 과정은 `artifacts/claude-session-ui-20261026.md`에 기록했습니다. 사용자 기존 프로젝트의 코드 작업을 대신 수행한 검증은 아닙니다.
- 공백 호출 이름 `세션 시험`으로 실제 마이크 시작→이름 대기→듣기 중지 확인. 키워드 표시 이름의 공백을 토큰으로 오인하던 오류를 수정하고 모델에 없는 토큰은 네이티브 감지기 호출 전에 검사합니다. [sherpa 키워드 파일 규약](https://k2-fsa.github.io/sherpa/onnx/kws/index.html)을 참고했습니다. 발화 인식 정확도 튜닝은 별도입니다.

- 0.2.8→**0.2.9 MSI 업데이트 완료**, 설치 종료 코드 0, 실행 파일 `0.2.9.0`. 개발 도구를 PATH에서 뺀 설치본 검사 **241/241**. 새 설치와 이전 앱 백업 각각 **1,624개** 파일의 해시 일치, 설정 3개·한국어 모델 5개 해시 보존. `artifacts/installed-and-backup-029.json`, `artifacts/installed-self-test-029/checks.json`, `artifacts/upgrade-and-verify-029.log`.
- 0.2.9 설치본도 분리한 시험 설정에서 로컬 HTTP 서버에 연결해 입력칸 조회→두 번째 칸만 수정→별도 이미지 전송 승인→완료 응답을 확인했습니다. 서버 요청 12–15, 이미지 **14,903 bytes**. 실제 제공자 모델은 사용하지 않았습니다. 시험 앱·창·서버를 종료하고 바탕화면 바로가기로 사용자 Claude 설정의 앱을 다시 실행했으며, 마이크 꺼짐·대기 상태와 설정 해시 보존을 확인했습니다. `artifacts/api-tools-ui-20261026.md`, `artifacts/final-state-029.json`.
- 0.2.9 MSI: **89,809,132 bytes**, SHA-256 `F9AF5A9D82535D7F499B98F352BD1959865D31CA05EA497714E6A930AF9E1A69`. 이전 버전 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.8-20261025-220338-4b562a.zip`.

- 0.2.9 재검사: 개발본 통합 **257/257**, Python **5/5**, 빌드 경고 0/오류 0. `artifacts/checks-api-tools.json`, `artifacts/verify-api-tools-recheck.log`. 수정한 검사 코드까지 포함해 MSI를 다시 빌드했습니다. 설치 후 추가한 증거는 작업 트리 문서에 기록되므로 패키지 문서는 빌드 시점의 기록입니다.

- 첫 0.2.9 통합 검사에서는 **256/257**이 통과했습니다. 실패한 기존 OpenAI 계획 반복 검사는 `messages`와 Chat Completions 응답을 전제로 했으므로, 제품이 사용하는 Responses의 `input`과 `function_call` 응답으로 시험 서버를 수정했습니다. 실제 관측 결과가 다음 요청에 포함되는 기존 검증 조건은 유지했습니다. 최초 결과는 `artifacts/checks-api-tools-first-full.json`에 보존했습니다.

- 0.2.9 외부 API 직접 도구 호출 집중 검사 **15/15**. 8개 제공자의 요청·응답 형식, 호출 ID·인수·반복 한도, 이미지 및 reasoning/signature 보존, 승인 거절·창 변경·취소·실패 시 후속 실행 차단을 확인했습니다. 첫 실행 7/15의 원인은 JSON의 `+` 이스케이프를 그대로 비교한 검사 코드였으며, 디코딩된 이미지 바이트 문자열의 일치 검사로 수정했습니다. `artifacts/api-tool-checks.json`.
- 0.2.9 소스 앱과 실제 로컬 HTTP 시험 서버, WPF 시험 창을 연결해 두 번째 입력칸만 `API 연결 확인`으로 변경하고 첫 칸을 보존했습니다. 별도 이미지 전송 승인 후 PNG **14,903 bytes**가 서버에 도착했습니다. 재시험에서 입력 확인창을 취소하자 원문을 보존하고 후속 요청·이미지 전송이 멈췄습니다. 초기 대상 미확정/대상 변경 차단/취소 시도는 성공으로 계산하지 않았습니다. `artifacts/api-tools-ui-20261026.md`, `artifacts/api-tools-ui-events.json`. 응답은 시험 스크립트이며 실제 모델 판단·유료 API·Ollama 추론 증거는 아닙니다.
- 규약은 [OpenAI 함수 호출](https://developers.openai.com/api/docs/guides/function-calling), [OpenAI reasoning](https://developers.openai.com/api/docs/guides/reasoning), [Anthropic 도구 결과](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls), [Ollama 호환 API](https://docs.ollama.com/api/openai-compatibility)를 확인했습니다. OpenAI는 Responses와 `store:false`, Anthropic은 이미지 포함 `tool_result`, 호환 서버는 도구 결과 뒤 이미지 사용자 메시지를 사용합니다.

- 바탕화면 바로가기로 0.2.8 설치본 실행 후 실제 Claude MCP에서 Ctrl+Shift+N 확인·전달. 시험 창 횟수 1→2, 보조 키 `None`, 두 입력칸 원문 보존을 화면으로 확인. `artifacts/keyboard-ui-20261026.md`.
- 0.2.7→**0.2.8 MSI 업데이트 완료**, 설치 종료 코드 0, 실행 파일 `0.2.8.0`. 개발 도구를 PATH에서 뺀 설치본 검사 **225/225**. 새 설치와 이전 앱 백업 각각 **1,624개** 파일의 해시 일치, 설정 3개·한국어 모델 5개 해시 보존. `artifacts/installed-and-backup-028.json`, `artifacts/installed-self-test-028/checks.json`, `artifacts/upgrade-and-verify-028.log`.
- 0.2.8 MSI: **89,772,268 bytes**, SHA-256 `C7BD62E7EA8E5220B07E2419C5F1281D2AF1934A50CEC72DE822DBB115E39F22`. 이전 버전 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.7-20261025-214302-6d1899.zip`.
- 0.2.8 개발본 통합 **241/241**, Python **5/5**, 빌드 경고 0/오류 0. `artifacts/checks-keyboard.json`, `artifacts/verify-keyboard.log`. MSI 빌드와 병행된 검사 결과·설치 후 증거는 작업 트리의 이 문서에 추가했으며, 패키지 문서는 빌드 시점의 기록임.
- 0.2.8 키보드 확장 집중 검사 **10/10**: 단일 조합 검증, macOS 별칭 변환, 기호/AltGr 배열, 확장 키, 보조 키 순서/해제, 이미 눌린 키와 취소, 모든 부분 전달 지점의 해제, 확인 거절, 실제 Claude/Codex의 Home→Shift+End→Backspace 순차 도구 호출. `artifacts/keyboard-checks.json`.
- 실제 WPF/Claude MCP: 두 번째 칸의 둘째 줄만 삭제, Ctrl+Z로 복원, `plus`로 선택 영역 교체, Ctrl+Shift+N 정확히 1회 전달 및 보조 키 `None` 확인. 확인 대기 중 같은 창의 다른 입력칸을 선택하면 무입력으로 중단. 로컬 `엔터` 확인창에서 중지 단축키 취소·원문 보존. `artifacts/keyboard-ui-20261026.md`. 같은 입력칸의 커서 이동은 이 키 도구의 초점 검사 범위에 포함하지 않음.
- 구현 참고: Microsoft [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)의 이미 눌린 키/전달 개수/UIPI 동작, [KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput)의 확장·키 해제 플래그, [VkKeyScanEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-vkkeyscanexw)의 대상 배열·보조 키 매핑. 실제 다른 배열·관리자 앱·브라우저·전역 Windows 조합은 추가 현장 검사가 필요함.

- 0.2.6→**0.2.7 MSI 업데이트 완료**, 종료 코드 0. 개발 도구를 PATH에서 뺀 설치본 검사 **217/217**. 설치 파일 **1,624개**, 0.2.6 백업 앱 파일 **1,624개**의 해시 일치. 설정 3개·한국어 모델 5개 해시 보존. `artifacts/installed-and-backup-027.json`, `artifacts/installed-self-test-027/checks.json`, `artifacts/upgrade-and-verify-027.log`.
- 바탕화면 바로가기로 0.2.7 실행. 실제 Claude가 B에서 최소화된 A를 조회·복원·선택하고 `front` 확인 후 A 두 번째 칸에 ` 설치전환 확인`을 추가했다. 결과 `둘째 칸 원문 설치전환 확인`을 화면에서 확인했고 A 첫 칸·B 두 칸을 보존했다. `artifacts/window-targets-ui-20261026.md`.
- 0.2.7 MSI: **89,747,692 bytes**, SHA-256 `B8CF3DFA8C0D31173A196883C75FF240767DD72234F9145405D4ED37E05E8DD1`. 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.6-20261025-212048-1ae315.zip`. 패키지에는 빌드 시점의 소스/검증 문서를 포함하며 설치 후 증거는 이 작업 트리에 추가함.

- 0.2.7 창 전환 개발본: 통합 **231/231**, Python **5/5**, 빌드 경고 0/오류 0. 집중 **8/8**에는 실제 Claude/Codex의 기존 창 조회→선택→`front`, 같은 앱 창 구별, ID 만료·페이지·요청 범위, 프로세스/시작 시각/제목 변경, 잘못된 선택 결과, 사용자 창 변경과 취소를 포함함. `artifacts/checks-window-targets.json`, `artifacts/window-target-checks.json`, `artifacts/python-window-targets.log`.
- 실제 WPF/Claude MCP: A에서 최소화된 B를 조회·복원·선택→현재 창 확인→B 두 번째 칸에만 글 추가, A 원문 보존. 이후 A 선택/입력 확인 중 B를 전면 선택하면 입력 없이 중단. `artifacts/window-targets-ui-20261026.md`. 브라우저 주소 읽기·열기 후 탭 연결과 Store 앱·다중 데스크톱 실측은 미검증.
- 첫 통합 실행 **230/231**: 기존 이미지 모의 환경이 새 `front` 조회를 지원하지 않아 Claude 검사에서 중단. 현재 창 관찰 응답을 모의 환경에 추가하고 실제 이미지 수신·정확한 좌표 클릭·최종 완료 조건은 유지했다. 수정 후 위 231/231 통과. 첫 결과는 `artifacts/checks-window-targets-first.json`에 보존.

- 0.2.5→**0.2.6 MSI 업데이트 완료**, 설치 종료 코드 0, 실행 파일 `0.2.6.0`. 개발 도구를 PATH에서 뺀 설치본 검사 **211/211**. 설치 파일 **1,624개**와 0.2.5 백업 파일 **1,624개** 해시 일치, 설정 3개·한국어 모델 5개 해시 보존. `artifacts/installed-and-backup-026.json`, `artifacts/installed-self-test-026/checks.json`, `artifacts/upgrade-and-verify-026.log`.
- 바탕화면 바로가기로 0.2.6 설치본 실행. 실제 Claude 조회→확인→두 번째 칸에 ` 설치확인` 추가 결과가 `새 한글 🎵 칸 원문 설치확인`인 것을 화면에서 확인했다. 첫 칸·읽기 전용 글·누르지 않은 버튼 상태 보존, 앱의 최종 내용·커서 검증 성공. `artifacts/insertion-ui-20261026.md`.
- 0.2.6 MSI: **89,731,308 bytes**, SHA-256 `EA9F7282D243940B6F0C4D7CAD99F69ACC95D69D5B4AB4CC1FE11201AE9392B0`. 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.5-20261025-210339-0f6267.zip`. 패키지에는 빌드 당시 소스와 검증 문서를 포함하며, 설치 후 검증 기록은 이 작업 트리 문서에 추가함.

- 0.2.6 입력칸 삽입 개발본: 통합 **223/223**, Python **5/5**, 빌드 경고 0/오류 0. 집중 **9/9**에는 실제 Claude/Codex가 두 번째 입력칸을 선택해 전체 교체 대신 `insert_field`를 호출하는 검사, 한글·이모지·줄바꿈·청크 경계, 커서·글 변경, 취소·부분 실패·크기 제한·확인 거절이 포함됨. `artifacts/checks-insertion.json`, `artifacts/insertion-checks.json`, `artifacts/python-insertion.log`.
- 실제 WPF/Claude MCP: `둘째 칸 원문`의 커서에 `한글 🎵 `를 추가하여 `둘째 한글 🎵 칸 원문` 확인 → 선택한 `둘째`만 `새`로 교체하여 `새 한글 🎵 칸 원문` 확인. 첫 칸·읽기 전용 글 보존. 확인 대기 중 커서를 Home에서 End로 옮긴 경우 입력 없이 중단. `artifacts/insertion-ui-20261026.md`. UIA TextPattern의 단일 선택 영역을 제공하는 앱에 한정하며 다양한 문서 앱·브라우저 호환성은 미검증.

- 0.2.4→**0.2.5 MSI 업데이트 완료**, 설치 종료 코드 0. 개발 도구를 PATH에서 뺀 설치본 검사 **204/204**, 설치 파일 **1,624개**와 0.2.4 백업 파일 **1,624개** 해시 일치. 설정 3개와 한국어 모델 5개 해시 보존. 실행 파일 버전 `0.2.5.0`. `artifacts/installed-and-backup-025.json`, `artifacts/installed-self-test-025/checks.json`, `artifacts/upgrade-and-verify-025.log`.
- 바탕화면 바로가기로 0.2.5를 실행해 Claude의 대상 창 캡처→초록 원 중심 미리보기→확인 후 클릭을 수행했고, 실제 시험 창의 결과가 `초록 원을 눌렀어요`로 바뀜. `artifacts/visual-ui-20261026.md`.
- 0.2.5 MSI: **89,690,348 bytes**, SHA-256 `F16FE6B8A2BAF0ABD4D4393365942C3FAA57291C23CBC55B7100C600F05D8DBB`. 이전 버전 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.4-20261025-205020-832369.zip`. 배포물에는 빌드 시점의 검증 문서가 포함되며 설치 후 검증 기록은 이 작업 트리에 추가함.

- 0.2.5 이미지 도구와 확인 안내 수정 후 통합 **214/214**, Python **5/5**, 빌드 경고 0/오류 0. 실제 Claude/Codex 이미지 읽기→정확한 색 도형 클릭 호출도 통과. `artifacts/checks-visual.json`, `artifacts/verify-visual.log`. 확인창 전면 표시와 클릭 취소 시 무변경도 새 소스 빌드의 실제 WPF 창에서 확인함.

- 0.2.5 개발본 이미지 도구 집중 검사 **9/9**: 실제 Claude/Codex의 서로 다른 그림 위치 판단, 64 KB를 넘는 이미지의 두 MCP 규약 전송/바이트 일치, 텍스트 응답에 이미지 바이트 미포함, PNG/JPEG 크기 제한, 음수 모니터 좌표·배율 변환, 이미지 ID 만료·재사용 차단, 창/픽셀 변경과 취소 검사. `artifacts/visual-checks.json`.
- 첫 통합 검사에서는 **213/214** 통과: Codex가 이미지 위치는 정확히 찾았지만 앱 확인창을 여는 click 도구 대신 채팅으로 재확인해 한 항목이 실패함. 도구 호출 자체가 앱의 승인 절차를 시작한다는 안내를 명확히 수정함. 실패 기록은 `artifacts/checks-visual-first.json`, `artifacts/verify-visual-first.log`에 보존함.
- 실제 WPF 그림을 Claude가 이미지로 읽고 `(513,240)`의 파란 원을 클릭해 결과 문구를 확인함. 확인 대기 중 원을 초록색으로 바꾸자 픽셀 변경으로 클릭 차단, 이전 결과 보존. 후속 이미지 확인창에서 중지 단축키로 닫힘·대기 상태 복귀. `artifacts/visual-ui-20261026.md`. 실제 브라우저/GPU 앱 호환성이나 음성 인식 정확도를 입증하는 검사는 아님.
- 캡처는 바탕화면 복사 대신 대상 창의 PrintWindow를 별도 프로세스에서 실행하며 5초 제한·취소 시 종료. [Microsoft 문서](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)의 동기 호출로 인한 정지 가능성에 대응함. 실제 응답 없는 대상 창의 시간 초과 검사는 아직 수행하지 않음.

- 0.2.3→**0.2.4 MSI 업데이트 완료**, 설치 종료 코드 0. 설치본 PATH 격리 검사 **197/197**, 배포 파일 **1,624개**와 이전 0.2.3 백업 파일 **1,617개** 해시 일치. 설정 3개·한국어 모델 5개 보존. 바탕화면 바로가기 대상과 설치 파일 버전 `0.2.4.0`, 실제 메인 화면 실행 확인. `artifacts/installed-and-backup-024.json`, `artifacts/installed-self-test-024/checks.json`, `artifacts/upgrade-and-verify-024.log`.

- 0.2.4 MCP 연결 추가 후 통합 **205/205**, Python **5/5**, 빌드 경고 0/오류 0. `artifacts/checks-mcp.json`, `artifacts/verify-mcp.log`, `artifacts/verify-mcp.stderr.log`. 집중 검사 **10/10**: 실제 stdio 별도 프로세스의 세 프로토콜 버전 연결·목록·호출·오류·취소·EOF, 같은 사용자 파이프 인증/동시 호출 직렬화, 거절·창 변경·호출 한도, 실제 Claude/Codex의 모의 타이머 ID 기반 선택 취소.
- MCP 실제 화면 검사: Claude가 WPF 시험 창의 같은 이름을 가진 두 입력칸 중 두 번째만 조회·확인 후 한글로 교체, 첫 번째/읽기 전용 내용 보존. 다음 요청의 확인창 대기 중 Ctrl+Alt+Backspace로 중지하여 확인창 닫힘·원문 보존·대기 상태 복귀 확인. `artifacts/mcp-ui-20261026.md`. 음성 인식 정확도나 실제 브라우저 조작 검사는 아님.

- 개발본 화면 도구 추가 후 통합 **195/195**, Python **5/5**, 빌드 경고 0/오류 0. `artifacts/checks-desktop-controls.json`, `artifacts/verify-desktop-controls.log`, `artifacts/verify-desktop-controls.stderr.log`. 집중 검사 9개에는 관측 ID/중복 이름/부모 영역, 제공자 JSON의 입력칸 ID 보존, 확인 거절, 페이지·만료·실패 후 ID 폐기, 입력 내용 변경 감지, Unicode와 관찰 JSON 길이, 실제 Claude의 입력칸 선택이 포함됨.
- 실제 Windows WPF 폼에서 두 번째 입력칸만 한글로 교체, 중복 이름의 두 번째 버튼만 실행, 확인 대기 중 바뀐 글 덮어쓰기 차단, 거절 시 무변경 확인. 실제 메모장의 본문 입력칸을 조회·선택해 한글로 교체하고 최종 문자열 일치를 확인했으며 시험 후 원문 29자를 복원함. `artifacts/desktop-controls-ui-20261026.md`. 새 화면 도구는 0.2.3 설치본 미포함이며 MCP stdio·이미지 결과 완료를 의미하지 않음. 남은 도구별 항목은 [TOOLS_PARITY.md](TOOLS_PARITY.md).

- 0.2.2→0.2.3 실제 MSI 업데이트 종료 코드 **0**. 설치 파일 1,617개와 이전 버전 백업 1,610개의 크기·SHA-256 모두 일치. config/apps/timers 및 음성 모델 5개를 합친 8개 파일의 해시 불변. 개발 도구를 PATH에서 뺀 설치본 자체 검사 **181/181**. 바탕화면 바로가기가 0.2.3.0 실행 파일을 열며 새 음성 제공자 목록과 기존 Windows 기본 음성·마이크 꺼짐 상태를 확인함. `artifacts/installed-and-backup-023.json`, `artifacts/installed-self-test-023/checks.json`, `artifacts/upgrade-and-verify-023.log`, `artifacts/final-state-023.json`.
- 0.2.3 MSI: 89,010,260 bytes, SHA-256 `9CAC2ABE194DFA891A7CAED1377DC45DBDFDBA0ABB2F3E37D9420D0BA3F0C2E7`. NAudio MIT 라이선스를 설치 폴더와 대응 소스 ZIP에 포함했고 배포 고지에도 기록함. `artifacts/build-023.log`, `artifacts/refresh-package-023.log`. 기존 버전 백업: `%LOCALAPPDATA%\VoiceAssistantWindows\install-backups\Sasarisa-0.2.2-20261025-194719-339748.zip`.

- 외부 음성 출력 추가 후 통합 **186/186**, Python **5/5**, 빌드 경고 0/오류 0. `artifacts/checks-external-voice.json`, `artifacts/verify-external-voice.log`, `artifacts/verify-external-voice.stderr.log`. 음성 집중 검사는 **15/15**이며 공급자별 요청/키 범위/SSML 이스케이프, 실제 HTTP 리디렉션 차단, 파일 크기·형식, 취소와 시간 초과 구분, 실패 시 대체 출력, 설정 내보내기 경계를 검사함.
- 실제 Windows 오디오 장치에서 기본/지정 출력의 WAV, MP3 디코딩·재생, 재생 도중 취소와 임시 파일 삭제를 검사함. 로컬 HTTP 시험 서버를 설정 UI에 등록하고 5.77초 음성을 재생, 생성 대기 중 취소 후 늦은 응답 미재생, HTTP 503의 기본 음성 전환·전환 해제, 키 저장/삭제, 설정 저장·재시작 후 일반 대화 답변 연결을 확인함. `artifacts/external-voice-ui-20261026.md`. 외부 유료 제공자 실계정 생성 품질이나 사용자 청취 품질을 검증한 것은 아님.
- 0.2.3 패키지에는 아래에서 개발본으로 기록한 받아쓰기 편집 명령, 브라우저 기능, 외부 음성 제공자가 포함됨. 이전 검사 시점의 0.2.2 미포함 기록은 이 버전에서 해소되며 실제 브라우저와 유료 서비스 현장 검증 경계는 유지함.

- 개발본 받아쓰기 편집 명령: 통합 **171/171**, Python **5/5**, 빌드 경고 0/오류 0. `artifacts/checks-dictation-controls.json`, `artifacts/verify-dictation-controls.log`, `artifacts/verify-dictation-controls.stderr.log`. 관련 집중 검사 **18/18**에는 기존 문장/저장 검사와 정확한 제어어·접미사 경계, 모델 출력의 명령 해석 방지, 승인 거절 시 무입력, 입력 실패·취소 시 뒤따르는 Enter 차단이 포함됨.
- 실제 메모장: 한 문장 받아쓰기의 말버릇 제거·입력, `취소`로 Ctrl+Z, 문장 뒤 `보내`로 입력 후 Enter, `줄바꿈`으로 Enter, 확인창 거절 시 문서 보존, 다른 창으로 전환한 요청의 입력 차단 확인. 시험 문서 원상 복원·추가 시험 창 종료·설정 파일 해시 불변. `artifacts/dictation-controls-ui-20261026.md`, `artifacts/dictation-controls-final.json`. 음성 인식 없이 공유 편집 경로를 검사했으며 사용자 발화 정확도 검사는 아님. 추가 기능은 0.2.2 MSI에 아직 미포함.

- 개발본 브라우저 이식: 통합 검사 **163/163**, Python **5/5**, 빌드 경고 0/오류 0. `artifacts/verify-browser-final.log`, `artifacts/verify-browser-final.stderr.log`. 120초 검색 문맥, 검색어 인코딩, 정확한 도메인/주소/ID 경계, 동일 제목 구분, 앞 창 우선·다른 창 검색, 탭 선택 복원, 목록 페이지, 중단·확인창 거절 시 후속 동작 중단을 검사함. 브라우저 조작 검사는 모의 드라이버이며 실제 Chrome/Edge 현장 증거가 아님.
- 실제 Claude에 모의 브라우저의 동일 제목·다른 주소 목록을 전달해 관측 ID로 지정 도메인 탭을 선택하고 완료하는 흐름 통과. 실제 사용자 브라우저를 Claude로 조작했다는 의미는 아님. 개발본 브라우저 기능은 아직 0.2.2 설치본에 미포함.

- Release 빌드: 경고 0, 오류 0.
- 다단계 AI UI: 실제 타이머 2개 조회 → 이름 조건으로 1개만 취소 → 나머지 유지 확인. 시험 메모장 본문의 고유 둘째 줄을 읽고 정확히 답변. 처리 중 단축키 취소 후 새 타이머 생성 없음, 대상 창 변경 시 중단, 입력 확인창에서 ‘아니요’ 선택 후 문서 불변 확인. 마지막에 시험 데이터 정리 후 config/apps/timers 파일이 시험 전 해시와 일치. `artifacts/agent-ui-20261026.md`.
- 다단계 실행 자동 검사: 실제 저장소의 타이머 ID 선택, 실패 후 재계획·의존 동작 생략, 전체 계획 사전 검증, 단계별 확인 거절·취소·대상 변경·12회 제한, 조회 결과의 JSON 경계/길이 제한, 조건부 학습 방지, 7개 제공자 모의 후속 요청, Claude 실응답의 조회 ID 선택을 포함함. 공급자 독립 JSON 실행 기록으로 연결하며 원본 MCP 도구 전체를 이식한 것은 아님.
- 0.2.1→0.2.2 MSI 업데이트 종료 코드 0. 설치본과 이전 버전 백업의 각 1,610개 파일 해시 일치, 설정 3개·음성 모델 5개 파일 불변. 외부 Python/.NET 경로를 제외한 설치본 검사 **144/144 통과**. 바탕화면 바로가기 대상의 버전 `0.2.2.0` 확인. `artifacts/installed-and-backup-022.json`, `artifacts/installed-self-test-022/checks.json`, `artifacts/upgrade-and-verify-022.log`. MSI SHA-256: `B690C72852CB42C2800788DE77BD1B337D41B2F9D37285276209AFE2E7C5E629`.
- 0.2.2 설치본 UI에서 타이머 목록 조회 단계와 0개라는 최종 답변 확인. 마이크 꺼짐·시험 타이머 없음. 사용자 음성 정확도를 개선했다는 의미는 아님.
- 0.2.2 통합 자동 검사: **148개 통과 / 0개 실패**, 추가 Python 단어/설치 경로 통합 검사 **5개 통과**. `verify.ps1 -AI -Codex -YouTube`, `artifacts/checks.json`, `artifacts/verify-agent-final.log`. 명령·정책·설정·저장/복원·WebView2·실제 마이크/음성 장치·Claude/Codex 실응답·실제 유튜브 검색 포함. 외부 API 부분은 아래의 모의 프로토콜 검사이며 유료 API 실연결 통과로 합산하지 않음.
- 배포 관련 자동 검사 6개: 검증 후 모델 교체/유효 파일 재사용/이전 모델 백업, 해시·크기·HTTP 실패 시 보존, 취소 시 임시 파일 정리, 경로 이탈·중복 파일 거절, 업데이트 ZIP에서 앱·설정 추출 후 원본 일치, 잘못된 설치 목록 거절.
- 모델 받기 UI: 실제 k2-fsa 다운로드 진행률 표시 → 취소/임시 폴더 제거 → 재시도 → 검증 완료 메시지. 사용자 모델 폴더의 다섯 파일 SHA-256이 고정된 목록과 일치함. 마이크 자동 시작 없음.
- 패키지의 Python 3.13.17은 격리 모드에서 numpy/sherpa-onnx/sounddevice/soundfile/sentencepiece/cffi 로드 및 장치 목록 조회 성공. MSI 설치·업데이트·제거 현장 검사는 아래 기록을 기준으로 판단.
- 설치본 시험에서 이름 감지기만 개발 폴더의 bpe.model을 참조하는 문제 발견 후, ASR과 동일한 선택 모델 경로를 사용하도록 수정. 비어 있는 개발 디렉터리 조건에서도 실제 KeywordSpotter가 준비되는 회귀 검사 추가.
- 독립 MSI 0.2.1 빌드 경고 0/오류 0. 초기 0.2.0의 한국어 설치 마법사에서 위치 확인/설치/완료를 확인. MSI API의 설치 컨텍스트 2(현재 사용자) 확인. 바탕화면 바로가기 대상은 `%LOCALAPPDATA%\SasarisaWindows\VoiceAssistant.exe`.
- 외부 Python/.NET 경로를 사용할 수 없는 자식 프로세스 환경에서 앱 실행 성공. 설치된 0.2.1도 같은 제한 환경에서 자동 검사 **126/126 통과**(외부 연결 선택 검사 3개 제외), `artifacts/installed-self-test-021/checks.json`. 패키지의 Python 3.13.17에서도 다섯 실제 모델 검사가 통과, `artifacts/python-packaged-tests.log`.
- MSI 0.2.0→0.2.1 업데이트 종료 코드 0. 기존 앱을 종료하고 이전 버전 ZIP 생성 후 적용. 새 설치본과 백업 ZIP의 앱 파일 각각 **1,610개가 해당 manifest의 SHA-256과 일치**. 설정 파일과 업데이트 전 만든 60분 타이머 파일도 동일. 재실행 화면에서 타이머 1개 복원 확인 후 시험 타이머 취소. `artifacts/installed-and-backup-021.json`, `artifacts/msi-upgrade-021.log`.
- 설치본 UI에서 Claude의 실제 ‘연결 성공’ 답변, 수정 후 이름 감지기 경로 경고 없이 ‘이름을 기다리고 있어요’와 마이크 준비 확인. 입력 과정은 설치 폴더의 Python을 사용. 사용자 발화 정확도를 검사한 것은 아님.
- 마이크가 켜진 상태에서 MSI 제거 종료 코드 0. 비서/음성 worker 종료, 설치 폴더와 MSI 소유 파일·바탕화면/시작 메뉴 바로가기 제거. 설정·등록 앱·타이머·모델 **8개 파일의 해시 불변**, 이전 버전 백업 유지. `artifacts/uninstall-result-021.json`, `artifacts/msi-uninstall-021.log`.
- 0.2.1 재설치 종료 코드 0, 위 8개 사용자 파일 불변, 설치 파일 1,610개 해시 일치. 바탕화면 바로가기를 두 번 실행해도 비서 1개/음성 worker 0개 유지. 최종 화면은 기본 대화 화면·Claude 선택·마이크 꺼짐·시험 타이머 없음. `artifacts/reinstalled-package-021.json`, `artifacts/msi-reinstall-021.log`. 최종 MSI SHA-256은 `CCCC673039E529EDDB04A1F22D840CE78B33A2EA48E4CD8766324D272DFD0279`.
- 설정 파일 자동 검사 4개: 중첩 설정 왕복/자격 증명 필드 없음, 다른 형식·버전·중복·null·키 필드·크기 초과 거절, 연결/장치 선택 복원, 기존 내보내기 보존과 파일 잠금 시 쓰기 실패 복구. 별도로 WPF 기본 창/복원 검토 창 리소스 로드를 검사.
- 설정 파일 UI: `artifacts/settings-export-ui.json` 내보내기 → 시험 파일의 변경 전후 확인 → 연결/장치 선택 시 제공자/장치 변경도 표시 → 취소 후 기존 값 유지 → 기본 범위 적용으로 이름 ‘아리’·가운데 알림·단어 2개 복원, Claude와 기본 마이크 유지. `config.json.backup`에서 이전 이름 ‘자비스’ 확인. 다른 형식 파일은 오류 표시 후 현재 값 유지. 시험 설정은 `artifacts/config-transfer-ui-tested.json`에 보관.
- 복원한 이름 ‘아리’가 앱 재시작 후 유지되는 것을 확인한 뒤, UI에서 처음 내보낸 파일을 다시 가져와 ‘자비스’·기본 알림 위치·빈 단어 목록으로 복원. 마지막 실행은 기본 대화 화면, Claude, 마이크 꺼짐 상태이며 앱 1개와 음성 worker 0개를 확인.
- 받아쓰기·개인화 자동 검사 10개: 전체 단어 말버릇 제거/내용 보존/줄바꿈, 끝 명령 경계, 이전 설정 호환/단어 제한, AI 동작 제안 거절, 과도한 수정/오류/시간 초과의 원문 복귀, 취소 후 입력 결과 미반환, 저장 편집/삭제/복원/재시작, 오래된 편집·복원 거절, 쓰기 실패·손상 파일 보존, 루틴 동작/중복/개수 제한.
- Python `runtime/test_vocabulary.py`: 지원 단어/중복/지원하지 않는 문자, 잘못된 입력 제한, 실제 한국어 모델에 힌트 2개 적용·임시 파일 정리·무음 스트림 완료, 기본 인식기/힌트 있는 눌러서 말하기의 마무리 신호를 검사. 문장 정확도 검사가 아님.
- 받아쓰기 설정 UI: ‘음, 음식을 먹고 으음 음악을 들어요.’ → ‘음식을 먹고 음악을 들어요.’ 확인. 실제 Claude 연결로 ‘음 내일 회의는 세 시에 시작합니다’ → ‘내일 회의는 3시에 시작합니다.’ 및 ‘AI로 다듬었어요’ 확인. 시험 중지 후 취소 표시와 이전 결과 유지 확인. 시험은 문서에 입력하지 않음.
- 단어 설정 UI: 메모장/인천상아초등학교 저장 → 마이크 준비 → ‘자주 쓰는 단어 2개를 인식 힌트로 적용했어요’ 확인. 앱 재실행 후 단어 설정과 AI 보정 꺼짐 유지. 인식률 개선으로 간주하지 않음.
- 개인화 관리 UI: 시험 기억 생성/수정/삭제/복원, 루틴 타이머 2개 추가·순서 변경·이름 수정, 배운 호출/응답/타이머 저장. 실제 대화에서 루틴으로 타이머 2개 실행 후 각각 되돌림, 배운 호출로 15분 타이머 실행 후 되돌림. ‘기억해’ 뒤 ‘되돌려’로 새 기억만 제거하고 원래 기억 유지, 재시작 후 저장 기억 표시 확인. 편집 화면이 열려 있는 동안 호출 단축키를 눌러도 음성 worker 0개 유지, 닫으면 한국어 모델/단어 힌트 준비 후 듣기 재개 확인. 시험 개인 데이터는 `artifacts/personal-ui-final.json`으로 옮겨 사용자 저장 위치에서 정리함.
- 데스크톱 설정 검사 9개: 키 조합 정규화/예약 키/중복/키 해제, 등록 실패와 설정 저장 실패 시 양쪽 키 복원, 시작 시 대체 키, 실제 Windows RegisterHotKey 충돌과 복원, 이전 설정 호환/새 설정 저장, 인사 호출 경계/치환, 인사 주소/개수 제한, 음수 좌표 모니터의 알림 배치, 빈 모델 파일 거절.
- 첫 실행 안내 UI: 기존 설정에 안내 완료 정보가 없는 상태에서 자동 표시 → ‘선생님’ 호칭 입력 → 목소리 시험 출력 완료 → 한국어 모델·입력 목록 11개·Claude 설치 상태 확인 → 저장. 마이크 꺼짐 유지, 재실행 후 안내가 다시 뜨지 않음.
- 단축키 UI: 말하기 Ctrl+Alt+F8 / 중지 Ctrl+Alt+F7로 변경·저장·재실행 유지. 변경한 말하기 키로 실제 모델과 마이크 준비 후 ‘말씀하세요’ 확인. 중지 키로 호출 대기 복귀 및 인사 음성 출력 취소 확인. 설정 입력 칸에서 이미 등록된 키를 눌러도 마이크가 켜지지 않으며, 동일 조합 저장은 오류 표시 후 기존 설정 유지.
- 알림 UI: 가운데 미리 보기, 실제 호출 알림, 작은 대기 표시, 작은 표시 클릭으로 말하기 전환, 마이크 중지 시 표시 숨김 확인. 위치/모니터 설정 저장 확인. 위쪽·모서리 위치의 계산은 자동 검사에 포함하되 다중 모니터/혼합 배율 실측으로 간주하지 않음.
- 인사 UI: 원형 애니메이션·차분한 화면·작은 알림 각각 표시, 사용자 호칭/비서 이름 치환 확인. ‘오늘 시작’ 호출 문장과 실제 시험 앱 연결을 저장한 후, 미리 보기에서는 시험 앱 프로세스 0개 유지. 실제 ‘오늘 시작’에서는 시험 창 열림 → ‘네가 연 창 닫아’로 그 창 닫힘. 재실행 후 호출/문구/앱 실행을 다시 확인. 시험값은 `artifacts/config-desktop-ui-tested.json`, 원래 값은 `artifacts/config-before-desktop-ui.json`에 보관.
- 이번 검사 후 사용자 호칭/인사/단축키/알림은 기본값으로 복원하고 처음 설정 완료 표시만 유지. 시험 앱 등록과 시험 창을 정리한 뒤 기본 화면·기본 단축키·마이크 꺼짐 상태로 다시 실행. 비서 1개, 시험 앱과 음성 worker 0개 확인.
- API·자격 증명 검사: 7개 제공자 경로·인증 헤더·함수 호출·응답 변환, HTTP 오류/잘린 응답/허용하지 않은 동작 거절, 취소, 주소별 키 분리, Windows 자격 증명 관리자에 고유한 가짜 키 저장·읽기·삭제. 실제 사용자 키를 사용하지 않음.
- 등록 앱·되돌리기 검사: 등록·이름 변경·저장/복원·명령 파싱·제거, 중복/명령 경로 거절, 손상 파일 보존, 쓰기 실패 시 메모리 상태 유지, 개별 타이머만 취소, 되돌리기 실패 재시도, 출력 장치 변경 시 음량 복원 거절.
- Python worker 컴파일, `pip check`, PowerShell 구문 검사.
- 마이크: G435 Wireless Gaming Headset가 입력 장치로 열리고 한국어 모델 준비 및 음량 이벤트 수신.
- 단축키: Ctrl+Shift+Space로 ‘말씀하세요’ 상태 전환.
- 한국어 입력: 별도로 만든 빈 메모장에 `윈도우 비서 입력 확인`을 실제 입력하고 화면 확인.
- 글 다듬기: 위 문장을 선택 → 영어 번역 요청 → 원문/수정안 비교 창 확인 → 적용 → 메모장에서 `Windows assistant input check` 확인. Claude 실응답 및 UI Automation 선택 영역 읽기/교체 경로 사용.
- 타이머: 3초 타이머 시작, 남은 시간 표시, 완료 알림을 대화 화면에서 확인. 저장 후 새 TimerStore로 복원·한 번만 발화 대상 추출·취소 상태 유지 자동 검사.
- 한국어 TTS: Windows Heami 선택과 재생 완료, 이후 대기 복귀 확인. 타이머가 다른 음성 출력 중 끝나면 안내가 큐에 들어가도록 보완.
- 출력 설정 UI: Microsoft Heami + G435 헤드셋 + 속도 1 + 음량 65 선택 → 시험 출력 완료 및 실제 SAPI 설정 로그 확인 → 저장된 값 확인. 테스트 뒤 기본 음성/기본 장치/속도 0/음량 85로 복원. 비서 답변 음량만 변경하며 시스템 음량을 변경하지 않음.
- 시험 출력 중 ‘답변을 읽는 중’ 상태와 중지 버튼 활성화 확인. 중지 버튼 및 Ctrl+Alt+Backspace 각각으로 취소 후 대기 상태 복귀, 취소한 시험 출력을 완료했다고 표시하지 않음.
- 설정: 9초 말 끝 대기 입력 거절, 1.2초 기본값 저장과 `config.json` 확인.
- 정상 종료 후 앱 소유 Python 음성 프로세스가 남지 않음.
- 바탕화면 `자비스 Windows.lnk` 생성, 바로가기에서 실행, 두 번째 실행이 기존 창을 복원하고 앱 프로세스 하나만 유지됨.
- AI 응답 대기 중 Ctrl+Alt+Backspace로 취소, 대기 상태 복귀와 자식 프로세스 0개 확인. 새 빌드의 듣기 중지 후에도 음성 프로세스 0개 확인.
- Codex: 설정 UI에서 제공자 선택/저장 → 정상 종료/재실행 → Codex 상태 유지 → 광합성 질문의 실응답 및 한국어 TTS 완료 확인.
- API 설정 UI: 루프백 모의 서버 `127.0.0.1:46383`에서 모델 2개 조회 → 목록 선택 → 연결 시험 → 설정 저장 → 일반 대화 경로로 응답 표시. 재실행 후 모델/주소 유지, Claude로 전환 시 기본 모델 복원 확인. `artifacts/api-ui-events.json`은 GET 모델 목록과 두 POST의 경로/모델/형식만 기록하며 키·프롬프트는 기록하지 않음. 이 서버는 언어 모델이 아닌 프로토콜 시험용임.
- 키 관리 UI: 가짜 키 입력 마스킹 → Windows 보관 성공 → 입력란 비움 → 선택한 주소의 키 삭제 → 키 없음 표시 확인. 실제 API 키는 등록하지 않음.
- 앱 등록 UI: 직접 만든 `VoiceAssistantFixture.exe`를 ‘검증 창’으로 등록 → 텍스트 ‘검증 창 열어’로 실제 실행 → 앱 재시작 후에도 재실행 → 목록에서 제거. 실제 실행 파일은 유지됨.
- 창 되돌리기: ‘되돌려’가 비서의 시험 창에 닫기 요청 → 시험용 저장 확인창 유지 → ‘아니요’ 후 창 유지 → 확인 옵션 해제 후 되돌리기 재시도 성공. 마지막 빌드에서 ‘네가 연 창 닫아’도 실제 새 창만 종료. 사용자가 열어 둔 메모장 창은 유지됨. 새 프로세스·새 창을 추적하고 창 파괴 이벤트로 핸들 재사용을 구별함.
- 음량 되돌리기 UI: 원래 20% → ‘볼륨 27로 해줘’ → ‘되돌려’로 20% 복원. 타이머 두 개 생성 → 되돌리기 한 번으로 마지막 것만 저장 목록에서 제거 → 나머지도 되돌려 정리. ‘방금 뭐했어’에서 앱/음량/타이머의 되돌림 상태 표시 확인.
- 유튜브: 실제 검색 결과 첫 영상 `aqz-KE-bpKQ` 재생 → ‘두 번째 영상 틀어’로 같은 창에서 `YE7VzlLtp-4` 재생. 접근성의 재생 중/일시중지 버튼과 시간 표시로 확인. 일시정지 후 0:09에서 ‘30초 빨리 감기’로 0:39 이동, ‘유튜브 창 닫아’로 창 종료 확인. 사용자의 다른 브라우저 탭은 건드리지 않음.
- 눌러서 말하기: 설정 UI 선택/저장, 실제 마이크 준비, 단축키 시작/해제 후 ‘말씀을 확인하는 중’ → ‘눌러서 말하기 대기’ 복귀 확인. 현재 자동화 도구는 Windows에서 키를 길게 누른 상태를 유지할 수 없으므로 실제 장시간 누르며 말하기는 현장 미검증.
- 실제 한국어 디코더의 눌러서 말하기 경계 검사: 키 해제 전 확정 0회, 해제 후 확정 1회와 완료 신호 수신. `runtime/check_hold.py`, `artifacts/hold-check.json`에 기록. **문장 정확도 검사는 통과하지 않았음**: 합성음 ‘메모장 열어’가 ‘메모지원여러.’로 인식됨. 경계 처리 통과를 음성 정확도 통과로 합산하지 않음.

## 구현됐지만 현장 검증이 더 필요한 항목

- 사용자 실제 발화의 호출 정확도·문장 오류율·소음 환경·장시간 연속 받아쓰기. 실제 발화→AI 보정→문서 입력 전체 경로와 처리 중 대상 창 변경 거절의 현장 확인은 별도이며, 위 설정 시험/자동 검사와 구분함.
- Ollama·LM Studio·호환 서버의 실제 모델, 사용자 키를 사용하는 외부 API 실연결, 선택 사항인 Whisper 서버 연결. 모의 서버 통과는 실제 모델 품질이나 계정 사용 권한 검증이 아님.
- 다른 마이크/사운드 드라이버와 Windows 10, 다른 Windows 배율 환경.
- 카카오톡·Claude Desktop·VS Code 등 앱별 입력/버튼 접근성. 앱 실행 경로는 존재 여부에 따라 안내하며, 설치되지 않은 앱을 실행했다고 보고하지 않음.
- 프로젝트 위임은 정상적인 Claude Code/Codex 세션을 시작하는 코드와 확인 흐름을 구현했지만 실제 프로젝트 수정 작업은 이 검증에서 실행하지 않음.
- 외부 앱 미디어 제어는 구현했으며 사용자 PC의 다른 앱 재생 상태를 바꾸는 테스트는 수행하지 않음. 시스템 음량 복원과 전용 영상 창의 미디어 제어는 위 항목처럼 확인함.
- 바로가기의 다양한 실행 방식, Store 앱 중개 프로세스, 기존 프로세스로 전달되는 앱의 창 소유권 식별. 현재는 정확히 새로 시작한 프로세스의 새 창만 자동 닫기 대상임.

## 원본과 다른 범위

- 유튜브 재생은 비서 전용 WebView2 창 사용. 별도 프로필이며 로그인이 필요한 영상은 브라우저로 이동해야 함. 검색 구조·네트워크·동의 페이지 변화 시 검색 페이지로 대체함.
- Claude Code, Codex, Ollama와 7개 API/호환 제공자를 구현함. 실행 결과를 JSON으로 다음 계획 요청에 전달하는 다단계 처리를 구현함. 원본 MCP 전체 도구·이미지 결과와 Aside는 남은 작업임. 7개 API 검사는 모의 서버이며 외부 키로 실제 호출한 증거는 아님.
- 되돌리기는 음량·개별 타이머·소유 창/영상 창·기억 추가·루틴 저장/삭제에 구현. 관리 화면도 마지막 변경 복원을 지원하며 이후 다른 저장을 덮어쓰지 않음. 문서 입력·웹 탭의 정확한 역동작은 남은 작업이며 지원되지 않는 동작은 안내함. 원본과 같은 전체 역동작 지원으로 간주하지 않음.
- 프로젝트 위임은 지정 폴더의 새 Claude Code/Codex 세션. 기존 GUI 세션 지정은 미구현.
- macOS AppleScript/Accessibility 전용 동작은 Windows UI Automation/SendInput으로 일부 대체. 모든 앱의 접근성 지원을 보장하지 않음.
- 0.2.18에서 출력 중 음성 끼어들기를 구현. 정책·출력 측정·입력 복원 검사와 별도 앱의 합성 인식 이벤트로 중단·듣기 복귀를 확인했으나 실제 사람의 발화 및 스피커 반향 환경 검증은 미완료. 중지 단축키·버튼도 제공.
- 독립 MSI는 로컬 시험 빌드이며 코드 서명된 공개 릴리스는 미제공. Windows 10이나 새 PC에서의 설치를 검증한 것으로 간주하지 않음.

원본 기능별 남은 작업은 [FEATURE_PARITY.md](FEATURE_PARITY.md)를 따릅니다. 이 기록은 현재 구현의 검증 증거이며 원본의 모든 기능과 인식 품질이 완전히 같다는 검증은 아닙니다.
