# MotorCoreVision

MotorCoreVision은 모터 코어 이미지 촬영 및 검사를 위한 WPF 애플리케이션입니다.

## 현재 버전

**1.0.3.44**

## Release Notes

### 1.0.3.44

- 프로그램 시작 시 Trigger Step 중복 순서에 맞춰 `InspectionManager`를 초기화하고, 자동 검사와 Setup 창에서 공유합니다. 종료 시 공용 manager를 해제합니다.
- 이미지 저장을 bounded queue와 worker thread로 분리하고, 메인 화면 하단에서 대기 상태와 오류를 확인할 수 있습니다. 큐가 가득 차면 저장 작업을 보존하기 위해 enqueue가 대기합니다.
- 검사 설정에서 이미지 저장 방식을 저장 안 함, OK만 저장, NG만 저장, 모두 저장 중 선택할 수 있습니다.
- Setup 창의 촬영, 검사, 이미지 불러오기/저장, 탐색기 동작 중 대기 커서를 표시합니다.
- 메인 화면에서 Reserve 버튼을 숨기고 Memory Usage 표시 너비를 조정했습니다.

### 1.0.3.43

- `--emul` 실행 옵션을 추가해 실제 카메라와 PLC 없이 설정 화면에서 에뮬레이터를 실행할 수 있습니다.
- Local Process 실행 결과의 ROI를 기반으로 검사 결과를 판정하고, Process가 생성한 DISPLAY 이미지를 검사 화면에 표시합니다.
- 검사 결과 이미지 저장 시 이미지 이름을 파일명에 포함합니다.
- Process 편집기 초기화와 다중 이미지 입력 처리를 보완했습니다.

### 1.0.3.42

- 시퀀스 설정에 `Inspection`, `InspectionAndOk`, `InspectionAndNg`, `ForceOk`, `ForceNg` 출력 형식을 추가했습니다.
- `InspectionAndOk`와 `InspectionAndNg`는 검사를 수행한 뒤 결과를 각각 OK/NG로 지정합니다. `ForceOk`와 `ForceNg`는 비전 Process 실행을 생략하고 결과를 지정하며, 촬영과 이미지 표시는 계속 수행합니다.
- `InspectionPass`가 켜져 있으면 모든 시퀀스를 OK로 표시하고, 꺼져 있으면 각 시퀀스의 `OutputType`에 따라 결과를 만듭니다.
- `InspectionManager.Run()` 결과에 `Judge()` 연결 지점을 추가했습니다. Local 결과 이미지 판정 로직은 아직 구현 전이며, 현재 `Judge()`는 `false`를 반환합니다.
- PLC Reject Address에는 해당 TriggerStep 내 시퀀스 순서대로 NG 비트를 기록합니다. 첫 번째 시퀀스는 bit 0, 두 번째는 bit 1입니다.

### 1.0.3.41

- Hikvision GigE 카메라를 지원합니다. IP 주소로 카메라를 선택하고 노출 시간을 설정할 수 있습니다.
- 검사 설정 편집기에서 카메라 종류를 선택할 수 있습니다.
- 설정 촬영 및 자동 검사 촬영 전후에 외부 조명을 제어합니다.
- 자동 검사 단계별 소요 시간을 Visual Studio 디버그 출력 창에 기록합니다.
- `InspectionPass` 설정에 따라 촬영 이미지 판정 결과를 처리합니다.

## 빌드

Visual Studio에서 `MotorCoreVision.sln`을 열고 `MotorCoreVision` 프로젝트를 **x64**로 빌드합니다. 애플리케이션은 **.NET Framework 4.8.1**을 대상으로 합니다.

빌드에는 Hikvision MVS .NET SDK와 네이티브 런타임이 필요합니다. 이미지 처리 라이브러리는 `ref\processing` 아래의 파일을 참조합니다.

## 검사별 이미지 입력

### ST#1 (#65A)

