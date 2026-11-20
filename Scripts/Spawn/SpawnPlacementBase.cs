using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 맵 씬마다 하나씩 두는 시작 오브젝트 배치 매니저의 바탕. 자식(그룹 아래 포함)의 영역에서 빈 바닥 자리를 찾아 SpawnAt에 넘긴다.
/// 몇 개를 어느 영역에 둘지는 상속한 매니저가 정한다 (박스·쓰레기는 SpawnManagerBase, 도구는 ToolSpawnManager).
/// </summary>
public abstract class SpawnPlacementBase<TSelf, TArea> : SceneSingleton<TSelf>
    where TSelf : SpawnPlacementBase<TSelf, TArea>
    where TArea : SpawnArea
{
    [Header("배치 판정")]
    [Tooltip("올려 둘 수 있는 바닥 레이어. 벽을 빼야 벽 윗면에 깔리지 않는다")]
    [SerializeField] private LayerMask groundMask = ~0;
    [Tooltip("겹치면 안 되는 레이어 (벽, 가구, 상자 등)")]
    [SerializeField] private LayerMask obstacleMask = ~0;
    [Tooltip("놓을 자리로 비워 둬야 하는 반경")]
    [SerializeField, Min(0f)] private float clearanceRadius = 0.2f;
    [Tooltip("이 매니저가 깐 것끼리의 최소 간격")]
    [SerializeField, Min(0f)] private float minSpacing = 0.8f;
    [Tooltip("이보다 기울어진 면에는 두지 않는다 (도)")]
    [SerializeField, Range(0f, 90f)] private float maxSlope = 30f;

    private const int MAX_ATTEMPTS_PER_SPAWN = 20;
    private const float GROUND_OFFSET = 0.02f; // 바닥에 파묻히지 않게 살짝 띄운다
    private const float CLEARANCE_OFFSET = 0.05f; // 빈자리 검사 구가 디딘 바닥 자체에 닿지 않게 띄운다
    private const float GROUND_SEARCH_MARGIN = 0.5f; // 영역 밑면을 바닥에 딱 맞춰 둬도 레이가 바닥에 닿도록 조금 더 내려 본다

    private TArea[] _areas;
    private readonly List<Vector3> _usedSpots = new();

    protected TArea[] Areas => _areas;

    protected override void Awake()
    {
        base.Awake();
        _areas = GetComponentsInChildren<TArea>();
    }

    /// <summary>바닥 지점 spot에 하나를 만들어 네트워크 스폰한다 (서버 전용)</summary>
    protected abstract void SpawnAt(TArea area, Vector3 spot);

    /// <summary>새로 깔기 전에 지난번에 쓴 자리 기록을 비운다</summary>
    protected void BeginSpawning() => _usedSpots.Clear();

    /// <summary>
    /// 다 깐 뒤 물리 위치를 맞추고 결과를 남긴다.
    /// 다음에 까는 매니저(박스 → 도구 → 쓰레기)가 방금 깐 것을 장애물로 보게 하기 위함
    /// </summary>
    protected void EndSpawning(int spawnedCount, int targetCount, string detail)
    {
        Physics.SyncTransforms();

        if (spawnedCount < targetCount)
        {
            Debug.LogWarning($"[{GetType().Name}] 빈자리를 못 찾아 {targetCount - spawnedCount}개를 깔지 못했습니다. 영역 크기나 minSpacing을 확인하세요.");
        }
        Debug.Log($"[{GetType().Name}] {spawnedCount}/{targetCount}개 배치 ({detail})");
    }

    /// <summary>영역 안에서 빈자리를 찾아 하나를 깐다. 자리를 못 찾으면 false</summary>
    protected bool TrySpawnIn(TArea area)
    {
        if (!TryFindSpot(area, out Vector3 spot)) return false;

        SpawnAt(area, spot);
        _usedSpots.Add(spot);
        return true;
    }

    protected static int[] GetShuffledIndices(int count)
    {
        int[] indices = Enumerable.Range(0, count).ToArray();
        for (int i = count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        return indices;
    }

    /// <summary>
    /// 영역 윗면에서 아래로 레이를 쏴 실제 바닥을 찾는다. 계단·가구 위처럼 높이가 다른 곳도 그대로 따라간다.
    /// </summary>
    private bool TryFindSpot(TArea area, out Vector3 spot)
    {
        float minGroundNormalY = Mathf.Cos(maxSlope * Mathf.Deg2Rad);
        for (int i = 0; i < MAX_ATTEMPTS_PER_SPAWN; i++)
        {
            Vector3 origin = area.GetRandomRayOrigin();
            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, area.RayLength + GROUND_SEARCH_MARGIN, groundMask, QueryTriggerInteraction.Ignore)) continue;
            if (hit.normal.y < minGroundNormalY) continue;
            if (IsNearUsedSpot(hit.point)) continue;

            // 레이 시작점이 벽·가구 안이면 그 콜라이더는 안 맞고 밑바닥이 잡히므로, 파묻힌 자리인지 따로 확인한다
            Vector3 clearanceCenter = hit.point + hit.normal * (clearanceRadius + CLEARANCE_OFFSET);
            if (Physics.CheckSphere(clearanceCenter, clearanceRadius, obstacleMask, QueryTriggerInteraction.Ignore)) continue;

            spot = hit.point;
            return true;
        }

        spot = default;
        return false;
    }

    private bool IsNearUsedSpot(Vector3 point)
    {
        float minSqr = minSpacing * minSpacing;
        foreach (Vector3 used in _usedSpots)
        {
            if ((used - point).sqrMagnitude < minSqr) return true;
        }
        return false;
    }

    protected static Quaternion RandomYaw() => Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

    /// <summary>
    /// 모델마다 피벗 위치가 달라서, 실제 모양의 밑면이 바닥에 닿도록 올린다.
    /// 공중에서 떨어뜨리면 시작하자마자 전부 굴러다니며 위치 동기화가 몰린다.
    /// </summary>
    protected static void PlaceOnGround(GameObject obj, float groundY)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;

        float bottom = renderers.Min(r => r.bounds.min.y);
        obj.transform.position += Vector3.up * (groundY - bottom + GROUND_OFFSET);
    }
}
