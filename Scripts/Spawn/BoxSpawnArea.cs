using UnityEngine;

/// <summary>
/// 게임 시작 시 배치 가능한 박스가 깔리는 영역. BoxSpawnManager의 자식(그룹 아래도 됨)에 둔다.
/// </summary>
public class BoxSpawnArea : SpawnArea
{
    protected override Color GizmoColor => new(1f, 0.6f, 0.2f);
}
