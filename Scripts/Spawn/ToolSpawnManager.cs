using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 맵 씬마다 하나 두는 도구 배치 매니저. 등록된 도구를 종류마다 참여한 플레이어 수만큼(+방장이 정한 배율만큼 무작위 추가) 만들어 자식의 ToolSpawnArea들에 고르게 흩어 둔다.
/// 한 영역에 몰리지 않도록 덜 받은 영역부터, 같은 종류가 아직 없는 영역부터 채운다.
/// </summary>
public class ToolSpawnManager : SpawnPlacementBase<ToolSpawnManager, ToolSpawnArea>
{
    [Header("도구")]
    [Tooltip("판마다 종류별로 참여한 플레이어 수만큼 깔 도구 프리팹 (대걸레, 카메라, 흡입기, 테이저건 등). DefaultNetworkPrefabs에 등록돼 있어야 한다")]
    [SerializeField] private PickupItem[] toolPrefabs;

    private PickupItem _prefabToSpawn; // TrySpawnIn → SpawnAt으로 이번에 만들 종류를 넘긴다

    /// <summary>
    /// 도구를 깐다 (서버 전용). 박스 다음, 쓰레기 전에 깔아야 박스를 피하고 쓰레기가 도구를 피한다.
    /// 종류마다 playerCount개씩은 꼭 깔고, 방장이 정한 배율(1 이상)로 늘어난 만큼은 종류를 무작위로 골라 더 깐다.
    /// 예: 3명, 4종, x1.5 → 기본 12개 + 추가 6개(종류 무작위) = 18개
    /// </summary>
    public void SpawnInitialTools(int playerCount, float amountMultiplier)
    {
        if (!NetworkManager.Singleton.IsServer) return;
        int[] validTypes = toolPrefabs == null
            ? new int[0]
            : Enumerable.Range(0, toolPrefabs.Length).Where(i => toolPrefabs[i] != null).ToArray();
        if (validTypes.Length == 0)
        {
            Debug.LogError("[ToolSpawnManager] toolPrefabs가 비어 있습니다!");
            return;
        }

        int baseCount = playerCount * validTypes.Length;
        int extraCount = Mathf.Max(0, Mathf.RoundToInt(baseCount * amountMultiplier) - baseCount);

        // 깔 순서: 종류를 한 바퀴씩 돌며 하나씩 (자리가 모자랄 때도 한 종류만 빠지지 않게), 그다음 추가분은 종류 무작위
        var queue = new List<int>(baseCount + extraCount);
        for (int round = 0; round < playerCount; round++)
        {
            foreach (int i in GetShuffledIndices(validTypes.Length)) queue.Add(validTypes[i]);
        }
        for (int n = 0; n < extraCount; n++)
        {
            queue.Add(validTypes[Random.Range(0, validTypes.Length)]);
        }

        BeginSpawning();
        ToolSpawnArea[] areas = Areas;
        int[] totals = new int[areas.Length];                       // 영역별로 받은 도구 수
        int[,] typeCounts = new int[areas.Length, toolPrefabs.Length]; // 영역별·종류별로 받은 수
        int spawnedCount = 0;

        foreach (int type in queue)
        {
            _prefabToSpawn = toolPrefabs[type];
            foreach (int a in GetAreasByPreference(type, totals, typeCounts))
            {
                if (!TrySpawnIn(areas[a])) continue;
                totals[a]++;
                typeCounts[a, type]++;
                spawnedCount++;
                break;
            }
        }

        EndSpawning(spawnedCount, queue.Count,
            $"플레이어 {playerCount}명 × {validTypes.Length}종 = {baseCount}개 + 배율 x{amountMultiplier} 추가 {extraCount}개");
    }

    /// <summary>
    /// 이번 종류를 둘 영역 순서. 이 종류를 덜 받은 영역 → 받은 도구 수 / weight가 적은 영역 순이고, 같으면 무작위.
    /// weight가 0인 영역은 쓰지 않는다
    /// </summary>
    private int[] GetAreasByPreference(int type, int[] totals, int[,] typeCounts)
    {
        ToolSpawnArea[] areas = Areas;

        // 먼저 섞고 안정 정렬(OrderBy)해야 조건이 같은 영역끼리는 무작위 순서가 된다
        return GetShuffledIndices(areas.Length)
            .Where(a => areas[a].Weight > 0f)
            .OrderBy(a => typeCounts[a, type])
            .ThenBy(a => totals[a] / areas[a].Weight)
            .ToArray();
    }

    protected override void SpawnAt(ToolSpawnArea area, Vector3 spot)
    {
        PickupItem tool = Instantiate(_prefabToSpawn, spot, RandomYaw() * _prefabToSpawn.transform.rotation);
        PlaceOnGround(tool.gameObject, spot.y);
        tool.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
    }
}
