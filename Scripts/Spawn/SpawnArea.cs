using UnityEngine;

/// <summary>
/// 게임 시작 시 오브젝트(쓰레기, 박스)가 깔리는 영역. 각 매니저의 자식(그룹 아래도 됨)에 두고 박스 크기로 범위를 정한다.
/// 콜라이더를 쓰지 않는다: 잡기(SphereCastAll)·다이브(OverlapSphere) 판정이 트리거도 맞추기 때문.
/// </summary>
public abstract class SpawnArea : MonoBehaviour
{
    [Tooltip("영역 크기 (로컬 기준). x·z는 바닥 면적, y는 위에서 아래로 바닥을 찾는 높이 범위")]
    [SerializeField] private Vector3 size = new(4f, 3f, 4f);

    [Tooltip("같은 넓이 대비 비중. 2면 같은 넓이의 다른 영역보다 두 배 많이 깔린다")]
    [SerializeField, Min(0f)] private float weight = 1f;

    /// <summary>씬 뷰에 영역을 그리는 색. 종류마다 달리해서 겹쳐 있어도 구분한다</summary>
    protected abstract Color GizmoColor { get; }

    private Vector3 WorldSize => Vector3.Scale(size, transform.lossyScale);

    /// <summary>바닥 면적 × 비중. 영역별로 고르게 나눌 때 매니저가 기본 개수를 이 값에 비례해 나눈다</summary>
    public float SpawnWeight => WorldSize.x * WorldSize.z * weight;

    /// <summary>넓이를 뺀 비중만. 하나씩 무작위로 영역을 고를 때 쓴다</summary>
    public float Weight => weight;

    /// <summary>바닥을 찾을 때 아래로 쏘는 거리 (영역 윗면 → 아랫면)</summary>
    public float RayLength => WorldSize.y;

    /// <summary>영역 윗면의 무작위 지점. 여기서 아래로 레이를 쏴 실제 바닥을 찾는다</summary>
    public Vector3 GetRandomRayOrigin()
    {
        Vector3 local = new(
            Random.Range(-0.5f, 0.5f) * size.x,
            size.y * 0.5f,
            Random.Range(-0.5f, 0.5f) * size.z);
        return transform.TransformPoint(local);
    }

    protected virtual void OnDrawGizmos()
    {
        Color color = GizmoColor;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(color.r, color.g, color.b, 0.15f);
        Gizmos.DrawCube(Vector3.zero, size);
        Gizmos.color = new Color(color.r, color.g, color.b, 0.9f);
        Gizmos.DrawWireCube(Vector3.zero, size);
    }
}
