using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class RoomSettings : NetworkBehaviour
{
    public static RoomSettings Instance { get; private set; }
    public readonly NetworkList<RoomPlayerInfo> AllPlayers = new(
        null,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server // 데이터 변조를 막기 위해 쓰기 권한은 서버(호스트)만 가집니다.
    );
    private const int MIN_PLAYER_COUNT = 3;
    private const int MAX_PLAYER_COUNT = 12;
    private const int MIN_MAFIA_COUNT = 1;
    private const int MIN_PLAY_TIME = 300; // 5분
    private const int MAX_PLAY_TIME = 1800; // 30분
    private const int PLAY_TIME_STEP = 30; // 30초 단위로 증가/감소
    private const float MIN_CONTAMINATION_MULTIPLIER = 0.5f;
    private const float MAX_CONTAMINATION_MULTIPLIER = 3f;
    private const float CONTAMINATION_MULTIPLIER_STEP = 0.25f; // x0.25 단위로 증가/감소
    private const float MIN_AMOUNT_MULTIPLIER = 0.25f;
    private const float MAX_AMOUNT_MULTIPLIER = 2f;
    private const float MIN_TOOL_AMOUNT_MULTIPLIER = 1f; // 도구는 종류마다 인원수만큼은 꼭 깔리므로 1배 아래로는 못 내린다
    private const float MAX_TOOL_AMOUNT_MULTIPLIER = 3f;
    private const float AMOUNT_MULTIPLIER_STEP = 0.25f; // x0.25 단위로 증가/감소

    public NetworkVariable<int> PlayerCount = new(MIN_PLAYER_COUNT);
    public NetworkVariable<int> MafiaCount = new(MIN_MAFIA_COUNT);
    public NetworkVariable<int> PlayTimeMinutes = new(MIN_PLAY_TIME);

    // ============ 오염도 관련 세팅 =========== 배율 (1.0 = 기본)
    public NetworkVariable<float> TrashContaminationMultiplier = new(1f); // 버리는 쓰레기
    public NetworkVariable<float> BoxContaminationMultiplier = new(1f);   // 정리 안 된 상자
    public NetworkVariable<float> PaintContaminationMultiplier = new(1f); // 페인트

    // ============ 시작 오염량 세팅 =========== 배율 (1.0 = 맵에 정해진 기본 양)
    public NetworkVariable<float> TrashAmountMultiplier = new(1f); // 게임 시작 시 맵에 깔리는 쓰레기 양
    public NetworkVariable<float> BoxAmountMultiplier = new(1f);   // 게임 시작 시 맵에 깔리는 배치 가능한 박스 양
    public NetworkVariable<float> PaintAmountMultiplier = new(1f); // 게임 시작 시 맵에 묻어 있는 페인트 양
    public NetworkVariable<float> ToolAmountMultiplier = new(1f);  // 게임 시작 시 맵에 깔리는 도구 양 (1.0 = 종류마다 인원수만큼)

    // 작업 현장(맵) 씬 이름. 비어 있으면 무작위 (MapCatalog.Resolve)
    public NetworkVariable<FixedString64Bytes> MapSceneName = new("");

    // 호스트만 아는 값이라 참가자도 친구를 부를 수 있게 공유한다
    public NetworkVariable<FixedString64Bytes> RoomCode = new("");
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
        if (IsServer)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += ServerOnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += ServerOnClientDisconnected;

            if (NetworkConnect.Instance != null)
            {
                NetworkConnect.Instance.MaxPlayers = PlayerCount.Value;
                RoomCode.Value = NetworkConnect.Instance.RoomCode ?? string.Empty;
            }

            // 호스트(서버 본인) 최초 등록
            RegisterPlayer(NetworkManager.ServerClientId, $"Player {NetworkManager.ServerClientId}");

            // 방장을 넘겨받아 방을 다시 연 경우 이전 방 설정을 이어받는다
            if (HostMigration.Instance != null) HostMigration.Instance.ApplyInheritedSettings(this);
        }
        PlayerCount.OnValueChanged += OnVariableChanged;
        MafiaCount.OnValueChanged += OnVariableChanged;
        PlayTimeMinutes.OnValueChanged += OnVariableChanged;
        TrashContaminationMultiplier.OnValueChanged += OnMultiplierChanged;
        BoxContaminationMultiplier.OnValueChanged += OnMultiplierChanged;
        PaintContaminationMultiplier.OnValueChanged += OnMultiplierChanged;
        TrashAmountMultiplier.OnValueChanged += OnMultiplierChanged;
        BoxAmountMultiplier.OnValueChanged += OnMultiplierChanged;
        PaintAmountMultiplier.OnValueChanged += OnMultiplierChanged;
        ToolAmountMultiplier.OnValueChanged += OnMultiplierChanged;
        RoomCode.OnValueChanged += OnTextChanged;
        MapSceneName.OnValueChanged += OnTextChanged;

        // 참여자 목록(등록/해제/Ready 변경 등)이 바뀔 때마다 UI 갱신
        AllPlayers.OnListChanged += OnAllPlayersChanged;

        RoomUIController.Instance?.Refresh();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= ServerOnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= ServerOnClientDisconnected;
        }

        PlayerCount.OnValueChanged -= OnVariableChanged;
        MafiaCount.OnValueChanged -= OnVariableChanged;
        PlayTimeMinutes.OnValueChanged -= OnVariableChanged;
        TrashContaminationMultiplier.OnValueChanged -= OnMultiplierChanged;
        BoxContaminationMultiplier.OnValueChanged -= OnMultiplierChanged;
        PaintContaminationMultiplier.OnValueChanged -= OnMultiplierChanged;
        TrashAmountMultiplier.OnValueChanged -= OnMultiplierChanged;
        BoxAmountMultiplier.OnValueChanged -= OnMultiplierChanged;
        PaintAmountMultiplier.OnValueChanged -= OnMultiplierChanged;
        ToolAmountMultiplier.OnValueChanged -= OnMultiplierChanged;
        RoomCode.OnValueChanged -= OnTextChanged;
        MapSceneName.OnValueChanged -= OnTextChanged;
        AllPlayers.OnListChanged -= OnAllPlayersChanged;
    }

    // 서버에서 안전하게 클라이언트 접속을 확인한 후 호출됨
    private void ServerOnClientConnected(ulong clientId)
    {
        // 호스트 본인은 이미 스폰 때 등록했으므로 패스
        if (clientId == NetworkManager.ServerClientId) return;

        string playerName = $"Player {clientId}";
        RegisterPlayer(clientId, playerName);
    }

    private void ServerOnClientDisconnected(ulong clientId)
    {
        RemovePlayer(clientId);
    }

    private void OnVariableChanged(int previousValue, int newValue)
    {
        // 서버(호스트) 시점에서만 NetworkConnect의 MaxPlayers를 갱신
        if (IsServer && NetworkConnect.Instance != null)
        {
            NetworkConnect.Instance.MaxPlayers = PlayerCount.Value;
        }

        RoomUIController.Instance?.Refresh();
    }

    private void OnMultiplierChanged(float previousValue, float newValue)
    {
        RoomUIController.Instance?.Refresh();
    }

    private void OnTextChanged(FixedString64Bytes previousValue, FixedString64Bytes newValue)
    {
        RoomUIController.Instance?.Refresh();
    }

    private void OnAllPlayersChanged(NetworkListEvent<RoomPlayerInfo> changeEvent)
    {
        Debug.Log($"[RoomSettings] AllPlayers.OnListChanged: Type={changeEvent.Type}, ClientId={changeEvent.Value.ClientId}");
        RoomUIController.Instance?.Refresh();
    }

    // Ready 상태 관리
    // ===== 참여자 등록 / 해제 / Ready 상태 관리 =====
    // 서버(호스트)만 AllPlayers를 수정할 수 있습니다. PlayerReady 컴포넌트에서 호출됩니다.

    public void RegisterPlayer(ulong clientId, string playerName)
    {
        if (!IsServer) return;

        for (int i = 0; i < AllPlayers.Count; i++)
        {
            if (AllPlayers[i].ClientId == clientId) return; // 이미 등록되어 있음
        }


        AllPlayers.Add(new RoomPlayerInfo
        {
            ClientId = clientId,
            PlayerName = playerName,
            IsReady = (clientId == NetworkManager.ServerClientId) // 호스트는 기본적으로 Ready 상태
        });
    }

    public void RemovePlayer(ulong clientId)
    {
        if (!IsServer) return;

        for (int i = 0; i < AllPlayers.Count; i++)
        {
            if (AllPlayers[i].ClientId == clientId)
            {
                AllPlayers.RemoveAt(i);
                return;
            }
        }
    }

    public void SetPlayerReady(ulong clientId, bool isReady)
    {
        if (!IsServer) return;

        for (int i = 0; i < AllPlayers.Count; i++)
        {
            if (AllPlayers[i].ClientId == clientId)
            {
                RoomPlayerInfo info = AllPlayers[i];
                info.IsReady = isReady;
                AllPlayers[i] = info; // 인덱서 대입 -> NetworkList가 변경으로 인식하고 동기화 + OnListChanged 발생
                return;
            }
        }
    }

    public void SetPlayerName(ulong clientId, FixedString128Bytes playerName)
    {
        if (!IsServer) return;

        for (int i = 0; i < AllPlayers.Count; i++)
        {
            if (AllPlayers[i].ClientId == clientId)
            {
                RoomPlayerInfo info = AllPlayers[i];
                info.PlayerName = playerName;
                AllPlayers[i] = info;
                return;
            }
        }
    }

    public bool TryGetPlayerReady(ulong clientId, out bool isReady)
    {
        for (int i = 0; i < AllPlayers.Count; i++)
        {
            if (AllPlayers[i].ClientId == clientId)
            {
                isReady = AllPlayers[i].IsReady;
                return true;
            }
        }

        isReady = false;
        return false;
    }


    // ===== 플레이어 수 =====
    public void PlayerCountUp()
    {
        if (!IsServer || PlayerCount.Value >= MAX_PLAYER_COUNT) return;
        PlayerCount.Value++;
    }

    public void PlayerCountDown()
    {
        if (!IsServer || PlayerCount.Value <= MIN_PLAYER_COUNT) return;
        // 현재 접속 인원보다 방 정원을 줄일 수 없도록 안전장치
        int absoluteMin = Mathf.Max(MIN_PLAYER_COUNT, AllPlayers.Count);
        if (PlayerCount.Value <= absoluteMin)
        {
            Debug.LogWarning($"[RoomSettings] 현재 접속 인원({AllPlayers.Count}명) 미만으로 방 정원을 줄일 수 없습니다.");
            return;
        }

        int newCount = PlayerCount.Value - 1;

        // 플레이어 수가 줄어서 마피아 수 상한(절반)을 초과하면 마피아 수도 같이 줄임
        int maxMafia = (newCount - 1) / 2;
        if (MafiaCount.Value > maxMafia)
        {
            MafiaCount.Value = Mathf.Max(MIN_MAFIA_COUNT, maxMafia);
        }

        PlayerCount.Value = newCount;
    }

    /// <summary>방장이 바뀌어도 이어 쓸 방 설정 값</summary>
    public struct Snapshot
    {
        public int PlayerCount, MafiaCount, PlayTime;
        public float TrashContamination, BoxContamination, PaintContamination;
        public float TrashAmount, BoxAmount, PaintAmount, ToolAmount;
        public FixedString64Bytes MapSceneName;
    }

    public Snapshot Capture() => new()
    {
        PlayerCount = PlayerCount.Value,
        MafiaCount = MafiaCount.Value,
        PlayTime = PlayTimeMinutes.Value,
        TrashContamination = TrashContaminationMultiplier.Value,
        BoxContamination = BoxContaminationMultiplier.Value,
        PaintContamination = PaintContaminationMultiplier.Value,
        TrashAmount = TrashAmountMultiplier.Value,
        BoxAmount = BoxAmountMultiplier.Value,
        PaintAmount = PaintAmountMultiplier.Value,
        ToolAmount = ToolAmountMultiplier.Value,
        MapSceneName = MapSceneName.Value,
    };

    /// <summary>[서버 전용] 저장해 둔 방 설정을 적용한다. 범위 검사는 각 setter가 한다</summary>
    public void Apply(Snapshot snapshot)
    {
        if (!IsServer) return;

        SetPlayerCount(snapshot.PlayerCount);
        SetMafiaCount(snapshot.MafiaCount);
        SetPlayTimeMinutes(snapshot.PlayTime);
        SetTrashMultiplier(snapshot.TrashContamination);
        SetBoxMultiplier(snapshot.BoxContamination);
        SetPaintMultiplier(snapshot.PaintContamination);
        SetTrashAmount(snapshot.TrashAmount);
        SetBoxAmount(snapshot.BoxAmount);
        SetPaintAmount(snapshot.PaintAmount);
        SetToolAmount(snapshot.ToolAmount);
        SetMap(snapshot.MapSceneName.ToString());
    }

    // ===== 작업 현장(맵). 빈 문자열이면 무작위 =====
    public void SetMap(string sceneName)
    {
        if (!IsServer) return;
        MapSceneName.Value = sceneName ?? string.Empty;
    }

    // ===== 플레이어 수 직접 입력 =====
    public void SetPlayerCount(int value)
    {
        if (!IsServer) return;

        int absoluteMin = Mathf.Max(MIN_PLAYER_COUNT, AllPlayers.Count);
        value = Mathf.Clamp(value, absoluteMin, MAX_PLAYER_COUNT);

        int maxMafia = (value - 1) / 2;
        if (MafiaCount.Value > maxMafia)
        {
            MafiaCount.Value = Mathf.Max(MIN_MAFIA_COUNT, maxMafia);
        }

        PlayerCount.Value = value;
    }

    // ===== 마피아 수 (플레이어 수의 절반을 넘을 수 없음) =====
    public void MafiaCountUp()
    {
        if (!IsServer) return;
        int maxMafia = (PlayerCount.Value - 1) / 2;
        if (MafiaCount.Value >= maxMafia) return;
        MafiaCount.Value++;
    }

    public void MafiaCountDown()
    {
        if (!IsServer || MafiaCount.Value <= MIN_MAFIA_COUNT) return;
        MafiaCount.Value--;
    }

    // ===== 마피아 수 직접 입력 =====
    public void SetMafiaCount(int value)
    {
        if (!IsServer) return;

        int maxMafia = Mathf.Max(MIN_MAFIA_COUNT, (PlayerCount.Value - 1) / 2);
        value = Mathf.Clamp(value, MIN_MAFIA_COUNT, maxMafia);

        MafiaCount.Value = value;
    }

    // ===== 플레이 시간 =====
    public void PlayTimeUp()
    {
        if (!IsServer || PlayTimeMinutes.Value >= MAX_PLAY_TIME) return;
        PlayTimeMinutes.Value += PLAY_TIME_STEP;
    }

    public void PlayTimeDown()
    {
        if (!IsServer || PlayTimeMinutes.Value <= MIN_PLAY_TIME) return;
        PlayTimeMinutes.Value-= PLAY_TIME_STEP;
    }

    // ===== 플레이 시간 직접 입력 =====
    public void SetPlayTimeMinutes(int value)
    {
        if (!IsServer) return;

        value = Mathf.Clamp(value, MIN_PLAY_TIME, MAX_PLAY_TIME);

        // 30초 단위로 스냅
        int steps = Mathf.RoundToInt((value - MIN_PLAY_TIME) / (float)PLAY_TIME_STEP);
        value = MIN_PLAY_TIME + steps * PLAY_TIME_STEP;

        PlayTimeMinutes.Value = value;
    }

    // ===== 오염도 배율 =====
    public void TrashMultiplierUp() => StepMultiplier(TrashContaminationMultiplier, +1);
    public void TrashMultiplierDown() => StepMultiplier(TrashContaminationMultiplier, -1);
    public void SetTrashMultiplier(float value) => SetMultiplier(TrashContaminationMultiplier, value);

    public void BoxMultiplierUp() => StepMultiplier(BoxContaminationMultiplier, +1);
    public void BoxMultiplierDown() => StepMultiplier(BoxContaminationMultiplier, -1);
    public void SetBoxMultiplier(float value) => SetMultiplier(BoxContaminationMultiplier, value);

    public void PaintMultiplierUp() => StepMultiplier(PaintContaminationMultiplier, +1);
    public void PaintMultiplierDown() => StepMultiplier(PaintContaminationMultiplier, -1);
    public void SetPaintMultiplier(float value) => SetMultiplier(PaintContaminationMultiplier, value);

    private void StepMultiplier(NetworkVariable<float> multiplier, int direction)
    {
        SetMultiplier(multiplier, multiplier.Value + direction * CONTAMINATION_MULTIPLIER_STEP);
    }

    private void SetMultiplier(NetworkVariable<float> multiplier, float value)
    {
        SetSnappedValue(multiplier, value, MIN_CONTAMINATION_MULTIPLIER, MAX_CONTAMINATION_MULTIPLIER, CONTAMINATION_MULTIPLIER_STEP);
    }

    // ===== 시작 오염량 배율 =====
    public void TrashAmountUp() => StepAmount(TrashAmountMultiplier, +1);
    public void TrashAmountDown() => StepAmount(TrashAmountMultiplier, -1);
    public void SetTrashAmount(float value) => SetAmount(TrashAmountMultiplier, value);

    public void BoxAmountUp() => StepAmount(BoxAmountMultiplier, +1);
    public void BoxAmountDown() => StepAmount(BoxAmountMultiplier, -1);
    public void SetBoxAmount(float value) => SetAmount(BoxAmountMultiplier, value);

    public void PaintAmountUp() => StepAmount(PaintAmountMultiplier, +1);
    public void PaintAmountDown() => StepAmount(PaintAmountMultiplier, -1);
    public void SetPaintAmount(float value) => SetAmount(PaintAmountMultiplier, value);

    public void ToolAmountUp() => SetToolAmount(ToolAmountMultiplier.Value + AMOUNT_MULTIPLIER_STEP);
    public void ToolAmountDown() => SetToolAmount(ToolAmountMultiplier.Value - AMOUNT_MULTIPLIER_STEP);
    public void SetToolAmount(float value) =>
        SetSnappedValue(ToolAmountMultiplier, value, MIN_TOOL_AMOUNT_MULTIPLIER, MAX_TOOL_AMOUNT_MULTIPLIER, AMOUNT_MULTIPLIER_STEP);

    private void StepAmount(NetworkVariable<float> amount, int direction)
    {
        SetAmount(amount, amount.Value + direction * AMOUNT_MULTIPLIER_STEP);
    }

    private void SetAmount(NetworkVariable<float> amount, float value)
    {
        SetSnappedValue(amount, value, MIN_AMOUNT_MULTIPLIER, MAX_AMOUNT_MULTIPLIER, AMOUNT_MULTIPLIER_STEP);
    }

    private void SetSnappedValue(NetworkVariable<float> variable, float value, float min, float max, float step)
    {
        if (!IsServer) return;

        value = Mathf.Clamp(value, min, max);

        // step 단위로 스냅 (float 덧셈이 쌓여 1.2499999 같은 값이 되는 것을 방지)
        value = Mathf.Round(value / step) * step;

        variable.Value = value;
    }
}