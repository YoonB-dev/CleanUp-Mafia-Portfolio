using UnityEngine;

/// <summary>
/// 게임 시작 시 페인트가 묻는 영역. PaintSpawnManager의 자식(그룹 아래도 됨)에 둔다.
/// 영역 윗면에서 아래로 바닥을 찾고, 벽 패턴은 그 바닥 위에서 옆으로 벽을 찾으므로 박스 밑면은 바닥 높이에 맞춘다.
/// 벽은 영역 밖이어도 옆으로 닿는 거리(wallSearchDistance) 안이면 칠해진다.
/// </summary>
public class PaintSpawnArea : SpawnArea
{
    [Tooltip("이 영역에서 나올 패턴과 비율. 비워 두면 PaintSpawnManager의 목록을 쓴다")]
    [SerializeField] private PaintPatternWeight[] patterns;

    protected override Color GizmoColor => new(0.9f, 0.15f, 0.25f);

    /// <summary>이 영역 전용 패턴 목록. 비어 있으면 null</summary>
    public PaintPatternWeight[] Patterns => patterns != null && patterns.Length > 0 ? patterns : null;
}
