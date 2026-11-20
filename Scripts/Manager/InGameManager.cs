using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class InGameManager : NetworkBehaviour
{
    public static InGameManager Instance { get; private set; }

    [Header("씬 설정")]
    [SerializeField] private string playSceneName = "InGameScene"; // 인게임 씬 이름과 정확히 맞출 것
    [SerializeField] private string lobbySceneName = "LobbyScene";
    [Tooltip("이 씬 위에 Additive로 불러올 맵 목록. 대기방에서 고른 맵(없으면 무작위)을 불러온다. 비워 두면 이 씬 자체를 맵으로 쓴다 (맵까지 한 씬에 넣은 단일 씬 방식)")]
    [SerializeField] private MapCatalog mapCatalog;
    private string _mapSceneName; // 이번 판에 고른 맵
    private bool _returningToLobby;   // 결과 화면의 버튼을 여러 번 눌러도 한 번만 돌아간다
    private const float MAP_UNLOAD_TIMEOUT = 10f;

    [Header("스폰 설정")]
    [SerializeField] private GameObject inGamePlayerPrefab; // 실제 조종할 인게임 캐릭터 프리팹
    private readonly HashSet<ulong> loadedClients = new(); // 씬 로드 완료를 보고한 클라이언트 ID를 저장하는 HashSet. 전원이 모이면 캐릭터를 스폰한다
    private bool _hasSpawnedPlayers; // 일괄 스폰은 한 번만. 스폰 후 이탈 시 재검사로 전원이 중복 스폰되는 것을 막는다

    [Header("승패 설정")]
    [Tooltip("제한 시간 종료 시 청소 상태(1 - 오염도 비율)가 이 값 이상이면 시민 승리, 못 미치면 마피아 승리. 바꾸면 ScoreCanvas의 화살표(PointImage)도 게이지의 같은 위치로 옮길 것")]
    [SerializeField, Range(0f, 1f)] private float citizenWinCleanRatio = 0.8f;
    public float CitizenWinCleanRatio => citizenWinCleanRatio;   // 작업 지시서 목표 문구가 읽는다

    [Header("게임 종료 무대 (맵의 MapInfo에 무대가 있을 때만)")]
    [Tooltip("게임이 끝나고 첫 사람이 떨어질 때까지(초)")]
    [SerializeField] private float podiumStartDelay = 0.8f;
    [Tooltip("한 사람씩 떨어지는 간격(초)")]
    [SerializeField] private float podiumDropInterval = 0.35f;
    [Tooltip("이긴 팀을 서는 자리보다 이만큼(m) 위에서 떨어뜨린다. 카메라 화면 밖에서 떨어지게")]
    [SerializeField] private float winnerDropHeight = 12f;
    [Tooltip("진 팀은 이긴 팀보다 높이서 떨어뜨린다")]
    [SerializeField] private float loserDropHeight = 16f;
    [Tooltip("다 떨어진 뒤 결과 창이 뜰 때까지(초)")]
    [SerializeField] private float podiumResultHold = 1.5f;

    [Header("시작 페인트")]
    [Tooltip("시작 페인트가 다 그려지고 면적을 다 셀 때까지 기다리는 최대 시간(초). 넘으면 그때까지 센 면적으로 오염도 기준을 정한다")]
    [SerializeField, Min(1f)] private float initialPaintTimeout = 10f;

    private const int PAINT_DABS_PER_RPC = 64; // 자국 하나 32바이트. RPC 하나를 2KB 정도로 나눠 보낸다

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer && mapCatalog != null && mapCatalog.maps.Length > 0)
        {
            string selected = RoomSettings.Instance != null ? RoomSettings.Instance.MapSceneName.Value.ToString() : null;
            _mapSceneName = mapCatalog.Resolve(selected);
            Debug.Log($"[InGameManager] 이번 판 맵: {_mapSceneName}");

            // 맵의 조명·스카이박스가 적용되도록 클라이언트도 서버의 Active Scene을 따라가게 한다
            NetworkManager.SceneManager.ActiveSceneSynchronizationEnabled = true;
            NetworkManager.SceneManager.OnLoadEventCompleted += OnMapLoadEventCompleted;
            if (GameTimerManager.Instance != null) GameTimerManager.Instance.OnTimerExpired += EndGame;
            StartCoroutine(LoadMapWhenReady());
            return;
        }

        if (IsServer)
        {
            // 1. 나중에 로드를 완료할 클라이언트들을 위해 이벤트를 등록합니다.
            NetworkManager.Singleton.SceneManager.OnSceneEvent += ServerOnSceneEvent;
            // 로딩 도중 이탈하면 남은 인원 기준으로 다시 검사해야 대기 상태에 갇히지 않는다
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
            if (GameTimerManager.Instance != null) GameTimerManager.Instance.OnTimerExpired += EndGame;

            // 2.호스트를 씬 완료 상태로 간주.
            loadedClients.Add(NetworkManager.ServerClientId);

            // 3. 모든 클라이언트가 씬 로드를 완료했는지 확인하고, 완료했다면 스폰을 진행.
            CheckAndSpawnAllPlayers();
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.SceneManager.OnSceneEvent -= ServerOnSceneEvent;
            NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= OnMapLoadEventCompleted;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
        }

        if (IsServer && GameTimerManager.Instance != null) GameTimerManager.Instance.OnTimerExpired -= EndGame;
    }

    /// <summary>
    /// 인게임 씬 로드 이벤트가 끝나야 다음 씬 이벤트를 시작할 수 있어서, 시작될 때까지 매 프레임 다시 요청한다.
    /// </summary>
    private IEnumerator LoadMapWhenReady()
    {
        while (true)
        {
            SceneEventProgressStatus status = NetworkManager.SceneManager.LoadScene(_mapSceneName, LoadSceneMode.Additive);
            if (status == SceneEventProgressStatus.Started) yield break;

            if (status != SceneEventProgressStatus.SceneEventInProgress)
            {
                Debug.LogError($"[InGameManager] 맵 씬 로드 실패: {_mapSceneName} ({status})");
                yield break;
            }
            yield return null;
        }
    }

    /// <summary>
    /// 전원이 맵 로드를 마치면 맵을 Active Scene으로 바꾸고 일괄 스폰한다.
    /// </summary>
    private void OnMapLoadEventCompleted(string sceneName, LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        if (sceneName != _mapSceneName || _hasSpawnedPlayers) return;

        _hasSpawnedPlayers = true;
        NetworkManager.SceneManager.OnLoadEventCompleted -= OnMapLoadEventCompleted;
        if (clientsTimedOut.Count > 0) Debug.LogWarning($"[InGameManager] 맵 로드 시간 초과 클라이언트: {string.Join(", ", clientsTimedOut)}");

        // 플레이어 등 이후 생성되는 오브젝트가 맵 씬에 속하도록 먼저 바꾼다
        SceneManager.SetActiveScene(SceneManager.GetSceneByName(_mapSceneName));

        // 쓰레기도 맵 씬에 속해야 하므로 Active Scene을 바꾼 뒤에 깐다
        SetupInitialContamination(clientsCompleted.Count);

        for (int i = 0; i < clientsCompleted.Count; i++)
        {
            SpawnPlayerCharacter(clientsCompleted[i], i);
        }
        StartTimer();
    }
    /// <summary>
    /// 로딩 도중 혹은 로딩 완료 후에 클라이언트가 나갔을 때 실행되는 콜백 함수
    /// </summary>
    private void OnClientDisconnect(ulong clientId)
    {
        if (!IsServer) return;

        Debug.Log($"[InGameManager] 클라이언트 {clientId}번의 연결 해제 감지.");

        // 1. 로딩 완료 목록에 해당 클라이언트가 있다면 지워줍니다.
        if (loadedClients.Contains(clientId))
        {
            loadedClients.Remove(clientId);
            Debug.Log($"[InGameManager] 클라이언트 {clientId}번을 로딩 완료 목록에서 제거했습니다.");
        }

        // 2. 누군가 나갔으므로 목표 인원수나 상태가 변했을 수 있으니, 스폰이 가능한지 다시 검사합니다.
        CheckAndSpawnAllPlayers();
    }

    private void ServerOnSceneEvent(SceneEvent sceneEvent)
    {
        if (!IsServer) return;

        // 인게임 씬이 완전히 로드되었을 때만 감지합니다.
        if (sceneEvent.SceneEventType == SceneEventType.LoadComplete && sceneEvent.SceneName == playSceneName)
        {
            ulong clientId = sceneEvent.ClientId;
            Debug.Log($"[InGameManager] 클라이언트 {clientId}번의 인게임 씬 로딩 완료 감지.");
            // 완료된 클라이언트 목록에 추가
            if (!loadedClients.Contains(clientId))
            {
                loadedClients.Add(clientId);
            }

            // 다 됐는지 검사
            CheckAndSpawnAllPlayers();
        }
    }

    /// <summary>
    /// 로비에 있던 인원 전체가 인게임 씬을 로드했는지 확인하고, 
    /// 모두가 완료되었다면 모든 플레이어 캐릭터를 동시에 일괄 스폰합니다.
    /// </summary>
    private void CheckAndSpawnAllPlayers()
    {
        if (!IsServer || _hasSpawnedPlayers) return;

        // 1. 방 설정(RoomSettings)에 등록된 전체 인원 수 가져오기
        if (RoomSettings.Instance == null)
        {
            Debug.LogWarning("[InGameManager] RoomSettings 인스턴스를 찾을 수 없습니다.");
            return;
        }

        int targetPlayerCount = RoomSettings.Instance.AllPlayers.Count;
        int currentLoadedCount = loadedClients.Count;

        Debug.Log($"[InGameManager] 로딩 체크: ({currentLoadedCount} / {targetPlayerCount})");

        // 2. 전체 인원이 아직 다 로드되지 않았다면 대기합니다.
        if (currentLoadedCount < targetPlayerCount)
        {
            Debug.Log("[InGameManager] 아직 모든 플레이어가 로딩되지 않았습니다. 대기 중...");
            return;
        }

        // 3. 한 명도 유실 없이 메시지를 받을 준비가 되었으므로 모두 한 번에 스폰합니다.
        Debug.Log("[InGameManager] 모든 플레이어 로딩 완료! 일괄 스폰을 시작합니다.");

        // 더 이상 중복 로딩 감지 및 스폰 처리가 일어나는 것을 막기 위해 이벤트를 꺼줍니다.
        _hasSpawnedPlayers = true;
        NetworkManager.Singleton.SceneManager.OnSceneEvent -= ServerOnSceneEvent;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;

        SetupInitialContamination(loadedClients.Count);

        // 목록에 쌓인 모두를 스폰해 줍니다.
        int spawnIndex = 0;
        foreach (ulong clientId in loadedClients)
        {
            SpawnPlayerCharacter(clientId, spawnIndex++);
        }
        // 4. 모든 플레이어 스폰이 끝났다면, 이제 타이머를 시작한다.
        StartTimer();
    }

    /// <summary>
    /// 방장이 정한 양만큼 맵에 박스·쓰레기·페인트를 깔고, 그 상태를 시작 오염도로 삼아 점수 기준을 정한다.
    /// 깔린 쓰레기 점수는 TrashSpawnManager가 ScoreManager에 넘긴다. 도구도 여기서 같이 깔아 서로 겹치지 않게 한다.
    /// </summary>
    /// <param name="playerCount">이번 판에 스폰할 플레이어 수. 도구는 종류마다 이만큼, 쓰레기·박스·페인트는 1명당 정해진 양만큼 깐다</param>
    private void SetupInitialContamination(int playerCount)
    {
        RoomSettings settings = RoomSettings.Instance;

        // 박스를 먼저 깔아야 쓰레기가 박스를 장애물로 보고 피해 놓인다
        if (BoxSpawnManager.Instance != null)
        {
            BoxSpawnManager.Instance.SpawnInitialBoxes(playerCount, settings != null ? settings.BoxAmountMultiplier.Value : 1f);
        }
        // 도구는 종류마다 참여한 사람 수만큼. 박스 다음에 깔아 박스를 피하고, 쓰레기는 도구를 피해 놓인다
        if (ToolSpawnManager.Instance != null)
        {
            ToolSpawnManager.Instance.SpawnInitialTools(playerCount, settings != null ? settings.ToolAmountMultiplier.Value : 1f);
        }
        if (TrashSpawnManager.Instance != null)
        {
            TrashSpawnManager.Instance.SpawnInitialTrash(playerCount, settings != null ? settings.TrashAmountMultiplier.Value : 1f);
        }

        PaintDab[] paint = PaintSpawnManager.Instance != null
            ? PaintSpawnManager.Instance.GenerateInitialPaint(playerCount, settings != null ? settings.PaintAmountMultiplier.Value : 1f)
            : System.Array.Empty<PaintDab>();

        // 상자 수와 오염도 100% 기준은 맵·쓰레기·페인트가 다 올라온 뒤에야 정할 수 있다.
        // 페인트 면적은 그려진 뒤 마스크를 읽어야(비동기) 나오므로 기다렸다가 정한다
        StartCoroutine(ApplyInitialPaintThenInitializeScore(paint));
    }

    /// <summary>
    /// 시작 페인트 자국 목록을 모든 클라이언트에 나눠 보내고, 서버(호스트) 화면에 다 그려져 칠해진 면적까지 센 뒤 오염도 기준을 정한다 (서버 전용).
    /// </summary>
    private IEnumerator ApplyInitialPaintThenInitializeScore(PaintDab[] dabs)
    {
        PaintSurfaceManager paintManager = PaintSurfaceManager.Instance;
        if (dabs.Length > 0 && paintManager != null)
        {
            int processedBefore = paintManager.ProcessedDabCount;
            for (int start = 0; start < dabs.Length; start += PAINT_DABS_PER_RPC)
            {
                int count = Mathf.Min(PAINT_DABS_PER_RPC, dabs.Length - start);
                var chunk = new PaintDab[count];
                System.Array.Copy(dabs, start, chunk, 0, count);
                ApplyInitialPaintClientRpc(chunk);
            }

            float deadline = Time.realtimeSinceStartup + initialPaintTimeout;
            while (paintManager != null && Time.realtimeSinceStartup < deadline)
            {
                if (paintManager.ProcessedDabCount - processedBefore >= dabs.Length && paintManager.IsIdle) break;
                yield return null;
            }
            if (Time.realtimeSinceStartup >= deadline)
            {
                Debug.LogWarning($"[InGameManager] 시작 페인트 반영이 {initialPaintTimeout}초 안에 끝나지 않아 지금까지 센 면적으로 오염도 기준을 정합니다.");
            }
        }

        if (ScoreManager.Instance != null) ScoreManager.Instance.InitializeMapScore();
    }

    [ClientRpc]
    private void ApplyInitialPaintClientRpc(PaintDab[] dabs)
    {
        if (PaintSurfaceManager.Instance != null) PaintSurfaceManager.Instance.EnqueueDabs(dabs);
    }

    private void SpawnPlayerCharacter(ulong clientId, int spawnIndex)
    {
        // 맵 씬에 MapInfo가 있으면 지정된 스폰 지점을 쓴다
        if (MapInfo.Instance != null)
        {
            Pose pose = MapInfo.Instance.GetSpawnPose(spawnIndex);
            GameObject mapPlayerObj = Instantiate(inGamePlayerPrefab, pose.position, pose.rotation);
            mapPlayerObj.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId, destroyWithScene: true);
            return;
        }

        // 1. 생성하기 전에 매니저로부터 랜덤 좌표를 먼저 받아옵니다.
        Vector3 randomSpawnPos = Vector3.zero;
        if (PlayerSpawnManager.Instance != null)
        {
            randomSpawnPos = PlayerSpawnManager.Instance.GetRandomSpawnPosition();
        }

        // 2. 애초에 태어날 때부터 랜덤 위치에서 스폰되도록 만듭니다.
        GameObject playerObj = Instantiate(inGamePlayerPrefab, randomSpawnPos, Quaternion.identity);

        NetworkObject netObj = playerObj.GetComponent<NetworkObject>();
        netObj.SpawnAsPlayerObject(clientId, destroyWithScene: true);
    }

    // 타이머 
    private void StartTimer()
    {
        if (GameTimerManager.Instance != null)
        {
            int totalSeconds = RoomSettings.Instance.PlayTimeMinutes.Value;

            GameTimerManager.Instance.StartTimer(5, totalSeconds);
            Debug.Log($"[InGameManager] GameTimerManager에 타이머 구동 명령 전달: {totalSeconds}초");
        }
        else
        {
            Debug.LogError("[InGameManager] 씬에 GameTimerManager 인스턴스가 존재하지 않습니다!");
        }
    }

    private readonly List<string> _departedMafia = new(); // [서버] 판 도중 나간 마피아 이름

    /// <summary>[서버] 판 도중 나간 마피아 이름을 남긴다. 결과 화면에 "(나감)"으로 보인다</summary>
    public void RecordDepartedMafia(string displayName)
    {
        if (IsServer && !_departedMafia.Contains(displayName)) _departedMafia.Add(displayName);
    }

    private void EndGame()
    {
        ScoreManager score = ScoreManager.Instance;
        float max = score.MaxContaminationScore;

        // 결과 화면에 보여 줄 마피아 명단. 남아 있는 사람은 clientId와 함께, 나간 사람은 이름만
        var ids = new List<ulong>();
        var names = new List<FixedString64Bytes>();
        foreach (RoleManager role in FindObjectsByType<RoleManager>(FindObjectsSortMode.None))
        {
            if (!role.IsSpawned || role.CurrentRole != PlayerRole.Mafia) continue;
            ids.Add(role.OwnerClientId);
            names.Add(new FixedString64Bytes(role.TryGetComponent(out PlayerData data) ? data.GetDisplayName() : $"Player {role.OwnerClientId}"));
        }
        foreach (string departed in _departedMafia)
        {
            ids.Add(ulong.MaxValue);
            names.Add(new FixedString64Bytes(departed));
        }

        float trash = score.TrashContamination / max, box = score.BoxContamination / max, paint = score.PaintContamination / max;
        float resultDelay = StartPodium(WinnerOf(trash, box, paint, out _));
        GameOverClientRpc(trash, box, paint, ids.ToArray(), names.ToArray(), resultDelay);
    }

    // 청소 상태 = 1 - 오염도. 마피아가 100% 넘게 더럽혀도 0 아래로는 안 내려간다
    private PlayerRole WinnerOf(float trash, float box, float paint, out float cleanRatio)
    {
        cleanRatio = Mathf.Clamp01(1f - (trash + box + paint));
        return cleanRatio >= citizenWinCleanRatio ? PlayerRole.Citizen : PlayerRole.Mafia;
    }

    /// <summary>[서버] 맵에 무대가 있으면 이긴 팀은 무대 위로, 진 팀은 무대 앞에 쓰러지게 떨어뜨린다. 결과 창을 띄울 때까지 걸리는 시간을 돌려준다</summary>
    private float StartPodium(PlayerRole winner)
    {
        MapInfo map = MapInfo.Instance;
        if (map == null || !map.HasPodium) return 0f;

        var winners = new List<NetworkObject>();
        var losers = new List<NetworkObject>();
        foreach (RoleManager role in FindObjectsByType<RoleManager>(FindObjectsSortMode.None))
        {
            if (!role.IsSpawned) continue;
            (role.CurrentRole == winner ? winners : losers).Add(role.NetworkObject);
        }

        StartCoroutine(PodiumRoutine(map, winners, losers));
        return podiumStartDelay + (winners.Count + losers.Count) * podiumDropInterval + podiumResultHold;
    }

    private IEnumerator PodiumRoutine(MapInfo map, List<NetworkObject> winners, List<NetworkObject> losers)
    {
        var wait = new WaitForSeconds(podiumDropInterval);
        yield return new WaitForSeconds(podiumStartDelay);

        // 이긴 팀은 선 채로 무대 위에 뚝뚝
        for (int i = 0; i < winners.Count; i++)
        {
            if (winners[i] != null && winners[i].TryGetComponent(out PlayerMovement movement))
            {
                Pose pose = map.GetWinnerPose(i);
                winners[i].GetComponent<PlayerInteraction>().ServerEmptyHands();
                movement.ServerTeleport(pose.position + Vector3.up * winnerDropHeight, pose.rotation.eulerAngles.y);
            }
            yield return wait;
        }

        // 진 팀은 무대 앞에 래그돌로 픽픽. 다시 일어나지 않는다
        foreach (NetworkObject loser in losers)
        {
            if (loser != null && loser.TryGetComponent(out PlayerMovement movement) && loser.TryGetComponent(out PlayerKnockdown knockdown))
            {
                Pose pose = map.GetLoserPose();
                loser.GetComponent<PlayerInteraction>().ServerEmptyHands();
                movement.ServerTeleport(pose.position + Vector3.up * loserDropHeight, pose.rotation.eulerAngles.y);
                knockdown.ServerKnockdown(Random.insideUnitSphere * 1.5f + Vector3.down * 2f, Random.insideUnitSphere * 6f, stayDown: true);
            }
            yield return wait;
        }
    }

    public void ReturnToLobby()
    {
        if (!IsServer || _returningToLobby) return;

        _returningToLobby = true;
        StartCoroutine(ReturnToLobbyRoutine());
    }

    /// <summary>
    /// 맵 씬은 Additive로 따로 불러온 데다 Active Scene이라, 그대로 대기방을 Single로 불러오면 NGO가 인게임 씬을 비동기로 내리는 것과
    /// 대기방 로드가 겹쳐 방장 에디터가 멈췄다. 그래서 인게임 씬을 Active로 되돌리고 맵을 먼저 다 내린 뒤 대기방을 불러온다.
    /// (처음 대기방 → 인게임으로 갈 때처럼 Single 로드가 바꾸는 씬이 하나뿐인 상태로 만든다)
    /// </summary>
    private IEnumerator ReturnToLobbyRoutine()
    {
        Scene map = string.IsNullOrEmpty(_mapSceneName) ? default : SceneManager.GetSceneByName(_mapSceneName);
        if (map.IsValid() && map.isLoaded)
        {
            NetworkManager.SceneManager.ActiveSceneSynchronizationEnabled = false;
            SceneManager.SetActiveScene(gameObject.scene);

            bool unloaded = false;
            void OnUnloaded(string sceneName, LoadSceneMode mode, List<ulong> completed, List<ulong> timedOut)
            {
                if (sceneName == _mapSceneName) unloaded = true;
            }
            NetworkManager.SceneManager.OnUnloadEventCompleted += OnUnloaded;

            SceneEventProgressStatus unload;
            while ((unload = NetworkManager.SceneManager.UnloadScene(map)) == SceneEventProgressStatus.SceneEventInProgress)
            {
                yield return null;
            }

            float deadline = Time.realtimeSinceStartup + MAP_UNLOAD_TIMEOUT;
            while (unload == SceneEventProgressStatus.Started && !unloaded && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            NetworkManager.SceneManager.OnUnloadEventCompleted -= OnUnloaded;
        }

        SceneEventProgressStatus status;
        while ((status = NetworkManager.SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single)) == SceneEventProgressStatus.SceneEventInProgress)
        {
            yield return null;
        }
    }

    [ClientRpc]
    private void GameOverClientRpc(float trash, float box, float paint, ulong[] mafiaIds, FixedString64Bytes[] mafiaNames, float resultDelay)
    {
        PlayerRole winner = WinnerOf(trash, box, paint, out float cleanRatio);
        Debug.Log($"[InGameManager] 게임 종료. 청소 상태 {cleanRatio:P0}, 승리: {winner}");

        if (VoteUIController.Instance != null) VoteUIController.Instance.Close();

        NetworkObject player = LocalPlayerInput.Local;
        if (player != null)
        {
            PlayerActionGate.GetOrAdd(player.gameObject).SetUIOpen(true);
            RecordResult(player, winner);
        }

        if (MapInfo.Instance != null)
        {
            FirstPersonLook look = player != null ? player.GetComponent<FirstPersonLook>() : null;
            MapInfo.Instance.ShowPodium(look != null ? look.PlayerCameraTransform.GetComponent<Camera>() : null);
        }
        StartCoroutine(ShowResultAfter(resultDelay, winner, cleanRatio, trash, box, paint, mafiaIds, mafiaNames));
    }

    // 무대에 다 떨어지는 걸 보여 준 뒤 결과 창을 띄운다
    private IEnumerator ShowResultAfter(float delay, PlayerRole winner, float cleanRatio, float trash, float box, float paint, ulong[] mafiaIds, FixedString64Bytes[] mafiaNames)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);

        InGameUIController.Instance?.ShowResult(winner, cleanRatio, trash, box, paint, citizenWinCleanRatio);
        InGameUIController.Instance?.ShowMafiaReveal(mafiaIds, mafiaNames);
    }

    private static void RecordResult(NetworkObject player, PlayerRole winner)
    {
        PlayStats stats = PlayerSave.Data.stats;
        stats.totalGamesPlayed++;
        player.GetComponent<PlayerData>().AddStatsTo(stats);

        if (player.GetComponent<RoleManager>().CurrentRole == PlayerRole.Mafia)
        {
            stats.mafiaPlayed++;
            if (winner == PlayerRole.Mafia) stats.mafiaWins++;
        }
        else
        {
            stats.citizenPlayed++;
            if (winner == PlayerRole.Citizen) stats.citizenWins++;
        }

        PlayerSave.Save();
    }
}
