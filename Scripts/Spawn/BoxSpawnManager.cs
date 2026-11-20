using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 맵 씬마다 하나 두는 배치 가능한 박스 매니저. 자식의 BoxSpawnArea들에 참여 인원 × 1명당 개수 × 방장이 정한 배율만큼 박스를 깐다.
/// 깐 박스는 ScoreManager가 상자 수를 셀 때 미배치 박스로 잡혀 시작 오염도에 들어간다.
/// </summary>
public class BoxSpawnManager : SpawnManagerBase<BoxSpawnManager, BoxSpawnArea>
{
    [Header("박스")]
    [SerializeField] private PlaceableBox boxPrefab;
    [Tooltip("배율 x1일 때 참여한 플레이어 1명당 깔리는 개수. 인원이 많아도 1명당 치울 양이 비슷하도록 인원에 비례한다")]
    [SerializeField, Min(0)] private int countPerPlayer = 5;

    /// <summary>
    /// 참여한 플레이어 수 × countPerPlayer × 방장이 정한 배율만큼 박스를 깐다 (서버 전용).
    /// 쓰레기보다 먼저 깔아야 쓰레기가 박스를 피해 놓인다.
    /// </summary>
    public void SpawnInitialBoxes(int playerCount, float amountMultiplier)
    {
        if (!NetworkManager.Singleton.IsServer) return;
        if (boxPrefab == null)
        {
            Debug.LogError("[BoxSpawnManager] boxPrefab이 설정되지 않았습니다!");
            return;
        }

        SpawnInAreas(playerCount * countPerPlayer, amountMultiplier);
    }

    protected override void SpawnAt(BoxSpawnArea area, Vector3 spot)
    {
        PlaceableBox box = Instantiate(boxPrefab, spot, RandomYaw());
        PlaceOnGround(box.gameObject, spot.y);
        box.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
    }
}
