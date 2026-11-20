using UnityEngine;

/// <summary>
/// 게임 시작 시 도구(대걸레, 카메라, 흡입기, 테이저건 등)가 깔리는 영역. ToolSpawnManager의 자식(그룹 아래도 됨)에 둔다.
/// weight는 같은 판에서 이 영역이 다른 영역보다 몇 배 더 받을지를 뜻한다 (넓이는 보지 않는다).
/// </summary>
public class ToolSpawnArea : SpawnArea
{
    protected override Color GizmoColor => new(0.3f, 0.6f, 1f);
}
