using UnityEngine;

/// <summary>
/// 게임 시작 시 쓰레기가 깔리는 영역. TrashSpawnManager의 자식(그룹 아래도 됨)에 둔다.
/// </summary>
public class TrashSpawnArea : SpawnArea
{
    [Tooltip("이 영역에서 나올 쓰레기 종류. 비워 두면 쓰레기 프리팹에 등록된 모든 종류가 나온다")]
    [SerializeField] private TrashData[] trashPool;

    protected override Color GizmoColor => new(0.4f, 0.9f, 0.3f);

    /// <summary>이 영역의 쓰레기 종류 중 하나. 지정이 없으면 fallback(모든 종류)에서 고른다</summary>
    public TrashData GetRandomTrashData(TrashData[] fallback)
    {
        TrashData[] pool = trashPool != null && trashPool.Length > 0 ? trashPool : fallback;
        if (pool == null || pool.Length == 0) return null;
        return pool[Random.Range(0, pool.Length)];
    }
}
