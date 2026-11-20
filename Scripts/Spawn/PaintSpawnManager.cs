using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 맵 씬마다 하나 두는 시작 페인트 배치 매니저. 자식의 PaintSpawnArea들에 참여 인원 × 1명당 면적 × 방장 배율만큼 페인트 패턴을 찍을 자리를 정한다.
/// 자리·모양·어느 표면에 묻는지까지 서버가 전부 정해 자국(PaintDab) 목록으로 만들고, InGameManager가 모든 클라이언트에 보내 각자 그린다.
/// (클라이언트마다 따로 계산하면 결과가 하나라도 어긋날 때 서버 점수와 화면이 달라진다)
/// 여기서 쓰는 면적은 얼마나 찍을지 정하는 어림값이고, 실제 점수는 그려진 뒤 서버가 페인트 마스크를 읽어서 센다.
/// </summary>
public class PaintSpawnManager : SceneSingleton<PaintSpawnManager>
{
    [Header("양")]
    [Tooltip("배율 x1일 때 참여한 플레이어 1명당 묻어 있는 페인트 면적 어림값(m²). 쓰레기·박스처럼 인원에 비례한다. 영역은 넓이 × weight에 비례해 자주 뽑힌다")]
    [SerializeField, Min(0f)] private float areaPerPlayer = 6f;

    [Header("패턴")]
    [Tooltip("나올 패턴과 비율. 영역에 패턴을 따로 지정하지 않으면 이 목록을 쓴다")]
    [SerializeField] private PaintPatternWeight[] patterns;

    [Header("배치 판정")]
    [Tooltip("페인트가 묻는 레이어. 페인트총의 paintableLayers와 같게 둔다")]
    [SerializeField] private LayerMask paintableMask = 6400; // Wall, Floor, PaintableItem
    [Tooltip("시야를 가리는 레이어. 이게 먼저 맞으면 가려진 자리로 보고 찍지 않는다. 쓰레기·박스(Item)는 빼서 그 밑에도 묻게 한다")]
    [SerializeField] private LayerMask blockingMask = 14657; // Default, Ground, Wall, Floor, PaintableItem, Stair
    [Tooltip("이보다 기울어진 면은 바닥으로 치지 않는다 (도)")]
    [SerializeField, Range(0f, 60f)] private float maxFloorSlope = 30f;
    [Tooltip("벽 패턴 중심을 찍는 높이 범위 (바닥에서 m). 걸레가 닿는 높이까지만")]
    [SerializeField] private Vector2 wallHeightRange = new(0.4f, 1.6f);
    [Tooltip("벽을 찾을 때 바닥 위에서 옆으로 쏘는 거리 (m)")]
    [SerializeField, Min(0.5f)] private float wallSearchDistance = 4f;
    [Tooltip("패턴 중심끼리의 최소 간격 (m)")]
    [SerializeField, Min(0f)] private float minSpacing = 1.5f;

    private const float SPLATTER_REACH = 1.8f;      // PaintBrush.shader와 맞출 것. 자국은 반경의 이 배수까지 방울이 튄다
    private const float SURFACE_LIFT = 0.05f;       // 가림 검사 선을 면에서 살짝 띄워 면 자체에 걸리지 않게 한다
    private const float CLEARANCE_RADIUS = 0.1f;    // 찾은 바닥 바로 위가 벽·가구 속(파묻힌 자리)인지 보는 구 반경
    private const float GROUND_SEARCH_MARGIN = 0.5f; // 영역 밑면을 바닥에 딱 맞춰 둬도 레이가 바닥에 닿도록 조금 더 내려 본다
    private const float MAX_WALL_NORMAL_Y = 0.3f;   // 노멀이 이보다 위아래로 기울면 벽으로 치지 않는다
    private const int MAX_ATTEMPTS_PER_PATTERN = 15;
    private const int MAX_FAILS_IN_A_ROW = 30;      // 연달아 이만큼 못 찍으면 더 찍을 자리가 없는 것으로 본다

    private PaintSpawnArea[] _areas;
    private readonly List<Vector3> _usedSpots = new();
    private readonly Collider[] _overlapBuffer = new Collider[16];
    private readonly HashSet<int> _dabSurfaces = new();

    protected override void Awake()
    {
        base.Awake();
        _areas = GetComponentsInChildren<PaintSpawnArea>();
    }

    /// <summary>
    /// 참여 인원 × 1명당 면적 × 방장 배율만큼 패턴을 찍을 자리를 정해 자국 목록으로 돌려준다 (서버 전용, 그리지는 않음).
    /// 영역을 넓이 × weight 비율로 하나씩 뽑아 패턴을 찍고, 찍은 어림 면적이 목표에 닿으면 멈춘다.
    /// </summary>
    public PaintDab[] GenerateInitialPaint(int playerCount, float amountMultiplier)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return Array.Empty<PaintDab>();

