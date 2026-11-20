using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Components;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

public class InGameUIController : NetworkBehaviour
{
    public static InGameUIController Instance { get; private set; }

    [SerializeField] private TMP_Text gameTimerTxt; 
    [SerializeField] private GameObject jobDisplayCanvas; // 전체 캔버스 -> 시작하자마자 띄워서 일관성 유지
    [SerializeField] private GameObject jobDisplayPanel; // 직업 결정되면 동작 -> 애니메이션으로 띄워짐
    [SerializeField] private Image roleAccent;
    [SerializeField] private Color citizenColor = new(0.180f, 0.420f, 0.320f);
    [SerializeField] private Color mafiaColor = new(0.871f, 0.482f, 0.157f);
    [SerializeField] private TMP_Text jobNotificationText;

    [Header("로컬라이제이션 string 연결")]
    public LocalizedString jobNotificationString; 
    [SerializeField] private LocalizedString citizenDesString;
    [SerializeField] private LocalizedString mafiaDesString;
    [SerializeField] private TMP_Text jobDescriptionText;

    [Header("결과")]
    [SerializeField] private GameObject resultPanel;
    [Tooltip("결과 창 뒤를 어둡게 덮는 판. 게임 종료 무대가 있으면 무대가 보이게 끈다")]
    [SerializeField] private GameObject resultBackdrop;
    [SerializeField] private LocalizeStringEvent winnerText;
    [SerializeField] private LocalizeStringEvent contaminationText;
    [SerializeField] private LocalizeStringEvent breakdownText;
    [SerializeField] private GameObject returnButton;
    [SerializeField] private GameObject waitHostText;
    [Tooltip("승리 문구 아래에 \"이번 판의 마피아: 이름, 이름\" 줄을 넣을 때 늘리는 결과 패널 높이")]
    [SerializeField] private float mafiaRevealHeight = 64f;
    private object[] _argsBuffer = new object[1]; // 한번만 생성하고 재활용하기 위한 버퍼
    private RoleManager _localRole;
    private bool _revealLock;               // 직업 안내 중이라 조작을 막아야 하는지
    private PlayerActionGate _revealGate;   // 그래서 막아 둔 내 플레이어

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        jobDisplayCanvas.SetActive(true);
        jobDisplayPanel.SetActive(false);
    }

    public override void OnNetworkSpawn()
    {
        if (GameTimerManager.Instance != null)
        {
            // 넷변수의 값이 변경될 때마다 텍스트 변환 함수를 호출하도록 리스너 등록
            GameTimerManager.Instance.RemainingTime.OnValueChanged += ConvertAndShowTime;
            GameTimerManager.Instance.CurrentState.OnValueChanged += OnTimerStateChanged;

            
            OnTimerStateChanged(TimerState.None, GameTimerManager.Instance.CurrentState.Value);

            // 첫 진입 시 UI에 현재 초기 시간 강제 표현 (예: 05:00)
            ConvertAndShowTime(0, GameTimerManager.Instance.RemainingTime.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        if (GameTimerManager.Instance != null)
        {
            GameTimerManager.Instance.CurrentState.OnValueChanged -= OnTimerStateChanged;
            GameTimerManager.Instance.RemainingTime.OnValueChanged -= ConvertAndShowTime;
        }

        if (_localRole != null) _localRole.RoleChanged -= OnLocalRoleChanged;
    }

    /// <summary>
    /// 타이머의 상태(Ready -> Playing)가 바뀔 때 호출되는 콜백
    /// </summary>
    private void OnTimerStateChanged(TimerState previousState, TimerState newState)
    {
        switch (newState)
        {
            case TimerState.Ready:
                // 5초 대기 시작 시점에 직업 패널을 키고 애니메이션 적용.
                jobDisplayPanel.SetActive(true);
                ShowLocalRole();
                SetRevealLock(true);
                break;

            case TimerState.Playing:
                SetRevealLock(false);
                // 3. 5초가 지나 본 게임 상태가 되면 직업 패널을 닫는다.
                if (jobDisplayCanvas != null)
                {
                    jobDisplayCanvas.SetActive(false);
                    jobDisplayPanel.SetActive(false);
                }
                break;
        }
    }

    // 직업 안내가 떠 있는 동안은 모두 멈춰 있다가, 서버 타이머가 Playing으로 바뀌는 순간 같이 풀린다.
    // 게임 종료 때처럼 행동만 막고 PlayerInput은 살려 두므로 채팅과 ESC는 그대로 된다
    private void SetRevealLock(bool locked)
    {
        _revealLock = locked;

        if (locked)
        {
            TryLockLocalPlayer();
            return;
        }

        if (_revealGate == null) return;

        _revealGate.SetUIOpen(false);
        if (_revealGate.TryGetComponent(out PlayerMovement movement)) movement.ResyncMoveInput();
        _revealGate = null;
    }

    private void TryLockLocalPlayer()
    {
        NetworkObject player = LocalPlayerInput.Local;
        if (player == null) return;

        _revealGate = PlayerActionGate.GetOrAdd(player.gameObject);
        _revealGate.SetUIOpen(true);
    }

    /// <summary>결과 화면이 떠 있는지. 떠 있는 동안은 ESC 메뉴를 열지 않는다</summary>
    public bool IsResultShown => resultPanel.activeSelf;

    // 직업 안내 중에 내 플레이어가 늦게 생겨도 막히도록 다시 시도한다
    private void Update()
    {
        if (_revealLock && _revealGate == null) TryLockLocalPlayer();

        // 결과 화면에서는 "대기방으로" 버튼을 눌러야 하므로, 채팅 등을 닫으며 커서를 잠가도 다시 푼다
        if (IsResultShown && Cursor.lockState != CursorLockMode.None)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    /// <summary>
    /// 데이터를 받아 분:초 포맷으로 바꿈
    /// </summary>
    private void ConvertAndShowTime(int previousValue, int newValue)
    {
        if (gameTimerTxt == null) return;

        int minutes = newValue / 60;
        int seconds = newValue % 60;

        gameTimerTxt.text = $"{minutes:D2}:{seconds:D2}";
    }


    /// <summary>
    /// 플레이어의 직업 번역본을 알림 텍스트에 적용합니다.
    /// </summary>
    /// <summary>
    /// 배정 목록은 서버에만 있으므로 내 플레이어의 RoleManager에서 직접 읽는다.
    /// 값이 늦게 복제될 수 있어 변경 이벤트도 같이 받는다.
    /// </summary>
    private void ShowLocalRole()
    {
        if (_localRole == null)
        {
            NetworkObject player = NetworkManager.Singleton.LocalClient?.PlayerObject;

            if (player != null && player.TryGetComponent(out RoleManager role))
            {
                _localRole = role;
                _localRole.RoleChanged += OnLocalRoleChanged;
            }
        }

        SetPlayerRoleNotification(_localRole != null && _localRole.CurrentRole == PlayerRole.Mafia);
    }

    private void OnLocalRoleChanged(PlayerRole role) => SetPlayerRoleNotification(role == PlayerRole.Mafia);

    public void SetPlayerRoleNotification(bool isMafia)
    {
        // 1. 해당 언어에 맞춰 번역된 직업 이름("마피아" 혹은 "Mafia")을 먼저 뽑아옵니다.
        string jobTableKey = isMafia ? "job_mafia" : "job_citizen";
        string translatedJobName = LocalizationSettings.StringDatabase.GetLocalizedString("InGame", jobTableKey);
        
        Color roleColor = isMafia ? mafiaColor : citizenColor;

        // 문장 안에서 직업 단어만 색을 입혀 눈에 먼저 들어오게 한다
        _argsBuffer[0] = $"<color=#{ColorUtility.ToHtmlStringRGB(roleColor)}>{translatedJobName}</color>";

        // 인자를 직접 넘겨야 {0}이 치환된다
        jobNotificationText.text = jobNotificationString.GetLocalizedString(_argsBuffer);

        if (roleAccent != null) roleAccent.color = roleColor;

        LocalizedString descriptionString = isMafia ? mafiaDesString : citizenDesString;
        jobDescriptionText.text = descriptionString.GetLocalizedString();
    }

    public void ShowResult(PlayerRole winner, float cleanRatio, float trash, float box, float paint, float citizenWinCleanRatio)
    {
        CloseOpenModals();

        bool mafiaWon = winner == PlayerRole.Mafia;
        winnerText.StringReference.TableEntryReference = mafiaWon ? "result_mafia_win" : "result_citizen_win";
        winnerText.GetComponent<TMP_Text>().color = mafiaWon ? mafiaColor : citizenColor;
        SetArgs(contaminationText, Percent(cleanRatio), Percent(citizenWinCleanRatio));
        SetArgs(breakdownText, Percent(trash), Percent(box), Percent(paint));

        jobDisplayCanvas.SetActive(true);
        if (resultBackdrop != null) resultBackdrop.SetActive(MapInfo.Instance == null || !MapInfo.Instance.HasPodium);
        returnButton.SetActive(IsServer);
        waitHostText.SetActive(!IsServer);
        resultPanel.SetActive(true);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private const string TABLE = "InGame";
    private LocalizeStringEvent _mafiaReveal;

    /// <summary>
    /// 승리 문구 바로 아래에 이번 판의 마피아 명단을 보여 준다. 마피아 수는 판마다 다르므로 이름을 이어 붙이고, 길면 줄이 바뀐다.
    /// 내가 마피아였으면 "(나)", 판 도중 나간 마피아는 "(나감)"이 붙는다 (나간 사람은 id가 ulong.MaxValue).
    /// 결과 문구와 같은 모양이 되도록 기여도 문구를 복제해서 쓰므로 씬에 따로 둘 필요가 없다.
    /// </summary>
    public void ShowMafiaReveal(ulong[] mafiaIds, Unity.Collections.FixedString64Bytes[] mafiaNames)
    {
        if (mafiaNames == null || mafiaNames.Length == 0)
        {
            if (_mafiaReveal != null) _mafiaReveal.gameObject.SetActive(false);
            return;
        }

        if (_mafiaReveal == null)
        {
            GameObject line = Instantiate(contaminationText.gameObject, contaminationText.transform.parent);
            line.name = "MafiaReveal";
            line.transform.SetSiblingIndex(winnerText.transform.GetSiblingIndex() + 1);
            _mafiaReveal = line.GetComponent<LocalizeStringEvent>();
            _mafiaReveal.StringReference.SetReference(TABLE, "result_mafia_reveal");
            TMP_Text tmp = line.GetComponent<TMP_Text>();
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.richText = true;

            // 한 줄이 늘어난 만큼 패널을 키운다 (세로 레이아웃이 줄을 나눠 담는다)
            var panelRect = (RectTransform)resultPanel.transform;
            panelRect.sizeDelta += new Vector2(0f, mafiaRevealHeight);
        }

        string me = LocalizationSettings.StringDatabase.GetLocalizedString(TABLE, "result_mafia_me");
        string left = LocalizationSettings.StringDatabase.GetLocalizedString(TABLE, "result_mafia_left");
        string color = ColorUtility.ToHtmlStringRGB(mafiaColor);
        ulong localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue;

        var names = new System.Text.StringBuilder();
        for (int i = 0; i < mafiaNames.Length; i++)
        {
            if (i > 0) names.Append(", ");
            names.Append($"<color=#{color}>{mafiaNames[i]}</color>");
            ulong id = mafiaIds != null && i < mafiaIds.Length ? mafiaIds[i] : ulong.MaxValue;
            if (id == ulong.MaxValue) names.Append($" ({left})");
            else if (id == localId) names.Append($" ({me})");
        }

        _mafiaReveal.gameObject.SetActive(true);
        SetArgs(_mafiaReveal, names.ToString());
    }

    // 결과 화면을 가리지 않도록 열려 있던 창을 모두 닫는다. 닫으면서 커서를 잠그므로 결과 화면을 띄운 뒤 다시 푼다
    private static void CloseOpenModals()
    {
        PauseMenuController menu = PauseMenuController.Instance;
        if (menu != null && menu.IsOpen) menu.Resume();

        SettingsUIController settings = SettingsUIController.Instance;
        if (settings != null && settings.IsOpen) settings.Close();

        ChatUIController chat = ChatUIController.Instance;
        if (chat != null && chat.IsOpen) chat.Close();

        InstructionSheetUI sheet = InstructionSheetUI.Instance;
        if (sheet != null && sheet.IsOpen) sheet.Close();
    }

    private static int Percent(float ratio) => Mathf.RoundToInt(ratio * 100f);

    private static void SetArgs(LocalizeStringEvent text, params object[] args)
    {
        text.StringReference.Arguments = args;
        text.RefreshString();
    }
}
