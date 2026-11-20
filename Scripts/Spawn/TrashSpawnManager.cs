using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 맵 씬마다 하나 두는 쓰레기 배치 매니저. 자식의 TrashSpawnArea들에 방장이 정한 양만큼 쓰레기를 깐다.
/// 깐 쓰레기 점수 합계는 ScoreManager에 넘겨 시작 오염도로 쓴다.
/// </summary>
public class TrashSpawnManager : SpawnManagerBase<TrashSpawnManager, TrashSpawnArea>
{
    [Header("쓰레기")]
    [Tooltip("영역에 쓰레기 종류를 지정하지 않으면 이 프리팹에 등록된 모든 종류가 나온다")]
    [SerializeField] private TrashObject trashPrefab;
    [Tooltip("배율 x1일 때 참여한 플레이어 1명당 깔리는 개수")]
    [SerializeField, Min(0)] private int countPerPlayer = 20;

    private const int DEFAULT_TRASH_SCORE = 10; // TrashData가 없는 쓰레기는 ScoreManager·쓰레기통과 같이 10점으로 센다

    private int _totalScore;

    /// <summary>
    /// 참여한 플레이어 수 × countPerPlayer × 방장이 정한 배율만큼 쓰레기를 깔고, 깐 쓰레기 점수 합계를 ScoreManager에 알린다 (서버 전용).
    /// </summary>
    public void SpawnInitialTrash(int playerCount, float amountMultiplier)
    {
        if (!NetworkManager.Singleton.IsServer) return;
        if (trashPrefab == null)
        {
            Debug.LogError("[TrashSpawnManager] trashPrefab이 설정되지 않았습니다!");
            return;
        }

        _totalScore = 0;
        SpawnInAreas(playerCount * countPerPlayer, amountMultiplier);
        Debug.Log($"[TrashSpawnManager] 쓰레기 점수 합계: {_totalScore}");

        if (ScoreManager.Instance != null) ScoreManager.Instance.SetTotalTrashScore(_totalScore);
    }

    protected override void SpawnAt(TrashSpawnArea area, Vector3 spot)
    {
        TrashObject trash = Instantiate(trashPrefab, spot, RandomYaw());
        // 스폰 전에 종류를 정해야 스폰 메시지에 같이 실린다
        trash.ServerInitialize(area.GetRandomTrashData(trashPrefab.TrashDatabase));
        PlaceOnGround(trash.gameObject, spot.y);
        trash.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);

        _totalScore += trash.Data != null ? trash.Data.score : DEFAULT_TRASH_SCORE;
    }
}