        float targetArea = playerCount * areaPerPlayer * amountMultiplier;
        if (targetArea <= 0f || _areas.Length == 0) return Array.Empty<PaintDab>();

        _usedSpots.Clear();
        var dabs = new List<PaintDab>();
        float totalWeight = _areas.Sum(area => area.SpawnWeight);
        float placedArea = 0f;
        int patternCount = 0;
        int failsInARow = 0;

        while (placedArea < targetArea && failsInARow < MAX_FAILS_IN_A_ROW)
        {
            PaintSpawnArea area = PickArea(totalWeight);
            PaintPattern pattern = PickPattern(area.Patterns ?? patterns);
            if (pattern == null)
            {
                Debug.LogWarning($"[PaintSpawnManager] {area.name}에 쓸 패턴이 없습니다. 매니저나 영역의 patterns를 채우세요.");
                break;
            }

            float scale = pattern.RandomScale();
            if (!TryStamp(area, pattern, scale, dabs))
            {
                failsInARow++;
                continue;
            }

            placedArea += pattern.EstimatedArea * scale * scale;
            patternCount++;
            failsInARow = 0;
        }

        if (placedArea < targetArea)
        {
            Debug.LogWarning($"[PaintSpawnManager] 찍을 자리를 못 찾아 목표 {targetArea:0.#}m² 중 {placedArea:0.#}m²만 찍었습니다. 영역 위치나 minSpacing을 확인하세요.");
        }
        Debug.Log($"[PaintSpawnManager] 패턴 {patternCount}개, 자국 {dabs.Count}개 (어림 {placedArea:0.#}/{targetArea:0.#}m² = {playerCount}명 × {areaPerPlayer}m² × 배율 x{amountMultiplier})");
        return dabs.ToArray();
    }

    /// <summary>넓이 × weight 비율로 영역 하나를 무작위로 고른다. 넓은 곳일수록 자주, 좁은 방은 가끔</summary>
    private PaintSpawnArea PickArea(float totalWeight)
    {
        if (totalWeight <= 0f) return _areas[Random.Range(0, _areas.Length)];

        float roll = Random.Range(0f, totalWeight);
        foreach (PaintSpawnArea area in _areas)
        {
            roll -= area.SpawnWeight;
            if (roll < 0f) return area;
        }
        return _areas[_areas.Length - 1];
    }

    /// <summary>weight 비율로 패턴 하나를 고른다. weight 합이 0이면 고르게 고른다</summary>
    private static PaintPattern PickPattern(PaintPatternWeight[] pool)
    {
        if (pool == null) return null;

        float totalWeight = 0f;
        int validCount = 0;
        foreach (PaintPatternWeight entry in pool)
        {
            if (entry.pattern == null) continue;
            totalWeight += entry.weight;
            validCount++;
        }
        if (validCount == 0) return null;

        if (totalWeight <= 0f)
        {
            int pick = Random.Range(0, validCount);
            foreach (PaintPatternWeight entry in pool)
            {
                if (entry.pattern == null) continue;
                if (pick-- == 0) return entry.pattern;
            }
        }

        float roll = Random.Range(0f, totalWeight);
        PaintPattern last = null;
        foreach (PaintPatternWeight entry in pool)
        {
            if (entry.pattern == null) continue;
            last = entry.pattern;
            roll -= entry.weight;
            if (roll < 0f) return entry.pattern;
        }
        return last;
    }

    /// <summary>영역 안에서 패턴을 찍을 자리를 찾아 자국을 목록에 더한다. 자국이 하나도 안 묻으면 실패</summary>
    private bool TryStamp(PaintSpawnArea area, PaintPattern pattern, float scale, List<PaintDab> dabs)
    {
        for (int attempt = 0; attempt < MAX_ATTEMPTS_PER_PATTERN; attempt++)
        {
            bool onWall = pattern.Placement == PaintPattern.SurfaceKind.Wall
                || (pattern.Placement == PaintPattern.SurfaceKind.Any && Random.value < 0.5f);

            if (!TryFindFloor(area, out RaycastHit hit)) continue;
            if (onWall)
            {
                if (!TryFindWall(hit.point, out hit)) continue;
            }
            else if (!IsPaintable(hit.collider)) continue;

            if (IsNearUsedSpot(hit.point)) continue;

            int before = dabs.Count;
            ExpandPattern(pattern, scale, hit.point, hit.normal, onWall, dabs);
            if (dabs.Count == before) continue;

            _usedSpots.Add(hit.point);
            return true;
        }
        return false;
    }

    /// <summary>영역 윗면에서 아래로 레이를 쏴 실제 바닥을 찾는다. 바닥이 페인트가 안 묻는 곳이어도 벽 패턴의 기준 높이로는 쓴다</summary>
    private bool TryFindFloor(PaintSpawnArea area, out RaycastHit hit)
    {
        Vector3 origin = area.GetRandomRayOrigin();
        if (!Physics.Raycast(origin, Vector3.down, out hit, area.RayLength + GROUND_SEARCH_MARGIN, blockingMask, QueryTriggerInteraction.Ignore)) return false;
        if (hit.normal.y < Mathf.Cos(maxFloorSlope * Mathf.Deg2Rad)) return false;

        // 레이 시작점이 벽·가구 안이면 그 콜라이더는 안 맞고 밑바닥이 잡히므로, 파묻힌 자리인지 따로 확인한다
        Vector3 clearanceCenter = hit.point + hit.normal * (CLEARANCE_RADIUS + SURFACE_LIFT);
        return !Physics.CheckSphere(clearanceCenter, CLEARANCE_RADIUS, blockingMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>바닥 위 무작위 높이에서 무작위 수평 방향으로 레이를 쏴 칠할 수 있는 벽을 찾는다</summary>
    private bool TryFindWall(Vector3 floorPoint, out RaycastHit hit)
    {
        hit = default;
        Vector2 flat = Random.insideUnitCircle;
        if (flat.sqrMagnitude < 0.01f) return false;

        Vector3 origin = floorPoint + Vector3.up * Random.Range(wallHeightRange.x, wallHeightRange.y);
        Vector3 direction = new Vector3(flat.x, 0f, flat.y).normalized;
        if (!Physics.Raycast(origin, direction, out hit, wallSearchDistance, blockingMask, QueryTriggerInteraction.Ignore)) return false;
        if (Mathf.Abs(hit.normal.y) > MAX_WALL_NORMAL_Y) return false;
        return IsPaintable(hit.collider);
    }

    /// <summary>
    /// 패턴의 자국들을 면 위에 펼치고, 자국마다 닿는 표면을 찾아 표면별 자국으로 더한다.
    /// 바닥 타일 경계나 모서리에 걸친 자국은 닿는 표면 모두에 들어간다.
    /// 패턴 중심에서 자국까지 가려진 것이 있으면(벽 너머 옆방, 바닥 아래로 빠진 흘러내림) 그 자국은 뺀다.
    /// </summary>
    private void ExpandPattern(PaintPattern pattern, float scale, Vector3 point, Vector3 normal, bool onWall, List<PaintDab> dabs)
    {
        Vector3 up = onWall && pattern.UprightOnWall
            ? Vector3.ProjectOnPlane(Vector3.up, normal)
            : Vector3.ProjectOnPlane(Random.onUnitSphere, normal);
        if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(Vector3.forward, normal);
        if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(Vector3.right, normal);
        up.Normalize();

        // 면을 바라봤을 때의 오른쪽
        Vector3 right = Vector3.Cross(normal, up);
        if (pattern.AllowMirror && Random.value < 0.5f) right = -right;

        Vector3 lineStart = point + normal * SURFACE_LIFT;
        foreach (PaintPattern.Dab dab in pattern.Dabs)
        {
            Vector3 center = point + (right * dab.offset.x + up * dab.offset.y) * scale;
            float radius = dab.radius * scale;

            if (Physics.Linecast(lineStart, center + normal * SURFACE_LIFT, blockingMask, QueryTriggerInteraction.Ignore)) continue;

            int count = Physics.OverlapSphereNonAlloc(center, radius * SPLATTER_REACH, _overlapBuffer, paintableMask, QueryTriggerInteraction.Ignore);
            _dabSurfaces.Clear();
            for (int i = 0; i < count; i++)
            {
                if (!_overlapBuffer[i].TryGetComponent(out PaintableSurface surface)) continue;
                if (!_dabSurfaces.Add(surface.SurfaceId)) continue;
                dabs.Add(new PaintDab(surface.SurfaceId, center, radius, normal));
            }
        }
    }

    private static bool IsPaintable(Collider collider) => collider != null && collider.TryGetComponent(out PaintableSurface _);

    private bool IsNearUsedSpot(Vector3 point)
    {
        float minSqr = minSpacing * minSpacing;
        foreach (Vector3 used in _usedSpots)
        {
            if ((used - point).sqrMagnitude < minSqr) return true;
        }
        return false;
    }
}
