# CleanUp-Mafia Unity Project (Portfolio ver.)
> ⚠️ 이 레포는 [클린업 마피아]의 전체 프로덕션 리포지토리가 아니라, 스크립트(`Scripts/`)와 제가 작성한 페인트 셰이더(`Shaders/`)만 가져온 것입니다.

> 🔍 **코드 리뷰어분께:** 핵심 설계(동작 단위 아이템 인터페이스, 서버 권위 구조, 페인트·오염도 시스템)와 해당 코드 위치는 **[HIGHLIGHTS.md](Docs/HIGHLIGHTS.md)** 에 정리해 두었습니다. 10분 읽기 순서도 함께 있습니다.
>
> 팀 프로젝트라 팀원의 코드도 함께 들어 있습니다. 제가 작성한 범위는 **[AUTHORSHIP.md](Docs/AUTHORSHIP.md)** 에 파일 단위로 구분해 두었습니다.

## 🎬 Gameplay Video

[![프로젝트 시연 영상](https://img.youtube.com/vi/LFLyDDB7Q7I/hqdefault.jpg)](https://www.youtube.com/watch?v=LFLyDDB7Q7I)

## 🖼️ Screenshots / Gallery

<table>
  <tr>
    <td align="center">
      <img src="https://github.com/user-attachments/assets/1e6a497a-2d62-49f0-8097-f3fae1f81198" width="600" height="350" style="object-fit: cover;" alt="이미지 1"><br>
      <sub><b>메인 화면</b></sub>
    </td>
    <td align="center">
      <img src="https://github.com/user-attachments/assets/c2b2a43d-1d9f-41a6-a589-da6c3c64e98b" width="600" height="350" style="object-fit: cover;" alt="이미지 2"><br>
      <sub><b>흡입기 동작 화면</b></sub>
    </td>
  </tr>
  <tr>
    <td align="center">
        <img src="https://github.com/user-attachments/assets/58ee4e8c-6c69-4d5d-8be8-45654ab7a6d3" width="600" height="350" style="object-fit: cover;" alt="이미지 1"><br>
        <sub><b>페인트 총 화면</b></sub>
      </td>
      <td align="center">
        <img src="https://github.com/user-attachments/assets/679693e6-729a-4a45-a7b7-3acd31e8258e" width="600" height="350" style="object-fit: cover;" alt="이미지 2"><br>
        <sub><b>폴라로이드 카메라 동작 화면</b></sub>
      </td>
  </tr>

</table>

---

## 📌 프로젝트 개요

**클린업 마피아**는 3~12명이 시민과 마피아로 나뉘어 정해진 시간 안에 주변을 청소하는 멀티플레이 파티 게임입니다.

- **엔진·네트워크**: Unity 6, Netcode for GameObjects, Steam 릴레이 (호스트가 서버를 겸하는 구조)
- **팀**: 클라이언트 프로그래머 2명, 모델러 1명
- **담당**: 아이템·페인트·점수·게임 진행 등 게임플레이 시스템

## 🧩 폴더 구성

`Scripts/`는 팀 프로젝트의 스크립트 폴더 전체입니다.

| 폴더 | 내용 |
|---|---|
| `Item/`, `Item/Interface/` | 아이템·도구(테이저건·흡입기·청소 도구)·페인트·상자 배치, 아이템 동작 인터페이스 |
| `Player/` | 이동·상호작용·인벤토리·역할 배정 (래그돌·전투·행동 게이트는 팀원) |
| `Manager/` | 게임 진행·타이머·점수 |
| `RoomSetting/`, `Network/` | 로비·방 설정, 접속 승인 (Steam 초기화·초대 코드는 팀원) |
| `PolaroidCamera/`, `Trash/`, `Light/` | 폴라로이드 카메라, 쓰레기 데이터, 두꺼비집 |
| `Sound/` | 사운드 매니저, 표면별 발소리, UI 사운드 |
| `UI/` | 페인트·공용 게이지 (전적 화면은 팀원) |
| `AI/`, `Editor/` | ComfyUI 연동 에셋 생성 툴, 페인트 표면 ID 툴 (`RagdollTransplant`는 팀원) |
| `Generic/` | 싱글톤 베이스 클래스 |
| `Settings/`, `Chat/`, `Voice/`, `Save/` | 설정, 채팅·음성, 저장 (팀원) |
