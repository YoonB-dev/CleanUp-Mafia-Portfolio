using System.Linq;
using UnityEngine;

/// <summary>
/// 방장이 정한 양만큼 까는 시작 오브젝트 배치 매니저의 공통 부분 (박스, 쓰레기).
/// 자식(그룹 아래 포함)의 영역들에 기본 개수(매니저마다 정함) × 방장 배율만큼 나눠 주고, 자리 찾기는 SpawnPlacementBase, 실제로 무엇을 만들지는 SpawnAt에 맡긴다.
/// </summary>
public abstract class SpawnManagerBase<TSelf, TArea> : SpawnPlacementBase<TSelf, TArea>
    where TSelf : SpawnManagerBase<TSelf, TArea>
    where TArea : SpawnArea
{
    private enum Distribution
    {
        EvenByArea,     // 영역마다 1개씩 먼저 주고 나머지를 넓이 × weight 비율로 나눈다. 넓은 곳일수록 많다 (쓰레기)
        RandomByWeight, // 하나씩 weight 비율로 무작위 영역을 고른다. 넓이는 무시하고, 어떤 영역은 비고 어떤 영역은 여러 개가 된다 (박스)
    }

    [Header("양")]
    [Tooltip("EvenByArea: 영역마다 1개씩 먼저, 나머지는 넓이 × weight 비율 (쓰레기)\nRandomByWeight: 하나씩 weight 비율로 무작위 영역 선택, 넓이 무시 (박스)")]
    [SerializeField] private Distribution distribution = Distribution.EvenByArea;

    private const int MAX_AREA_PICKS = 5; // 무작위로 고른 영역에 자리가 없으면 다른 영역을 다시 고르는 횟수

    /// <summary>
    /// 기본 개수 × 배율만큼 distribution 방식대로 영역에 나눠 깐다 (서버 전용).
    /// 맵 씬이 Active Scene이 된 뒤에 불러야 깐 오브젝트가 맵 씬에 속해 맵과 함께 정리된다.
    /// </summary>
    protected void SpawnInAreas(int baseCount, float amountMultiplier)
    {
        BeginSpawning();
        TArea[] areas = Areas;
        int targetCount;
        int spawnedCount = 0;

        if (distribution == Distribution.EvenByArea)
        {
            int[] counts = DistributeToAreas(baseCount, amountMultiplier);
            targetCount = counts.Sum();
            for (int i = 0; i < areas.Length; i++)
            {
                for (int n = 0; n < counts[i]; n++)
                {
                    if (TrySpawnIn(areas[i])) spawnedCount++;
                }
            }
        }
        else
        {
            targetCount = areas.Length > 0 ? Mathf.RoundToInt(baseCount * amountMultiplier) : 0;
            for (int n = 0; n < targetCount; n++)
            {
                for (int pick = 0; pick < MAX_AREA_PICKS; pick++)
                {
                    if (!TrySpawnIn(PickAreaByWeight())) continue;
                    spawnedCount++;
                    break;
                }
            }
        }

        EndSpawning(spawnedCount, targetCount, $"기본 {baseCount}개 × 배율 x{amountMultiplier}");
    }

    /// <summary>weight 비율로 영역 하나를 무작위로 고른다. 넓이는 보지 않는다</summary>
    private TArea PickAreaByWeight()
    {
        TArea[] areas = Areas;
        float totalWeight = areas.Sum(area => area.Weight);
        if (totalWeight <= 0f) return areas[Random.Range(0, areas.Length)];

        float roll = Random.Range(0f, totalWeight);
        foreach (TArea area in areas)
        {
            roll -= area.Weight;
            if (roll < 0f) return area;
        }
        return areas[areas.Length - 1];
    }

    /// <summary>
    /// 영역마다 1개씩 먼저 주고, 나머지는 넓이 × weight 비율로 나눈 뒤 배율을 곱한다.
    /// 내림한 몫을 먼저 주고, 소수점 아래를 모은 만큼은 소수점이 큰 영역부터 하나씩 더 줘서 합계를 기본 개수 × 배율에 맞춘다 (최대잉여법).
    /// </summary>
    private int[] DistributeToAreas(int baseCount, float amountMultiplier)
    {
        TArea[] areas = Areas;
        int areaCount = areas.Length;
        int[] counts = new int[areaCount];
        if (areaCount == 0) return counts;

        // 영역이 기본 개수보다 많으면 1개씩 먼저 줄 수 없으니 전부 비율로만 나눈다
        float perArea = baseCount >= areaCount ? 1f : 0f;
        float rest = baseCount - perArea * areaCount;
        float totalWeight = areas.Sum(area => area.SpawnWeight);

        float[] remainders = new float[areaCount];
        float expectedTotal = 0f;
        int assigned = 0;
        for (int i = 0; i < areaCount; i++)
        {
            float share = totalWeight > 0f ? areas[i].SpawnWeight / totalWeight : 1f / areaCount;
            float expected = (perArea + rest * share) * amountMultiplier;
            counts[i] = Mathf.FloorToInt(expected);
            remainders[i] = expected - counts[i];
            expectedTotal += expected;
            assigned += counts[i];
        }

        // 소수점이 같은 영역끼리는 무작위 순서로 골라, 판마다 같은 영역만 비지 않게 한다
        int[] order = GetShuffledIndices(areaCount);
        for (int left = Mathf.RoundToInt(expectedTotal) - assigned; left > 0; left--)
        {
            int best = order[0];
            foreach (int i in order)
            {
                if (remainders[i] > remainders[best]) best = i;
            }
            counts[best]++;
            remainders[best] = -1f; // 한 영역에 두 번 주지 않는다
        }
        return counts;
    }
}
