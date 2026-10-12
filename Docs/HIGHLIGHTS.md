# 주요 작업

**클린업 마피아**는 Unity와 Netcode for GameObjects(NGO)로 만든 멀티플레이 파티 게임입니다. 호스트가 서버를 겸하고, Steam 릴레이로 접속합니다. 저는 클라이언트 프로그래머 2명 중 한 명으로 아이템·페인트·점수·게임 진행 등 게임플레이 시스템을 맡았습니다. 담당 범위는 [AUTHORSHIP.md](AUTHORSHIP.md)에 파일 단위로 정리했습니다.

먼저 봐 주셨으면 하는 작업은 세 가지입니다.

| | 작업 | 한 줄 요약 | 먼저 볼 코드 |
|---|---|---|---|
| 1 | 동작 단위 아이템 인터페이스 | 새 도구를 추가해도 플레이어 코드는 바뀌지 않는 구조 | [PickupContracts](../Scripts/Item/Interface/PickupContracts.cs), [PlayerInteraction.OnUseItem](../Scripts/Player/PlayerInteraction.cs#L622) |
| 2 | 클라이언트는 행동만, 결과는 호스트가 | 모든 게임 행동을 "요청 → 서버 검증 → 결과 전파"로 통일 | [RequestPaintServerRpc](../Scripts/Player/Mafia/MafiaPaintAction.cs#L302), [PickupItem](../Scripts/Item/PickupItem.cs#L112) |
| 3 | 페인트·오염도 시스템 | 어떤 메시에든 칠하고, 칠한 면적을 서버가 GPU로 집계 | [PaintSurfaceManager](../Scripts/Item/PaintGun/PaintSurfaceManager.cs), [PaintBrush.shader](../Shaders/Paint/PaintBrush.shader) |

---

## 1. 동작 단위 아이템 인터페이스

테이저건, 흡입기, 청소 도구, 폴라로이드 카메라, 상자, 쓰레기처럼 아이템 종류는 많지만, 플레이어가 아이템에 하는 동작은 **사용·조준·촬영·줍기/놓기** 정도로 정해져 있습니다. 그래서 아이템 종류가 아니라 **동작 단위로 인터페이스를 나누고**, 아이템은 자기가 할 수 있는 동작만 구현하게 했습니다.

| 동작 | 인터페이스 | 구현한 아이템 |
|---|---|---|
| 사용 (좌클릭) | [`IUsableItem`](../Scripts/Item/Interface/IUsableItem.cs) | 테이저건, 흡입기, 청소 도구 |
| 조준 | `IZoomTool` | 조준 가능한 아이템 공용 컴포넌트 [`ZoomableItem`](../Scripts/Item/Interface/ZoomableItem.cs) |
| 촬영 | `ICaptureTool` | 폴라로이드 카메라 |
| 줍기·놓기 때의 반응 | `IPickupListener` | 상자, 폴라로이드 카메라, `ZoomableItem` |
| 처리 방식 분류 | `PickupCategory` (쓰레기 / 상자 / 도구) | 모든 아이템 |

### 플레이어 코드는 도구를 모릅니다

플레이어 입력 코드는 들고 있는 아이템이 해당 동작을 구현했는지만 확인하고 호출합니다.

```csharp
// PlayerInteraction.OnUseItem — 좌클릭 하나로 테이저건·흡입기·청소 도구를 모두 처리
PickupItem currentItem = _inventory.GetCurrentEquippedItem();
if (currentItem == null) return;

if (currentItem.TryGetComponent<IUsableItem>(out var usableItem))
{
    usableItem.OnUse(context, playerCamera);
}
```

처음에는 흡입기 전용 입력 함수, 켜고 끄는 ServerRpc, "요청한 게 들고 있는 아이템인지" 확인하는 코드가 모두 플레이어 코드 안에 있어서 도구가 늘 때마다 플레이어 코드도 같이 커졌습니다. 이 코드를 인터페이스 뒤로 옮겼고([#42](https://github.com/YoonB-dev/CleanUp-Mafia-Portfolio/commit/7ea6c9d)), 이어서 청소 도구와 카메라 촬영도 같은 방식으로 옮겼습니다([9e0ef82](https://github.com/YoonB-dev/CleanUp-Mafia-Portfolio/commit/9e0ef82)). 지금 [`PlayerInteraction`](../Scripts/Player/PlayerInteraction.cs)은 테이저건·흡입기·청소 도구·카메라 클래스를 하나도 직접 참조하지 않습니다. 새 도구는 인터페이스만 구현하면 기존 입력에 바로 연결됩니다.

- 입력 분기: [OnUseItem](../Scripts/Player/PlayerInteraction.cs#L622), [OnAim](../Scripts/Player/PlayerInteraction.cs#L514), [OnCapture](../Scripts/Player/PlayerInteraction.cs#L525)
- 구현 예: [TaserGun.OnUse](../Scripts/Item/TaserGun/TaserGun.cs#L55), [MagnetAttractor.OnUse](../Scripts/Item/MagnetAttractor.cs#L81), [PaintCleaner.OnUse](../Scripts/Item/PaintGun/PaintCleaner.cs#L69), [PolaroidCamera.Capture](../Scripts/PolaroidCamera/PolaroidCamera.cs#L143)

### 네트워크 처리도 아이템이 직접 가집니다

아이템을 주우면 서버가 **든 사람에게 소유권을 넘기고**(`ChangeOwnership`), 놓으면 회수합니다. NGO의 ServerRpc는 소유자만 호출할 수 있으므로, 아이템의 ServerRpc는 자연히 지금 든 사람만 부를 수 있습니다. 그래서 각 도구가 자기 RPC를 가져도 플레이어 코드가 중간에서 확인할 필요가 없습니다. ([PickupItem.Pickup](../Scripts/Item/PickupItem.cs#L112), [MagnetAttractor.SetMagnetStateServerRpc](../Scripts/Item/MagnetAttractor.cs#L94))

쓰레기통 같은 공용 시스템도 상자나 도구 클래스를 모르고 `PickupCategory` 값만 봅니다. ([TrashCan](../Scripts/Item/TrashCan/TrashCan.cs#L40))

---

## 2. 클라이언트는 행동만, 결과는 호스트가

게임 결과에 영향을 주는 행동은 모두 같은 흐름을 따릅니다.

> **클라이언트**: 입력과 대상만 보냄 → **호스트(서버)**: 조건을 다시 확인하고 결과를 계산 → **모든 클라이언트**: 결과를 받아서 반영

클라이언트가 "칠했다", "주웠다", "몇 미터 움직였다"를 정하지 않고, 정해진 결과만 받아 그리기 때문에 조작된 요청이 와도 서버 기준의 결과만 남습니다.

| 행동 | 클라이언트가 보내는 것 | 호스트가 하는 것 | 코드 |
|---|---|---|---|
| 이동·점프 | 입력 방향, 점프 요청 | 서버에서 캐릭터를 직접 이동시킴 (클라이언트는 위치를 정하지 않음) | [SubmitMoveServerRpc](../Scripts/Player/PlayerMovement.cs#L222), [서버 이동](../Scripts/Player/PlayerMovement.cs#L96) |
| 줍기 | 집으려는 대상 | 거리, 이미 누가 들었는지, 인벤토리 빈칸 확인 → 소유권 이전 → "누가 들었다" 전파 | [TryPickupServerRpc](../Scripts/Player/PlayerInteraction.cs#L303) |
| 던지기 | 버튼을 눌렀다 / 뗐다 | 누른 시간을 서버 시계로 재고, 서버에 동기화된 시선 방향으로 힘·회전을 계산해 전파 | [StartThrowChargeServerRpc](../Scripts/Player/PlayerInteraction.cs#L565), [RequestDropOrThrowServerRpc](../Scripts/Player/PlayerInteraction.cs#L574) |
| 슬롯 변경 | 슬롯 번호 | 바꿀 수 있는 상태인지 확인 후 서버 변수 갱신 | [RequestChangeSlotServerRpc](../Scripts/Player/PlayerInventory.cs#L141) |
| 페인트 | 표면 ID, 맞은 지점 | 역할·게이지·발사 간격·사거리 확인, 반경은 서버 값으로 전파 | [RequestPaintServerRpc](../Scripts/Player/Mafia/MafiaPaintAction.cs#L302) |
| 청소 | 표면 ID, 맞은 지점 | 요청한 사람이 실제로 든 사람인지, 사거리 확인 | [RequestCleanServerRpc](../Scripts/Item/PaintGun/PaintCleaner.cs#L94) |
| 테이저 발사 | 발사 요청 | 요청한 사람이 든 사람인지, 탄약·발사 간격 확인 → 그 사람의 시점에서 발사 위치·방향을 계산해 서버가 탄을 생성 | [RequestFireServerRpc](../Scripts/Item/TaserGun/TaserGun.cs#L77) |
| 사진 촬영 | 축소·JPG 인코딩한 사진 | 크기 상한(512KB) 확인 후 서버가 사진 오브젝트 생성 | [RequestSpawnPhotoServerRpc](../Scripts/PolaroidCamera/PolaroidCamera.cs#L203) |
| 상자 배치 | 놓고 싶은 위치 | 거리·배치 구역·빈자리를 서버가 다시 판정 | [TryPlaceBoxServerRpc](../Scripts/Player/PlayerInteraction.cs#L430) |

### 보이기만 하는 것은 "일어난 일"만 한 번 보냅니다

판정에 쓰이지 않는 연출은 위치를 계속 보내지 않고, 이벤트 한 번만 보낸 뒤 각 클라이언트가 이미 동기화된 정보로 그립니다.

- **손에 든 아이템**: "누가 들었다"만 보내고, 각 클라이언트가 그 플레이어의 손에 아이템을 붙입니다. 들고 있는 동안에는 아이템 위치를 주고받지 않습니다. 놓거나 던질 때는 손 앞 위치에 고정하고 물리를 먼저 켠 뒤, 1프레임 뒤에 `NetworkTransform`을 다시 켜고 서버가 위치를 확정합니다. 놓는 순간 아이템이 예전 위치로 순간이동하던 문제를 이 순서로 해결했습니다. ([AttachToHolderClientRpc](../Scripts/Item/PickupItem.cs#L157), [DetachRoutine](../Scripts/Item/PickupItem.cs#L246))
- **페인트**: 텍스처 대신 "어디를 칠했다"만 보냅니다 (3번).
- **테이저 탄 박힘**: 서버가 맞은 지점에서 가장 가까운 뼈와 그 뼈 기준 위치를 한 번 계산해 보내고, 각 클라이언트는 그 뼈에 시각용 탄을 붙입니다. 뼈가 움직이면 탄도 따라 움직입니다. ([AttachServer](../Scripts/Item/TaserGun/PlayerTaserStuck.cs#L57), [AttachClientRpc](../Scripts/Item/TaserGun/PlayerTaserStuck.cs#L169))
- **발소리**: 아무것도 보내지 않습니다. 각 클라이언트가 동기화된 플레이어 위치로 이동 거리를 재서 발밑 표면에 맞는 소리를 냅니다. ([PlayerFootsteps](../Scripts/Sound/Footstep/PlayerFootsteps.cs#L86))

### 소유권 문제 해결 사례

파쇄기처럼 줍지 않고 끄는 오브젝트는 소유권이 서버에 있어서, 클라이언트가 그 오브젝트의 ServerRpc를 직접 부르면 거부되고 호스트에서만 끌렸습니다. 요청을 **플레이어가 소유한 오브젝트의 ServerRpc로 받고** 서버가 대상의 로직을 호출하게 바꿨습니다. 끄는 동안의 위치(계단 오르내림, 낭떠러지 낙하)도 서버가 계산합니다. ([#45](https://github.com/YoonB-dev/CleanUp-Mafia-Portfolio/commit/c118661), [RequestStartDragServerRpc](../Scripts/Player/PlayerInteraction.cs#L225), [DraggableObject](../Scripts/Item/Interface/DraggableObject.cs#L173))

---

## 3. 페인트·오염도 시스템

마피아가 벽과 바닥에 페인트를 칠하고 시민이 지우며, 칠해진 면적이 오염도 점수가 되는 시스템입니다. 셰이더부터 네트워크, 점수 집계, 에디터 툴까지 제가 만들었습니다.

### 어떤 메시든 같은 크기로 칠해지는 브러시

처음에는 레이가 맞은 지점의 UV 좌표에 브러시를 찍어서, UV가 펼쳐진 정도에 따라 표면마다 자국 크기가 달랐습니다. 브러시를 **월드 좌표 기준**으로 다시 만들었습니다([3c17719](https://github.com/YoonB-dev/CleanUp-Mafia-Portfolio/commit/3c17719)).

- 표면 메시를 UV 위치에 펼쳐 렌더 텍스처에 그리면, 셰이더는 각 픽셀이 메시의 어느 월드 지점인지 압니다. 브러시 중심과의 **월드 거리(미터)**로 칠할지 정하므로 어떤 메시든 같은 반경이면 같은 크기 자국이 나고, 모서리에 찍으면 양쪽 면에 걸쳐 칠해집니다. ([DrawAt](../Scripts/Item/PaintGun/PaintSurfaceManager.cs#L149), [PaintBrush.shader Pass 0](../Shaders/Paint/PaintBrush.shader#L49))
- 원본 머테리얼은 건드리지 않고 같은 메시의 투명 오버레이를 자동으로 만듭니다. 칠할 오브젝트에는 컴포넌트 하나만 붙이면 됩니다. 마스크 해상도는 표면 크기에 따라 512~4096으로 정합니다. ([PaintableSurface](../Scripts/Item/PaintGun/PaintableSurface.cs#L96), [PaintOverlayURP.shader](../Shaders/Paint/PaintOverlayURP.shader))

### 크기와 상관없이 공정한 점수

오염도를 처음에는 표면별 칠해진 비율의 평균으로 계산해서, 넓은 바닥은 많이 칠해도 점수가 거의 오르지 않았습니다. **실제로 칠해진 면적(m²)**으로 바꿨습니다.

- 칠해진 픽셀 수 × 픽셀 하나가 덮는 월드 면적으로 계산합니다.
- 픽셀당 면적은 표면을 등록할 때 **GPU로 한 번 측정**합니다. 메시를 펼쳐 그리면서 `ddx`/`ddy`로 이웃 픽셀과의 월드 위치 차이를 구해 면적을 기록합니다. 빌드에서는 메시 Read/Write가 꺼져 있어 스크립트로 정점을 읽을 수 없기 때문에 GPU에서 계산했습니다. ([MeasureAreaPerPixel](../Scripts/Item/PaintGun/PaintSurfaceManager.cs#L79), [PaintBrush.shader Pass 1](../Shaders/Paint/PaintBrush.shader#L77), [ScoreManager](../Scripts/Manager/ScoreManager.cs#L292))

### 텍스처가 아니라 그리기 명령을 동기화

표면 하나의 마스크는 256KB~16MB라서 칠할 때마다 보낼 수 없습니다. 네트워크로는 **표면 ID, 맞은 지점, 반경**만 보내고, 각 클라이언트가 같은 셰이더로 자기 텍스처에 그립니다. 서버는 칠하기 요청을 검증한 뒤에만 전파합니다(2번 표). ([RequestPaintServerRpc](../Scripts/Player/Mafia/MafiaPaintAction.cs#L302), [ApplyPaintClientRpc](../Scripts/Player/Mafia/MafiaPaintAction.cs#L324))

### 점수 집계는 서버에서만, 겹치지 않게

점수를 정하는 집계는 서버에서만 합니다. `AsyncGPUReadback`으로 마스크를 비동기로 읽어 칠해진 픽셀을 세고, 같은 표면에 읽기 요청이 진행 중이면 새 요청을 보내지 않습니다. 연사로 초당 여러 번 칠해도 전체 픽셀 집계가 겹쳐 쌓이지 않습니다. ([RequestContaminationReadback](../Scripts/Item/PaintGun/PaintSurfaceManager.cs#L194))

### 에디터 툴

표면마다 네트워크에서 쓰는 식별자(Surface ID)가 필요해서, 메뉴 하나로 씬의 모든 표면 ID를 다시 매기는 에디터 툴을 만들었습니다(Undo 지원). ([ReassignAllSurfaceIds](../Scripts/Editor/PaintableSurfaceEditor.cs#L25))

---

## 그 밖의 작업

- **게임 시작 시 일괄 스폰**: 클라이언트마다 씬 로드가 끝나는 순간 따로 스폰하면서 캐릭터가 원점에 보였다가 이동하던 문제를, 방 인원 전원이 씬 로드를 마치면 처음부터 스폰 위치에 한 번에 생성하도록 바꿨습니다. 로딩 중 나간 사람은 빼고 다시 확인합니다. ([CheckAndSpawnAllPlayers](../Scripts/Manager/InGameManager.cs#L105))
- **접속 승인**: 이미 게임이 시작된 방과 정원이 찬 방에는 접속 단계에서 입장을 거부합니다. ([ApprovalCheck](../Scripts/Network/NetworkConnect.cs#L109))
- **폴라로이드 카메라**: 카메라마다 전용 렌더 텍스처를 만들어, 모든 카메라 뷰파인더가 같은 화면을 보이던 문제를 해결했습니다. 촬영한 사진은 축소·JPG 인코딩해서 전송합니다. ([PolaroidCamera](../Scripts/PolaroidCamera/PolaroidCamera.cs#L62))
- **ComfyUI 연동 에디터 툴**: 에디터 안에서 2D 이미지 생성 → 수락/거절 → 3D 변환 → 임포트·씬 배치까지 이어지는 에셋 생성 툴을 만들었습니다. ([ComfyUIController](../Scripts/AI/ComfyUIController.cs))
- **사운드 시스템**: 사운드 매니저, 표면별 발소리, UI 사운드 자동 연결 등 사운드 전반. ([Sound/](../Scripts/Sound/))