아래 내용은 디딤에서 제공한 `D:\Setup\파비스(현대자동차) 2\알고리즘\`의 JSON에 저장된 `InputNameList`를 기준으로 정리했습니다. 번호는 배열의 0부터 시작하는 위치입니다. `Empty` 항목은 비어 있는 입력 슬롯입니다.

#### 측면 검사

| 구분 | 대상 JSON | 입력 목록 / 비교 결과 |
|---|---|---|
| Mask | `Make Mask.json` | 0 REF, 1 RGH, 2 RGV, 3 RG2, 4 INH, 5 INV, 6 IN2, 7 R, 8 G, 9 B, 10 Right |
| Global | `Global 갭.json`, `Global 귀 갭.json`, `Global 녹.json`, `Global 바니시.json`, `Global 스크래치.json`, `Global 에폭시.json`, `Global 찍힘.json`, `Global Edge찍힘.json` | 8개 파일의 목록이 동일: 0–11 `반사율_[0]`, `거칠기(H)_[0]`, `거칠기(V)_[0]`, `기울기(H)_[0]`, `기울기(V)_[0]`, `Red_[0]`, `Green_[0]`, `Blue_[0]`, `Mask_[0]`, `Hole Mask_[0]`, `Resizer Mask_[0]`, `REF_[0]`; 12–16은 `Empty[12]`–`Empty[16]` |
| Global 예외 | `Global Empty.json` | 위 8개와 다름. `Empty0`–`Empty16`으로만 구성 |
| Local 공통 | 측면의 `Local` JSON 8개 | 0–9 공통: `반사율_[0]`, `거칠기(H)_[0]`, `거칠기(V)_[0]`, `기울기(H)_[0]`, `기울기(V)_[0]`, `Red_[0]`, `Green_[0]`, `Blue_[0]`, `Mask_[0]`, `Hole Mask_[0]` |
| Local별 차이 | `Local 갭.json` | 10–12 `REF 밝기 펴기_[0]`, `갭 찾기 전_[0]`, `외각 찾기 전_[0]`; 13–16 Empty |
|  | `Local 귀 갭.json` | 10–12는 `Local 갭`과 같고, 13 `외각 검사 Mask 영역_[0]`; 14–16 Empty |
|  | `Local 녹.json` | 10 `Color BPF_[0]`, 11 `REF_[0]`; 12–16 Empty |
|  | `Local 바니시.json` | 10 `3D Check 용_[0]`, 11 `Result Mask_[0]`; 12–16 Empty |
|  | `Local 스크래치.json` | 10–14 `BPF RGH_[0]`, `BPF RGV_[0]`, `BPF INH_[0]`, `BPF INV_[0]`, `BPF REF_[0]`; 15–16 Empty |
|  | `Local 에폭시.json` | 10–11 `REF 2 RGH_[0]`, `REF 2 RGV_[0]`; 12–16 Empty |
|  | `Local 찍힘.json` | 10–11 모두 `BPF INH_[0]`; 12–16 Empty. 같은 입력명이 연속으로 중복됨 |
|  | `Local Edge찍힘.json` | 10–13 `BPF INH_[0]`, `BPF INH_[0]`, `3D_[0]`, `BPF-3D_[0]`; 14–16 Empty |

#### 내경 검사

| 구분 | 대상 JSON | 입력 목록 / 비교 결과 |
|---|---|---|
| Mask | `Mask.json`, `Mask - 복사본.json` | 두 파일의 목록이 동일: 0 `반사율`, 1 `거칠기(H)`, 2 `거칠기(V)`, 3 `거칠기(양방향)`, 4 `4INH`, 5 `5INV`, 6 `기울기(양방향)`, 7 `반사율(PolarTheta)`, 8 `거칠기(PolarTheta)`, 9 `9INC`, 10 `R`, 11 `G`, 12 `B` |
| Global | `Glabal_Dent.json`, `Glabal_SC.json`, `Global_Epoxy.json`, `Global_Gap.json`, `Global_Pollution.json` | 5개 파일의 목록이 동일: 0 `반사율`, 1 `거칠기(H)`, 2 `거칠기(V)`, 3 `거칠기(양방향)`, 4 `기울기(2DTHETA)`, 5 `기울기(2DPHI)`, 6 `기울기(양방향)`, 7 `반사율(PolarTheta)`, 8 `거칠기(PolarTheta)`, 9 `기울기(PolarTheta)`, 10 `R`, 11 `G`, 12 `B`, 13 `종이Mask`, 14 `노치Mask`, 15 `Mask`, 16 `에폭시Mask` |
| Global 예외 | `Global Empty.json` | 위 5개와 다름. `Empty0`–`Empty16`으로만 구성 |
| Local 공통 | 내경의 `Local` JSON 5개 | 0–8 공통: `반사율_[0]`, `거칠기(H)_[0]`, `거칠기(V)_[0]`, `거칠기(양방향)_[0]`, `기울기(2DTHETA)_[0]`, `기울기(2DPHI)_[0]`, `기울기(양방향)_[0]`, `반사율(PolarTheta)_[0]`, `거칠기(PolarTheta)_[0]`; 13–16 공통: `종이Mask_[0]`, `노치Mask_[0]`, `Mask_[0]`, `에폭시Mask_[0]` |
| Local별 차이 | `Local_Dent.json` | 9–12 `PolarTheta_Region_[0]`, `Theta_패턴제거_[0]`, `Phi_패턴제거_[0]`, `3D결과_[0]` |
|  | `Local_Epoxy.json` | 9–12 `3D데이터_[0]`, `Polar거칠기_패턴제거H_[0]`, `Polar거칠기_패턴제거V_[0]`, `REF_패턴제거_[0]` |
|  | `Local_Gap.json` | 9–12 `기울기(PolarTheta)_[0]`, `외부갭_[0]`, `내부갭_[0]`, `외부갭2_[0]` |
|  | `Local_Pollutioin.json` | 9–12 `기울기(PolarTheta)_[0]`, `용접 오염 찾기용_[0]`, `G_[0]`, `B_[0]` |
|  | `Local_SC.json` | 9–12 `3D데이터_[0]`, `REF_패턴제거_[0]`, `RGH_패턴제거_[0]`, `RGV_패턴제거_[0]` |

#### 확인된 예외 및 주의점

- 내경 Global 파일명 `Glabal_Dent.json`, `Glabal_SC.json`은 `Global`이 아닌 `Glabal`로 저장되어 있습니다.
- 내경 Mask 두 파일은 `InputNameList`뿐 아니라 `InputPathList`도 동일하며, 모두 `E:\Sample\현대자동차\20250709_에폭시\250709-4.내경바니시\내경\1\...`의 샘플 BMP 경로를 가리킵니다.
- 측면 `Global 녹.json`과 `Global 바니시.json`은 공통 목록과 달리 8번 `Mask_[0]`, 9번 `Hole Mask_[0]`의 `InputPathList`가 채워져 있습니다. 두 항목 모두 `D:\#개발\현대 코어\20250825\250825 03 sfzos8s0009\측면\3\0REF.bmp`를 가리킵니다. 다른 검사 JSON의 해당 경로는 비어 있습니다.
- `Make Hole.json`은 측면의 별도 보조 JSON으로, 11개 입력(반사율, RGH, RGV, RG2, INH, INV, IN2, R, G, B, Mask)을 가집니다. `Make Mask.json`과 입력 목록이 다릅니다.
- 내경 Local의 `Local_Pollutioin.json`도 파일명에 `Pollutioin` 오타가 있습니다.
